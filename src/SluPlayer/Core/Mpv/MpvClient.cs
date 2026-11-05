using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace SluPlayer.Core.Mpv;

public sealed class MpvException(string message, int error = 0) : Exception(message)
{
    public int Error { get; } = error;
}

public readonly record struct MpvEndFileInfo(MpvEndFileReason Reason, int Error, long PlaylistEntryId)
{
    public string ErrorText => Error < 0 ? MpvNative.ErrorString(Error) : string.Empty;
}

/// <summary>
/// Owns one mpv handle and its event loop thread. All callbacks/events are raised on the
/// event thread; callers marshal to the UI thread themselves.
/// </summary>
public sealed unsafe class MpvClient : IDisposable
{
    private sealed record Observer(string Name, MpvFormat Format, Action<object?> Callback);

    private nint _handle;
    private nint _eventHandle;
    private Thread? _eventThread;
    private volatile bool _stopRequested;
    private int _disposed;
    private long _nextReplyId = 1;
    private readonly ConcurrentDictionary<ulong, Observer> _observers = new();
    private readonly ConcurrentDictionary<ulong, Action<int, object?>> _commandCallbacks = new();
    private readonly string _name;

    public event Action<long>? StartFile;
    public event Action<MpvEndFileInfo>? EndFile;
    public event Action? FileLoaded;
    public event Action? PlaybackRestart;
    /// <summary>The core entered idle mode (deprecated mpv event; also the first sign that its playback loop runs).</summary>
    public event Action? Idle;
    /// <summary>mpv dropped events because this client's queue was full (property values are not lost).</summary>
    public event Action? QueueOverflow;
    public event Action? Shutdown;
    public event Action<MpvLogLevel, string, string>? LogMessage;

    public MpvClient(string name = "mpv")
    {
        _name = name;
        _handle = MpvNative.mpv_create();
        if (_handle == 0)
            throw new MpvException("mpv_create failed (libmpv-2.dll could not create a context)");
    }

    public bool IsInitialized { get; private set; }
    public bool IsAlive => _handle != 0 && !_stopRequested;

    public static uint ApiVersion => MpvNative.mpv_client_api_version();

    // ---- setup ---------------------------------------------------------

    /// <summary>Sets an option, returning false instead of throwing (for options that may not exist in every build).</summary>
    public bool TrySetOption(string name, string value)
        => MpvNative.mpv_set_option_string(_handle, name, value) >= 0;

    /// <summary>
    /// Sets a key/value-list option (script-opts, ytdl-raw-options, ...) from a map. Passing an
    /// MPV_FORMAT_NODE_MAP avoids mpv's string escaping rules (values may contain ',' ':' '\').
    /// </summary>
    public bool TrySetOptionMap(string name, IReadOnlyDictionary<string, string> map)
    {
        int n = map.Count;
        var values = (MpvNode*)NativeMemory.AllocZeroed((nuint)Math.Max(1, n), (nuint)sizeof(MpvNode));
        var keys = (nint*)NativeMemory.AllocZeroed((nuint)Math.Max(1, n), (nuint)sizeof(nint));
        var list = (MpvNodeList*)NativeMemory.AllocZeroed(1, (nuint)sizeof(MpvNodeList));
        try
        {
            int i = 0;
            foreach (var kv in map)
            {
                keys[i] = Marshal.StringToCoTaskMemUTF8(kv.Key);
                values[i].Format = MpvFormat.String;
                values[i].String = Marshal.StringToCoTaskMemUTF8(kv.Value);
                i++;
            }
            list->Num = n;
            list->Values = (nint)values;
            list->Keys = (nint)keys;
            var node = new MpvNode { Format = MpvFormat.NodeMap, List = (nint)list };
            return MpvNative.mpv_set_option(_handle, name, MpvFormat.Node, &node) >= 0;
        }
        finally
        {
            for (int i = 0; i < n; i++)
            {
                if (keys[i] != 0) Marshal.FreeCoTaskMem(keys[i]);
                if (values[i].String != 0) Marshal.FreeCoTaskMem(values[i].String);
            }
            NativeMemory.Free(values);
            NativeMemory.Free(keys);
            NativeMemory.Free(list);
        }
    }

    public void RequestLogMessages(string minLevel)
        => MpvNative.mpv_request_log_messages(_handle, minLevel);

    public void Initialize()
    {
        int r = MpvNative.mpv_initialize(_handle);
        if (r < 0) throw new MpvException($"mpv_initialize failed: {MpvNative.ErrorString(r)}", r);
        IsInitialized = true;
        _eventHandle = _handle;
        _eventThread = new Thread(EventLoop)
        {
            IsBackground = true,
            Name = $"mpv-events ({_name})",
            Priority = ThreadPriority.AboveNormal,
        };
        _eventThread.Start();
    }

    // ---- commands ------------------------------------------------------

    /// <summary>Queues a command without blocking the caller. The optional callback runs on the event thread.</summary>
    public void CommandAsync(Action<int, object?>? callback, params string[] args)
    {
        if (!IsAlive) return;
        ulong id = (ulong)Interlocked.Increment(ref _nextReplyId);
        if (callback != null) _commandCallbacks[id] = callback;
        var arr = MpvNative.AllocUtf8Array(args);
        try
        {
            int r = MpvNative.mpv_command_async(_handle, id, arr);
            if (r < 0)
            {
                // Not even queued (e.g. MPV_ERROR_EVENT_QUEUE_FULL): never silent.
                Services.Log.Warn($"mpv command '{(args.Length > 0 ? args[0] : "")}' rejected: {MpvNative.ErrorString(r)}");
                if (callback != null && _commandCallbacks.TryRemove(id, out var cb)) cb(r, null);
            }
        }
        finally { MpvNative.FreeUtf8Array(arr); }
    }

    public void CommandAsync(params string[] args) => CommandAsync(null, args);

    // ---- properties ----------------------------------------------------

    public int SetProperty(string name, string value)
        => IsAlive ? MpvNative.mpv_set_property_string(_handle, name, value) : (int)MpvError.Uninitialized;

    /// <summary>Non-blocking property set (value copied immediately by mpv).</summary>
    public void SetPropertyAsync(string name, string value)
    {
        if (!IsAlive) return;
        nint str = Marshal.StringToCoTaskMemUTF8(value);
        try { MpvNative.mpv_set_property_async(_handle, 0, name, MpvFormat.String, &str); }
        finally { Marshal.FreeCoTaskMem(str); }
    }

    public void SetPropertyAsync(string name, bool value)
    {
        if (!IsAlive) return;
        int v = value ? 1 : 0;
        MpvNative.mpv_set_property_async(_handle, 0, name, MpvFormat.Flag, &v);
    }

    public void SetPropertyAsync(string name, long value)
    {
        if (!IsAlive) return;
        long v = value;
        MpvNative.mpv_set_property_async(_handle, 0, name, MpvFormat.Int64, &v);
    }

    public void SetPropertyAsync(string name, double value)
    {
        if (!IsAlive) return;
        double v = value;
        MpvNative.mpv_set_property_async(_handle, 0, name, MpvFormat.Double, &v);
    }

    public string? GetPropertyString(string name)
    {
        if (!IsAlive) return null;
        nint p = MpvNative.mpv_get_property_string(_handle, name);
        if (p == 0) return null;
        try { return Marshal.PtrToStringUTF8(p); }
        finally { MpvNative.mpv_free(p); }
    }

    public double? GetPropertyDouble(string name)
    {
        if (!IsAlive) return null;
        double v;
        return MpvNative.mpv_get_property(_handle, name, MpvFormat.Double, &v) >= 0 ? v : null;
    }

    public long? GetPropertyLong(string name)
    {
        if (!IsAlive) return null;
        long v;
        return MpvNative.mpv_get_property(_handle, name, MpvFormat.Int64, &v) >= 0 ? v : null;
    }

    public object? GetPropertyNode(string name)
    {
        if (!IsAlive) return null;
        MpvNode node;
        if (MpvNative.mpv_get_property(_handle, name, MpvFormat.Node, &node) < 0) return null;
        try { return MpvNodeConverter.ToObject(&node); }
        finally { MpvNative.mpv_free_node_contents(&node); }
    }

    /// <summary>Observes a property. The callback runs on the event thread with the converted value (null if unavailable).</summary>
    public ulong ObserveProperty(string name, MpvFormat format, Action<object?> callback)
    {
        ulong id = (ulong)Interlocked.Increment(ref _nextReplyId);
        _observers[id] = new Observer(name, format, callback);
        int r = MpvNative.mpv_observe_property(_handle, id, name, format);
        if (r < 0)
        {
            _observers.TryRemove(id, out _);
            throw new MpvException($"observe '{name}': {MpvNative.ErrorString(r)}", r);
        }
        return id;
    }

    // ---- hooks ---------------------------------------------------------

    private readonly ConcurrentDictionary<ulong, Action> _hooks = new();

    /// <summary>
    /// Registers a synchronous hook handler (e.g. "on_load"). The handler runs on the event thread while the
    /// core waits; mpv_hook_continue is called automatically afterwards. Keep handlers fast.
    /// </summary>
    public void AddHook(string name, int priority, Action handler)
    {
        ulong id = (ulong)Interlocked.Increment(ref _nextReplyId);
        _hooks[id] = handler;
        int r = MpvNative.mpv_hook_add(_handle, id, name, priority);
        if (r < 0)
        {
            _hooks.TryRemove(id, out _);
            throw new MpvException($"hook '{name}': {MpvNative.ErrorString(r)}", r);
        }
    }

    // ---- event loop ----------------------------------------------------

    private void EventLoop()
    {
        // Dispose() zeroes _handle; the loop keeps its own copy until it exits (Dispose joins first).
        while (!_stopRequested)
        {
            MpvEvent* ev = MpvNative.mpv_wait_event(_eventHandle, -1);
            if (ev == null || _stopRequested) break;
            if (ev->EventId == MpvEventId.None) continue;
            try
            {
                if (!Dispatch(ev)) break;
            }
            catch (Exception ex)
            {
                // Never let a handler exception kill the event thread.
                LogMessage?.Invoke(MpvLogLevel.Error, "sluplayer", $"event handler failed: {ex}");
            }
        }
    }

    /// <returns>false when the loop must stop (core shutdown).</returns>
    private bool Dispatch(MpvEvent* ev)
    {
        switch (ev->EventId)
        {
            case MpvEventId.Shutdown:
                _stopRequested = true;
                Shutdown?.Invoke();
                return false;

            case MpvEventId.PropertyChange:
            {
                if (_observers.TryGetValue(ev->ReplyUserdata, out var obs))
                {
                    var prop = (MpvEventProperty*)ev->Data;
                    object? value = prop == null ? null : MpvNodeConverter.ReadData(prop->Format, prop->Data);
                    obs.Callback(value);
                }
                break;
            }

            case MpvEventId.CommandReply:
            {
                if (_commandCallbacks.TryRemove(ev->ReplyUserdata, out var cb))
                {
                    object? result = null;
                    if (ev->Error >= 0 && ev->Data != 0)
                        result = MpvNodeConverter.ToObject(&((MpvEventCommand*)ev->Data)->Result);
                    cb(ev->Error, result);
                }
                break;
            }

            case MpvEventId.LogMessage:
            {
                var msg = (MpvEventLogMessage*)ev->Data;
                if (msg != null && LogMessage != null)
                {
                    LogMessage(msg->LogLevel,
                        Marshal.PtrToStringUTF8(msg->Prefix) ?? string.Empty,
                        (Marshal.PtrToStringUTF8(msg->Text) ?? string.Empty).TrimEnd('\n'));
                }
                break;
            }

            case MpvEventId.StartFile:
            {
                var sf = (MpvEventStartFile*)ev->Data;
                StartFile?.Invoke(sf == null ? 0 : sf->PlaylistEntryId);
                break;
            }

            case MpvEventId.EndFile:
            {
                var ef = (MpvEventEndFile*)ev->Data;
                if (ef != null)
                    EndFile?.Invoke(new MpvEndFileInfo(ef->Reason, ef->Error, ef->PlaylistEntryId));
                break;
            }

            case MpvEventId.Hook:
            {
                var hk = (MpvEventHook*)ev->Data;
                if (hk != null)
                {
                    try
                    {
                        if (_hooks.TryGetValue(ev->ReplyUserdata, out var handler))
                            handler();
                    }
                    finally
                    {
                        // Always continue, or the core would hang waiting for us.
                        MpvNative.mpv_hook_continue(_eventHandle, hk->Id);
                    }
                }
                break;
            }

            case MpvEventId.FileLoaded: FileLoaded?.Invoke(); break;
            case MpvEventId.PlaybackRestart: PlaybackRestart?.Invoke(); break;
            case MpvEventId.Idle: Idle?.Invoke(); break;
            case MpvEventId.QueueOverflow:
                Services.Log.Warn("mpv event queue overflowed; some events were dropped");
                QueueOverflow?.Invoke();
                break;
        }
        return true;
    }

    // ---- shutdown ------------------------------------------------------

    /// <summary>
    /// Stops the event loop and destroys the core. Blocks until mpv has shut down, so call it from a
    /// thread that does not need to pump the UI (see <see cref="DisposeAsync"/>).
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stopRequested = true; // new API calls bail out from here on (IsAlive)
        nint h = _handle;
        if (h == 0) return;
        MpvNative.mpv_wakeup(h);
        // Event handlers must never block on the UI thread (use BeginInvoke), so this join is short.
        bool joined = _eventThread == null || _eventThread == Thread.CurrentThread
                      || _eventThread.Join(TimeSpan.FromSeconds(5));
        _handle = 0;
        // If the event thread is stuck inside mpv_wait_event we must not free the handle under it;
        // leaking is the lesser evil (this only happens at process exit).
        if (joined) MpvNative.mpv_terminate_destroy(h);
        _observers.Clear();
        _commandCallbacks.Clear();
    }

    /// <summary>Destroys mpv on a worker thread so the UI thread keeps pumping messages (mpv's video window is a child of ours).</summary>
    public Task DisposeAsync() => Task.Run(Dispose);
}

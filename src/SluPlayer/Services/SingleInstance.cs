using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using SluPlayer.Core.Interop;

namespace SluPlayer.Services;

/// <summary>
/// Single-instance support: the first process owns a mutex and a named pipe; later processes forward
/// their command-line arguments through the pipe and exit.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    // Per user, and per data folder so portable copies (tests, USB installs) don't capture each other.
    private static readonly string Suffix = Environment.UserName.Replace('\\', '_') + "." +
        Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(
            System.Text.Encoding.UTF8.GetBytes(AppPaths.DataDir.ToLowerInvariant())))[..10];
    private readonly string _mutexName = $@"Local\SluPlayer.Instance.{Suffix}";
    private readonly string _pipeName = $"SluPlayer.Pipe.{Suffix}";
    private Mutex? _mutex;
    private CancellationTokenSource? _cts;
    private readonly object _gate = new();
    private readonly List<string[]> _backlog = new();
    private Action<string[]>? _handler;

    /// <summary>
    /// Sets the receiver of forwarded arguments (called on a background thread). Arguments that arrived before
    /// (the server starts as soon as the mutex is won, long before the main window exists) are delivered now, in order.
    /// </summary>
    public void SetHandler(Action<string[]> handler)
    {
        lock (_gate)
        {
            _handler = handler;
            foreach (var args in _backlog) handler(args);
            _backlog.Clear();
        }
    }

    private void Deliver(string[] args)
    {
        lock (_gate)
        {
            if (_handler == null) _backlog.Add(args);
            else _handler(args);
        }
    }

    public bool TryAcquire()
    {
        _mutex = new Mutex(initiallyOwned: true, _mutexName, out bool createdNew);
        if (!createdNew)
        {
            _mutex.Dispose();
            _mutex = null;
        }
        return createdNew;
    }

    /// <summary>Sends args to the running instance. Returns false if it could not be reached.</summary>
    public bool Forward(string[] args)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out);
            client.Connect(3000);
            // Let the primary instance bring itself to the foreground, unless the caller (a script or test) said
            // --noactivate: then nothing the primary does for this command may take the focus from the user's app.
            if (!args.Contains("--noactivate", StringComparer.OrdinalIgnoreCase))
                Win32.AllowSetForegroundWindow(-1 /* ASFW_ANY */);
            var payload = JsonSerializer.SerializeToUtf8Bytes(args);
            client.Write(payload, 0, payload.Length);
            client.Flush();
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"single-instance forward failed: {ex.Message}");
            return false;
        }
    }

    public void StartServer()
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                    using var ms = new MemoryStream();
                    await server.CopyToAsync(ms, token).ConfigureAwait(false);
                    var args = JsonSerializer.Deserialize<string[]>(ms.ToArray()) ?? Array.Empty<string>();
                    Deliver(args);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Log.Warn($"single-instance server error: {ex.Message}");
                    await Task.Delay(200, CancellationToken.None).ConfigureAwait(false);
                }
            }
        }, token);
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try { _mutex?.ReleaseMutex(); } catch { }
        _mutex?.Dispose();
    }
}

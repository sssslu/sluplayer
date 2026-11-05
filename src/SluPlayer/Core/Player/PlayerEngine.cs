using System.Globalization;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using SluPlayer.Core.Mpv;
using SluPlayer.Services;
using SluPlayer.Services.Settings;

namespace SluPlayer.Core.Player;

public sealed class EndFileEventArgs(MpvEndFileReason reason, string? errorText, string? path) : EventArgs
{
    public MpvEndFileReason Reason { get; } = reason;
    public string? ErrorText { get; } = errorText;
    public string? Path { get; } = path;
}

/// <summary>
/// UI-thread facade over libmpv: observable playback state + playback commands.
/// All public members must be used from the UI thread; mpv callbacks are marshalled here.
/// </summary>
public sealed class PlayerEngine : ObservableObject
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly Dispatcher _ui;
    private readonly AppSettings _settings;
    private MpvClient? _mpv;
    private readonly DispatcherTimer _positionTimer;
    private double _latestTimePos;
    private int _timePosDirty;
    private string? _loadedPath;
    private double _resumedAt;
    private DateTime _lastHistoryWrite;
    private bool _firstRestartPending;
    private string? _lastStreamError;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, string> _entryPaths = new();

    public event EventHandler? FileStarted;
    public event EventHandler? FileLoaded;
    /// <summary>The first frame of a file is up: video size and tracks are final (auto-fit, etc.).</summary>
    public event EventHandler? MediaReady;
    /// <summary>An open request produced nothing to play (empty folder, ...); the argument is a user-facing message.</summary>
    public event EventHandler<string>? OpenRejected;
    public event EventHandler<EndFileEventArgs>? EndFile;
    public event EventHandler? MpvShutdown;

    public PlayerEngine(Dispatcher ui, AppSettings settings)
    {
        _ui = ui;
        _settings = settings;
        _positionTimer = new DispatcherTimer(DispatcherPriority.Normal, ui) { Interval = TimeSpan.FromMilliseconds(100) };
        _positionTimer.Tick += (_, _) => FlushPosition();
        // Shuffle is a mode: new playlists are shuffled while it is on, across restarts too.
        _isShuffled = settings.Shuffle;
    }

    public MpvClient? Mpv => _mpv;
    public bool IsInitialized => _mpv?.IsInitialized == true;
    public AppSettings Settings => _settings;

    // =====================================================================
    // Observable state
    // =====================================================================

    private bool _isIdle = true;
    public bool IsIdle { get => _isIdle; private set { if (SetProperty(ref _isIdle, value)) { OnPropertyChanged(nameof(HasMedia)); UpdateDerived(); } } }
    public bool HasMedia => !_isIdle;

    private bool _isLoading;
    public bool IsLoading { get => _isLoading; private set => SetProperty(ref _isLoading, value); }

    private bool _isPaused;
    public bool IsPaused { get => _isPaused; private set { if (SetProperty(ref _isPaused, value)) OnPropertyChanged(nameof(IsPlaying)); } }
    public bool IsPlaying => HasMedia && !_isPaused;

    private bool _isBuffering;
    public bool IsBuffering { get => _isBuffering; private set => SetProperty(ref _isBuffering, value); }

    private int _bufferingPercent;
    public int BufferingPercent { get => _bufferingPercent; private set => SetProperty(ref _bufferingPercent, value); }

    private bool _eofReached;
    public bool EofReached { get => _eofReached; private set => SetProperty(ref _eofReached, value); }

    private double _position;
    public double Position { get => _position; private set => SetProperty(ref _position, value); }

    private double _duration;
    public double Duration { get => _duration; private set { if (SetProperty(ref _duration, value)) OnPropertyChanged(nameof(IsSeekable)); } }

    private bool _seekable = true;
    public bool IsSeekable => _seekable && _duration > 0;

    private double _volume = 100;
    public double Volume { get => _volume; private set => SetProperty(ref _volume, value); }

    private double _volumeMax = 150;
    public double VolumeMax { get => _volumeMax; private set => SetProperty(ref _volumeMax, value); }

    private bool _isMuted;
    public bool IsMuted { get => _isMuted; private set => SetProperty(ref _isMuted, value); }

    private double _speed = 1.0;
    public double Speed { get => _speed; private set => SetProperty(ref _speed, value); }

    private string? _mediaTitle;
    public string? MediaTitle { get => _mediaTitle; private set => SetProperty(ref _mediaTitle, value); }

    private string? _currentPath;
    public string? CurrentPath { get => _currentPath; private set { if (SetProperty(ref _currentPath, value)) OnPropertyChanged(nameof(IsNetworkStream)); } }
    public bool IsNetworkStream => _currentPath != null && MediaFormats.IsUrl(_currentPath);

    private int _videoWidth;
    public int VideoWidth { get => _videoWidth; private set => SetProperty(ref _videoWidth, value); }

    private int _videoHeight;
    public int VideoHeight { get => _videoHeight; private set => SetProperty(ref _videoHeight, value); }

    private int _sourceWidth;
    /// <summary>Display width of the decoded video (aspect + rotation applied).</summary>
    public int SourceWidth { get => _sourceWidth; private set => SetProperty(ref _sourceWidth, value); }

    private int _sourceHeight;
    public int SourceHeight { get => _sourceHeight; private set => SetProperty(ref _sourceHeight, value); }

    private bool _hasVideo;
    /// <summary>True when a real (non cover-art) video track is playing.</summary>
    public bool HasVideo { get => _hasVideo; private set { if (SetProperty(ref _hasVideo, value)) UpdateDerived(); } }

    private bool _hasAlbumArt;
    public bool HasAlbumArt { get => _hasAlbumArt; private set => SetProperty(ref _hasAlbumArt, value); }

    private bool _isAudioOnly;
    public bool IsAudioOnly { get => _isAudioOnly; private set => SetProperty(ref _isAudioOnly, value); }

    private string? _hwdecCurrent;
    public string? HwdecCurrent { get => _hwdecCurrent; private set => SetProperty(ref _hwdecCurrent, value); }

    private IReadOnlyList<MediaTrack> _videoTracks = [];
    public IReadOnlyList<MediaTrack> VideoTracks { get => _videoTracks; private set => SetProperty(ref _videoTracks, value); }

    private IReadOnlyList<MediaTrack> _audioTracks = [];
    public IReadOnlyList<MediaTrack> AudioTracks { get => _audioTracks; private set => SetProperty(ref _audioTracks, value); }

    private IReadOnlyList<MediaTrack> _subtitleTracks = [];
    public IReadOnlyList<MediaTrack> SubtitleTracks { get => _subtitleTracks; private set => SetProperty(ref _subtitleTracks, value); }

    private IReadOnlyList<ChapterInfo> _chapters = [];
    public IReadOnlyList<ChapterInfo> Chapters { get => _chapters; private set => SetProperty(ref _chapters, value); }

    private int _currentChapter = -1;
    public int CurrentChapter { get => _currentChapter; private set => SetProperty(ref _currentChapter, value); }

    private IReadOnlyList<PlaylistEntry> _playlist = [];
    public IReadOnlyList<PlaylistEntry> Playlist { get => _playlist; private set { if (SetProperty(ref _playlist, value)) OnPropertyChanged(nameof(PlaylistCount)); } }
    public int PlaylistCount => _playlist.Count;

    private int _playlistPos = -1;
    public int PlaylistPos { get => _playlistPos; private set => SetProperty(ref _playlistPos, value); }

    private LoopMode _loop;
    public LoopMode Loop { get => _loop; private set => SetProperty(ref _loop, value); }

    private bool _isShuffled;
    public bool IsShuffled { get => _isShuffled; private set => SetProperty(ref _isShuffled, value); }

    private double _subDelay;
    public double SubDelay { get => _subDelay; private set => SetProperty(ref _subDelay, value); }

    private bool _subVisible = true;
    public bool SubVisible { get => _subVisible; private set => SetProperty(ref _subVisible, value); }

    private int _videoRotate;
    public int VideoRotate { get => _videoRotate; private set => SetProperty(ref _videoRotate, value); }

    private string _aspectOverride = "no";
    public string AspectOverride { get => _aspectOverride; private set => SetProperty(ref _aspectOverride, value); }

    private IReadOnlyList<TimeRange> _bufferedRanges = [];
    public IReadOnlyList<TimeRange> BufferedRanges { get => _bufferedRanges; private set => SetProperty(ref _bufferedRanges, value); }

    private IReadOnlyDictionary<string, string> _metadata = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, string> Metadata { get => _metadata; private set { if (SetProperty(ref _metadata, value)) OnPropertyChanged(nameof(Artist)); } }
    public string? Artist => Meta("artist") ?? Meta("album_artist") ?? Meta("uploader");

    private string? Meta(string key)
        => _metadata.FirstOrDefault(kv => kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value is { Length: > 0 } v ? v : null;

    private void UpdateDerived()
    {
        IsAudioOnly = HasMedia && !IsLoading && !HasVideo;
        OnPropertyChanged(nameof(IsPlaying));
    }

    // =====================================================================
    // Initialization
    // =====================================================================

    /// <summary>Creates the mpv core rendering into <paramref name="wid"/> (a child HWND).</summary>
    public void Initialize(nint wid)
    {
        if (_mpv != null) return;
        var mpv = new MpvClient("player");
        _mpv = mpv;

        foreach (var (name, value) in MpvOptionBuilder.Build(_settings, wid))
        {
            if (!mpv.TrySetOption(name, value))
                Log.Warn($"mpv option rejected: {name}={value}");
        }
        foreach (var (name, map) in MpvOptionBuilder.BuildMaps())
        {
            if (!mpv.TrySetOptionMap(name, map))
                Log.Warn($"mpv option map rejected: {name} ({string.Join(", ", map.Keys)})");
        }

        mpv.LogMessage += OnMpvLog;
        mpv.RequestLogMessages("info");

        mpv.StartFile += _ => _ui.BeginInvoke(OnStartFile);
        // Whichever comes first, both follow the shuffle that mpv's playback loop does when it starts.
        mpv.Idle += () => _coreStarted.TrySetResult();
        mpv.StartFile += _ => _coreStarted.TrySetResult();
        mpv.FileLoaded += () => _ui.BeginInvoke(OnFileLoaded);
        mpv.EndFile += info =>
        {
            // The entry id identifies the file even when it failed before 'path' was ever loaded.
            string? path = _entryPaths.TryGetValue(info.PlaylistEntryId, out var p) ? p : _loadedPath;
            double lastPos = Volatile.Read(ref _latestTimePos);
            _ui.BeginInvoke(() => OnEndFile(info, path, lastPos));
        };
        mpv.PlaybackRestart += () => _ui.BeginInvoke(OnPlaybackRestart);
        mpv.QueueOverflow += () => _ui.BeginInvoke(ResyncAfterOverflow);
        mpv.Shutdown += () => _ui.BeginInvoke(() => MpvShutdown?.Invoke(this, EventArgs.Empty));

        // Resume support: decide the start position right before each file is opened.
        mpv.AddHook("on_load", 50, OnLoadHook);

        Observe(mpv);
        mpv.Initialize();
        Log.Info($"mpv initialized: {mpv.GetPropertyString("mpv-version")} / ffmpeg {mpv.GetPropertyString("ffmpeg-version")} / api {MpvClient.ApiVersion >> 16}.{MpvClient.ApiVersion & 0xFFFF}");

        VolumeMax = AppSettings.MaxVolume;
        Task.Run(CleanTempPlaylists);
    }

    private void OnMpvLog(MpvLogLevel level, string prefix, string text)
    {
        var line = $"[mpv/{prefix}] {text}";
        if (level <= MpvLogLevel.Error) Log.Error(line);
        else if (level <= MpvLogLevel.Warn) Log.Warn(line);
        else Log.Info(line);
    }

    private void Observe(MpvClient mpv)
    {
        void On(string name, MpvFormat fmt, Action<object?> apply)
            => mpv.ObserveProperty(name, fmt, v => _ui.BeginInvoke(() => apply(v)));

        On("idle-active", MpvFormat.Flag, v => IsIdle = v is true);
        On("pause", MpvFormat.Flag, v => IsPaused = v is true);
        On("paused-for-cache", MpvFormat.Flag, v => IsBuffering = v is true);
        On("cache-buffering-state", MpvFormat.Int64, v => BufferingPercent = (int)(v as long? ?? 0));
        On("eof-reached", MpvFormat.Flag, v => EofReached = v is true);
        On("duration", MpvFormat.Double, v => Duration = v as double? ?? 0);
        On("seekable", MpvFormat.Flag, v => { _seekable = v is not false; OnPropertyChanged(nameof(IsSeekable)); });
        On("volume", MpvFormat.Double, v => Volume = v as double? ?? Volume);
        On("volume-max", MpvFormat.Double, v => VolumeMax = v as double? ?? VolumeMax);
        On("mute", MpvFormat.Flag, v => IsMuted = v is true);
        On("speed", MpvFormat.Double, v => Speed = v as double? ?? 1);
        On("media-title", MpvFormat.String, v => MediaTitle = v as string);
        On("path", MpvFormat.String, v => CurrentPath = v as string);
        On("dwidth", MpvFormat.Int64, v => VideoWidth = (int)(v as long? ?? 0));
        On("dheight", MpvFormat.Int64, v => VideoHeight = (int)(v as long? ?? 0));
        On("hwdec-current", MpvFormat.String, v => HwdecCurrent = v as string);
        On("chapter", MpvFormat.Int64, v => CurrentChapter = (int)(v as long? ?? -1));
        On("playlist-pos", MpvFormat.Int64, v => PlaylistPos = (int)(v as long? ?? -1));
        On("sub-delay", MpvFormat.Double, v => SubDelay = v as double? ?? 0);
        On("sub-visibility", MpvFormat.Flag, v => SubVisible = v is not false);
        On("video-rotate", MpvFormat.Int64, v => VideoRotate = (int)(v as long? ?? 0));
        // As a string mpv prints the number ("-2.000000"): read the number and map it to SetAspect's labels.
        On("video-aspect-override", MpvFormat.Double, v => AspectOverride = AspectLabel(v as double? ?? -2));
        On("loop-file", MpvFormat.Node, v => UpdateLoop(fileLoop: IsLoopOn(v), null));
        On("loop-playlist", MpvFormat.Node, v => UpdateLoop(null, playlistLoop: IsLoopOn(v)));
        On("metadata", MpvFormat.Node, v => Metadata = ToStringMap(v));
        // Source display size, rotation applied.
        On("video-params", MpvFormat.Node, SetSourceSize);

        // ytdl_hook publishes yt-dlp's result here; keep the error to explain failed online streams.
        mpv.ObserveProperty("user-data/mpv/ytdl/json-subprocess-result", MpvFormat.Node, v =>
        {
            if (v is not Dictionary<string, object?> m) return;
            long status = m.GetLong("status") ?? 0;
            var stderr = m.GetString("stderr");
            string? msg = status == 0 ? null : YtDlpErrors.Translate(stderr) ?? YtDlpErrors.Translate(m.GetString("error_string"));
            if (status != 0) Log.Warn($"yt-dlp (stream) failed: status={status} {YtDlpErrors.LastErrorLine(stderr) ?? m.GetString("error_string")}");
            Volatile.Write(ref _lastStreamError, msg);
        });

        // Heavy/structured properties: parse on the event thread, publish on the UI thread.
        mpv.ObserveProperty("time-pos", MpvFormat.Double, v =>
        {
            if (v is double d)
            {
                Volatile.Write(ref _latestTimePos, d);
                Interlocked.Exchange(ref _timePosDirty, 1);
            }
        });
        mpv.ObserveProperty("track-list", MpvFormat.Node, v =>
        {
            var tracks = (v as List<object?> ?? []).Select(MediaTrack.FromNode).OfType<MediaTrack>().ToList();
            _ui.BeginInvoke(() => ApplyTracks(tracks));
        });
        mpv.ObserveProperty("chapter-list", MpvFormat.Node, v =>
        {
            var list = new List<ChapterInfo>();
            if (v is List<object?> items)
            {
                for (int i = 0; i < items.Count; i++)
                    if (items[i] is Dictionary<string, object?> m)
                        list.Add(new ChapterInfo(i, m.GetDouble("time") ?? 0, m.GetString("title") ?? ""));
            }
            _ui.BeginInvoke(() => Chapters = list);
        });
        mpv.ObserveProperty("playlist", MpvFormat.Node, v =>
        {
            var list = new List<PlaylistEntry>();
            if (v is List<object?> items)
            {
                for (int i = 0; i < items.Count; i++)
                    if (items[i] is Dictionary<string, object?> m)
                    {
                        var entry = new PlaylistEntry(i, m.GetLong("id") ?? i, m.GetString("filename") ?? "",
                            m.GetString("title"), m.GetBool("current") || m.GetBool("playing"));
                        list.Add(entry);
                        _entryPaths[entry.Id] = entry.Filename;
                    }
            }
            // Ids are never reused: forget entries that left the playlist long ago so the map stays bounded.
            if (_entryPaths.Count > list.Count + 512)
            {
                var keep = list.Select(e => e.Id).ToHashSet();
                foreach (var id in _entryPaths.Keys)
                    if (!keep.Contains(id)) _entryPaths.TryRemove(id, out _);
            }
            _ui.BeginInvoke(() => Playlist = list);
        });
        mpv.ObserveProperty("demuxer-cache-state", MpvFormat.Node, v =>
        {
            var ranges = new List<TimeRange>();
            if (v is Dictionary<string, object?> m && m.TryGetValue("seekable-ranges", out var r) && r is List<object?> list)
            {
                foreach (var item in list)
                    if (item is Dictionary<string, object?> rm)
                        ranges.Add(new TimeRange(rm.GetDouble("start") ?? 0, rm.GetDouble("end") ?? 0));
            }
            _ui.BeginInvoke(() =>
            {
                if (!RangesEqual(_bufferedRanges, ranges)) BufferedRanges = ranges;
            });
        });
    }

    /// <summary>Source display size from video-params (aspect and rotation applied).</summary>
    private void SetSourceSize(object? videoParams)
    {
        int w = 0, h = 0;
        if (videoParams is Dictionary<string, object?> m)
        {
            w = (int)(m.GetLong("dw") ?? m.GetLong("w") ?? 0);
            h = (int)(m.GetLong("dh") ?? m.GetLong("h") ?? 0);
            if ((m.GetLong("rotate") ?? 0) % 180 != 0) (w, h) = (h, w);
        }
        SourceWidth = w;
        SourceHeight = h;
    }

    private static bool RangesEqual(IReadOnlyList<TimeRange> a, IReadOnlyList<TimeRange> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (Math.Abs(a[i].Start - b[i].Start) > 0.5 || Math.Abs(a[i].End - b[i].End) > 0.5) return false;
        return true;
    }

    private static bool IsLoopOn(object? v) => v switch
    {
        bool b => b,
        string s => s is "inf" or "yes" or "force",
        long l => l > 0,
        double d => d > 0,
        _ => false,
    };

    private void UpdateLoop(bool? fileLoop, bool? playlistLoop)
    {
        if (fileLoop == true) Loop = LoopMode.File;
        else if (playlistLoop == true) Loop = LoopMode.Playlist;
        else if (fileLoop == false && Loop == LoopMode.File) Loop = LoopMode.None;
        else if (playlistLoop == false && Loop == LoopMode.Playlist) Loop = LoopMode.None;
    }

    private static IReadOnlyDictionary<string, string> ToStringMap(object? v)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (v is Dictionary<string, object?> m)
            foreach (var kv in m)
                if (kv.Value != null) map[kv.Key] = kv.Value.ToString() ?? "";
        return map;
    }

    private void ApplyTracks(List<MediaTrack> tracks)
    {
        VideoTracks = tracks.Where(t => t.Type == TrackType.Video).ToList();
        AudioTracks = tracks.Where(t => t.Type == TrackType.Audio).ToList();
        SubtitleTracks = tracks.Where(t => t.Type == TrackType.Subtitle).ToList();
        var vid = VideoTracks.FirstOrDefault(t => t.IsSelected);
        HasAlbumArt = vid != null && (vid.IsAlbumArt || vid.IsImage);
        HasVideo = vid != null && !vid.IsAlbumArt && !vid.IsImage;
        OnPropertyChanged(nameof(SelectedSubtitleId));
    }

    public long? SelectedSubtitleId => _subtitleTracks.FirstOrDefault(t => t.IsSelected)?.Id;

    // =====================================================================
    // Lifecycle events
    // =====================================================================

    /// <summary>Runs on the mpv event thread before a file is opened: applies the resume position.</summary>
    private void OnLoadHook()
    {
        _resumedAt = 0;
        var mpv = _mpv;
        if (mpv == null || !_settings.ResumePlayback) return;
        var path = mpv.GetPropertyString("path");
        if (string.IsNullOrEmpty(path)) return;
        // Respect an explicit start position (e.g. "start=" from a playlist entry).
        var explicitStart = mpv.GetPropertyString("file-local-options/start");
        if (!string.IsNullOrEmpty(explicitStart) && explicitStart != "none") return;
        var entry = HistoryService.Find(path);
        if (entry == null || entry.Position < 10) return;
        if (entry.Duration > 0 && entry.Position > entry.Duration - 15) return;
        mpv.SetProperty("file-local-options/start", entry.Position.ToString("0.###", Inv));
        _resumedAt = entry.Position;
    }

    private void OnStartFile()
    {
        IsLoading = true;
        _firstRestartPending = true;
        EofReached = false;
        Position = 0;
        Volatile.Write(ref _latestTimePos, 0);
        BufferedRanges = [];
        _positionTimer.Start();
        FileStarted?.Invoke(this, EventArgs.Empty);
    }

    private void OnFileLoaded()
    {
        _loadedPath = _mpv?.GetPropertyString("path");
        if (_siblingsPendingFor != null)
        {
            var pending = _siblingsPendingFor;
            _siblingsPendingFor = null;
            if (_loadedPath != null && SamePath(_loadedPath, pending)) AddFolderSiblingsAsync(pending);
        }
        UpdateDerived();
        if (_loadedPath != null)
        {
            HistoryService.Touch(_loadedPath, _mpv?.GetPropertyString("media-title"),
                _mpv?.GetPropertyDouble("duration") ?? 0, _resumedAt);
            _lastHistoryWrite = DateTime.UtcNow;
            _historyPos = _resumedAt;
        }
        if (_resumedAt > 0)
            ShowOsd($"이어서 재생 {Formatters.Time(_resumedAt)}", 3000);
        FileLoaded?.Invoke(this, EventArgs.Empty);
    }

    private void OnPlaybackRestart()
    {
        if (_firstRestartPending)
        {
            _firstRestartPending = false;
            IsLoading = false;
            // Property notifications trail the events: read the new file's video size directly.
            SetSourceSize(_mpv?.GetPropertyNode("video-params"));
            UpdateDerived();
            FlushPosition(force: true);
            MediaReady?.Invoke(this, EventArgs.Empty);
            return;
        }
        FlushPosition(force: true);
    }

    private void OnEndFile(MpvEndFileInfo info, string? path, double lastPos)
    {
        IsLoading = false;
        bool wasLoaded = path != null && _loadedPath != null && SamePath(path, _loadedPath);
        if (wasLoaded && info.Reason is MpvEndFileReason.Eof or MpvEndFileReason.Stop or MpvEndFileReason.Quit)
        {
            // Finished files restart from the beginning next time.
            double pos = info.Reason == MpvEndFileReason.Eof ? 0 : lastPos;
            HistoryService.Touch(path!, null, 0, pos);
        }
        string? error = null;
        if (info.Reason == MpvEndFileReason.Error)
        {
            error = info.ErrorText;
            var streamError = Volatile.Read(ref _lastStreamError);
            if (streamError != null && path != null && MediaFormats.IsUrl(path)) error = streamError;
            Log.Warn($"playback error for '{path}': {error}");
        }
        Volatile.Write(ref _lastStreamError, null);
        EndFile?.Invoke(this, new EndFileEventArgs(info.Reason, error, path));
        if (info.Reason != MpvEndFileReason.Redirect) _loadedPath = null;
    }

    /// <summary>
    /// Start/load/restart events may have been dropped (property notifications are not): re-derive the state that
    /// only those events maintain, so the UI never stays on "불러오는 중…".
    /// </summary>
    private void ResyncAfterOverflow()
    {
        var mpv = _mpv;
        if (mpv == null) return;
        var path = mpv.GetPropertyString("path");
        if (path != null && mpv.GetPropertyDouble("time-pos") != null)
        {
            _firstRestartPending = false;
            IsLoading = false;
            _loadedPath ??= path;
            SetSourceSize(mpv.GetPropertyNode("video-params"));
        }
        UpdateDerived();
    }

    private void FlushPosition(bool force = false)
    {
        if (_mpv == null) return;
        if (Interlocked.Exchange(ref _timePosDirty, 0) == 1 || force)
        {
            var p = Volatile.Read(ref _latestTimePos);
            if (Math.Abs(p - _position) >= 0.05 || force) Position = p;
        }
        if (_loadedPath != null && !IsPaused && (DateTime.UtcNow - _lastHistoryWrite).TotalSeconds > 5)
        {
            _lastHistoryWrite = DateTime.UtcNow;
            HistoryService.Touch(_loadedPath, MediaTitle, Duration, Position);
            _historyPos = Position;
        }
        if (IsIdle && !IsLoading) _positionTimer.Stop();
    }

    // =====================================================================
    // Opening media
    // =====================================================================

    private const int MaxFolderSiblings = 5000;
    private int _openSeq;      // bumped by every request that replaces the playlist
    private int _appendSeq;    // bumped by every request that appends to it
    private Task _openQueue = Task.CompletedTask;
    private CancellationTokenSource _openCts = new(); // folder scans of the requests since the last replace
    private string? _siblingsPendingFor;
    private double _historyPos; // the position this engine last stored for the loaded file
    private int _shuffleSeq;    // bumped by every shuffle toggle
    private readonly HashSet<long> _unplacedIds = new(); // entries added in their given order while shuffle was on (locked: filled on the event thread)

    /// <summary>
    /// mpv's playback loop shuffles the playlist once when it starts (with shuffle on), after its scripts have loaded:
    /// a list added before that would be shuffled a second time, and the entry already picked would then sit somewhere
    /// in the middle. Lists therefore wait for this (only right after startup).
    /// </summary>
    private readonly TaskCompletionSource _coreStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Opens one item (file, folder or URL). Local single files optionally pull in their folder siblings.</summary>
    public void Open(string pathOrUrl, bool append = false)
    {
        if (_mpv == null || string.IsNullOrWhiteSpace(pathOrUrl)) return;
        pathOrUrl = NormalizeInput(pathOrUrl);
        if (append || MightBeFolder(pathOrUrl))
        {
            OpenMany([pathOrUrl], append);
            return;
        }
        SupersedeOpens();
        ReleaseLoadedFile(pathOrUrl);
        if (MediaFormats.IsUrl(pathOrUrl)) RefreshYtdlCookiesIfChanged();
        _mpv.CommandAsync("loadfile", pathOrUrl, "replace");
        // keep-open leaves the player paused at the end of the last file; a newly opened file should play.
        _mpv.SetPropertyAsync("pause", false);
        // Siblings are added only once this file actually loads: if it fails, mpv must not silently
        // continue with some other file from the folder.
        _siblingsPendingFor = !MediaFormats.IsUrl(pathOrUrl)
            && MediaFormats.Classify(pathOrUrl) is MediaKind.Video or MediaKind.Audio
            ? pathOrUrl : null;
    }

    /// <summary>
    /// Replaces (or appends to) the playlist with several items and plays the first. Folders are scanned off the
    /// UI thread; requests are applied in the order they were made.
    /// </summary>
    public void OpenMany(IReadOnlyList<string> items, bool append = false)
    {
        if (_mpv == null) return;
        var list = items.Where(i => !string.IsNullOrWhiteSpace(i)).Select(NormalizeInput).ToList();
        if (list.Count == 0) return;
        if (list.Count == 1 && !append && !MightBeFolder(list[0])) { Open(list[0]); return; }
        // An explicit selection (Explorer multi-select, drop, dialog) is the playlist: no folder siblings.
        _siblingsPendingFor = null;
        if (append) _appendSeq++;
        else SupersedeOpens();
        if (list.Any(MediaFormats.IsUrl)) RefreshYtdlCookiesIfChanged();
        _ = OpenManyAsync(list, append, _openSeq, _openCts.Token);
    }

    /// <summary>
    /// A request that replaces the playlist (or clears it): requests made before it are dropped when their folder scan
    /// ends, and the scans are cancelled. Nothing queued later waits for them.
    /// </summary>
    private void SupersedeOpens()
    {
        _openSeq++;
        _openQueue = Task.CompletedTask;
        _openCts.Cancel();
        _openCts = new CancellationTokenSource();
        lock (_unplacedIds) _unplacedIds.Clear();
    }

    /// <summary>
    /// Open() replaces the loaded file: its position is recorded now rather than at its end-file event. mpv reports that
    /// end only after the new open has started, and writing then would undo a history change made for this open
    /// ('처음부터 재생' resets the position of the file it reopens to 0): such a change is kept.
    /// </summary>
    private void ReleaseLoadedFile(string next)
    {
        if (_loadedPath == null) return;
        bool changedForThisOpen = SamePath(_loadedPath, next) && HistoryService.Find(_loadedPath)?.Position != _historyPos;
        if (!changedForThisOpen)
            HistoryService.Touch(_loadedPath, null, 0, EofReached ? 0 : Volatile.Read(ref _latestTimePos));
        _loadedPath = null;
    }

    private async Task OpenManyAsync(List<string> items, bool append, int seq, CancellationToken cancel)
    {
        var previous = _openQueue;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _openQueue = done.Task;
        try
        {
            var entries = items.Any(MightBeFolder) ? await Task.Run(() => ExpandFolders(items, cancel), cancel) : items;
            // An append keeps its place behind the requests before it; a replace supersedes them, so it doesn't wait.
            if (append) await previous;
            if (!_coreStarted.Task.IsCompleted) await Task.WhenAny(_coreStarted.Task, Task.Delay(3000));
            if (_mpv == null || seq != _openSeq) return; // a newer replace (or a clear) superseded this request
            if (entries.Count == 0)
            {
                OpenRejected?.Invoke(this, "재생할 파일 없음");
                return;
            }
            // With shuffle on, a list that is opened plays in random order; items added to the playlist keep their order.
            var loading = LoadEntries(entries, append ? "append-play" : "replace", shuffle: !append && IsShuffled);
            if (!append) _mpv.SetPropertyAsync("pause", false);
            await loading;
        }
        catch (OperationCanceledException)
        {
            // superseded while scanning
        }
        catch (Exception ex)
        {
            Log.Error("open failed", ex);
        }
        finally
        {
            done.SetResult();
        }
    }

    /// <summary>True when an item has no media extension and is not a URL, so it may be a folder to expand.</summary>
    private static bool MightBeFolder(string item)
        => !MediaFormats.IsUrl(item) && MediaFormats.Classify(item) == MediaKind.Unknown;

    /// <summary>Folders become their media files (recursive, natural order); everything else is kept as is. Thread pool.</summary>
    private static List<string> ExpandFolders(List<string> items, CancellationToken cancel)
    {
        var result = new List<string>();
        foreach (var item in items)
        {
            cancel.ThrowIfCancellationRequested();
            if (MightBeFolder(item) && Directory.Exists(item)) result.AddRange(MediaFormats.EnumerateFolder(item, recursive: true, cancel));
            else result.Add(item);
        }
        return result;
    }

    /// <summary>
    /// Adds entries to mpv's playlist. <paramref name="mode"/>: replace | append | append-play | insert-at.
    /// Longer lists go through one 'loadlist' of a temporary playlist file: one async 'loadfile' per entry would
    /// overflow mpv's 1000-slot event queue for big folders, and mpv then drops events. A list to play in random order
    /// (<paramref name="shuffle"/>, only while shuffle is on) goes that way too: mpv shuffles what 'loadlist' adds (its
    /// 'shuffle' option) before it picks the entry to play, and remembers the natural order for 'playlist-unshuffle'.
    /// So while shuffle is on, a list that keeps its given order never goes through 'loadlist'.
    /// Completes once every entry was sent to mpv.
    /// </summary>
    private Task LoadEntries(IReadOnlyList<string> entries, string mode, int insertAt = 0, bool shuffle = false)
    {
        var mpv = _mpv;
        if (mpv == null || entries.Count == 0) return Task.CompletedTask;
        bool keepOrder = IsShuffled && !shuffle;
        shuffle &= entries.Count > 1;
        if ((entries.Count > 16 || shuffle) && !keepOrder && entries.All(FitsTempPlaylist) && WriteTempPlaylist(entries) is { } listFile)
        {
            void Delete(int err, object? _)
            {
                if (err < 0) Log.Warn($"loadlist failed: {MpvNative.ErrorString(err)}");
                try { File.Delete(listFile); } catch { }
            }
            if (mode == "insert-at") mpv.CommandAsync(Delete, "loadlist", listFile, "insert-at", insertAt.ToString(Inv));
            else mpv.CommandAsync(Delete, "loadlist", listFile, mode);
            return Task.CompletedTask;
        }
        if (shuffle)
        {
            // Entries that can't go through a playlist file are shuffled here (unshuffling can't restore their order).
            var order = entries.ToArray();
            Random.Shared.Shuffle(order);
            entries = order;
        }
        // Otherwise one 'loadfile' per entry.
        var commands = new List<string[]>(entries.Count);
        for (int i = 0; i < entries.Count; i++)
            commands.Add(mode == "insert-at"
                ? ["loadfile", entries[i], "insert-at", (insertAt + i).ToString(Inv)]
                : ["loadfile", entries[i], mode == "replace" && i > 0 ? "append" : mode]);
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int seq = _openSeq;
        SendInChunks(mpv, commands, 0, () => seq != _openSeq, keepOrder ? NoteUnplaced : null, () => sent.TrySetResult());
        return sent.Task;
    }

    /// <summary>Remembers an entry 'playlist-unshuffle' has no place for (loadfile reply, event thread).</summary>
    private void NoteUnplaced(int err, object? result)
    {
        if (err >= 0 && result is Dictionary<string, object?> r && r.GetLong("playlist_entry_id") is long id)
            lock (_unplacedIds) _unplacedIds.Add(id);
    }

    private const int LoadChunk = 200;

    /// <summary>
    /// Sends commands in order. Each one holds a slot of mpv's event queue until its reply is read, so at most
    /// <see cref="LoadChunk"/> are sent at a time: the next chunk follows the reply to the last command of this one.
    /// Sending stops once <paramref name="stale"/> returns true (a newer request made the rest pointless).
    /// <paramref name="reply"/> gets every reply (event thread); <paramref name="done"/> runs once everything was sent
    /// or sending stopped.
    /// </summary>
    private void SendInChunks(MpvClient mpv, IReadOnlyList<string[]> commands, int start, Func<bool> stale,
        Action<int, object?>? reply, Action done)
    {
        if (_mpv != mpv || !mpv.IsAlive || stale()) { done(); return; }
        int end = Math.Min(commands.Count, start + LoadChunk);
        for (int i = start; i < end; i++)
        {
            Action<int, object?>? callback = i < end - 1 || end == commands.Count ? reply : (err, result) =>
            {
                reply?.Invoke(err, result);
                _ui.BeginInvoke(() => SendInChunks(mpv, commands, end, stale, reply, done));
            };
            mpv.CommandAsync(callback, commands[i]);
        }
        if (end == commands.Count) done();
    }

    /// <summary>
    /// mpv opens local files and http(s) links listed in a playlist file; other schemes (udp, ytdl, ...) may be refused
    /// there by its stream-origin rules, so lists with those are added one entry at a time.
    /// </summary>
    private static bool FitsTempPlaylist(string entry)
        => !MediaFormats.IsUrl(entry)
           || entry.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
           || entry.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    private static string TempPlaylistDir => Path.Combine(AppPaths.LocalDir, "tmp");

    private static string? WriteTempPlaylist(IReadOnlyList<string> entries)
    {
        try
        {
            Directory.CreateDirectory(TempPlaylistDir);
            var file = Path.Combine(TempPlaylistDir, $"open-{Guid.NewGuid():N}.m3u8");
            var sb = new System.Text.StringBuilder("#EXTM3U\n");
            foreach (var e in entries) sb.Append(MediaFormats.IsUrl(e) ? e : Path.GetFullPath(e)).Append('\n');
            File.WriteAllText(file, sb.ToString(), new System.Text.UTF8Encoding(false));
            return file;
        }
        catch (Exception ex)
        {
            Log.Warn("temp playlist: " + ex.Message);
            return null;
        }
    }

    /// <summary>Removes temporary playlists left behind by a crash (thread pool, at startup).</summary>
    private static void CleanTempPlaylists()
    {
        try
        {
            if (!Directory.Exists(TempPlaylistDir)) return;
            foreach (var f in Directory.EnumerateFiles(TempPlaylistDir, "open-*.m3u8"))
                if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddMinutes(-10)) File.Delete(f);
        }
        catch { }
    }

    /// <summary>Puts the other files of the folder around a file the user opened (folder scan off the UI thread).</summary>
    private async void AddFolderSiblingsAsync(string file)
    {
        int seq = _openSeq, appendSeq = _appendSeq;
        var kind = MediaFormats.Classify(file);
        List<string> siblings;
        int idx;
        try
        {
            (siblings, idx) = await Task.Run(() =>
            {
                var dir = Path.GetDirectoryName(Path.GetFullPath(file));
                if (dir == null) return (new List<string>(), -1);
                var list = MediaFormats.EnumerateFolder(dir, recursive: false).Where(f => MediaFormats.Classify(f) == kind).ToList();
                return (list, list.FindIndex(f => SamePath(f, file)));
            });
        }
        catch (Exception ex)
        {
            Log.Warn("folder scan failed: " + ex.Message);
            return;
        }
        // Skip if the user opened or queued something else meanwhile.
        if (_mpv == null || seq != _openSeq || appendSeq != _appendSeq || _loadedPath == null || !SamePath(_loadedPath, file)) return;
        if (idx < 0 || siblings.Count <= 1 || siblings.Count > MaxFolderSiblings) return;
        if (IsShuffled)
        {
            // The opened file plays first; the rest of the folder follows in random order (one batch, shuffled by mpv).
            siblings.RemoveAt(idx);
            _ = LoadEntries(siblings, "append", shuffle: true);
            return;
        }
        _ = LoadEntries(siblings.GetRange(0, idx), "insert-at", 0);
        _ = LoadEntries(siblings.GetRange(idx + 1, siblings.Count - idx - 1), "append");
    }

    private static bool SamePath(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
    }

    private static string NormalizeInput(string s)
    {
        s = s.Trim().Trim('"');
        if (s.StartsWith("file:///", StringComparison.OrdinalIgnoreCase))
        {
            try { s = new Uri(s).LocalPath; } catch { }
        }
        return s;
    }

    public void AddSubtitle(string path) => _mpv?.CommandAsync("sub-add", path, "select");

    // =====================================================================
    // Transport
    // =====================================================================

    public void PlayPause()
    {
        if (_mpv == null) return;
        if (IsIdle)
        {
            if (_playlist.Count > 0)
            {
                _mpv.CommandAsync("playlist-play-index", (_playlistPos >= 0 ? _playlistPos : 0).ToString(Inv));
                _mpv.SetPropertyAsync("pause", false);
            }
            return;
        }
        if (EofReached && IsPaused)
        {
            // keep-open paused us on the last frame: restart from the beginning.
            _mpv.CommandAsync("seek", "0", "absolute+exact");
            _mpv.SetPropertyAsync("pause", false);
            return;
        }
        _mpv.SetPropertyAsync("pause", !IsPaused);
    }

    public void Play() { if (IsPaused || IsIdle) PlayPause(); }
    public void Pause() => _mpv?.SetPropertyAsync("pause", true);

    public void Stop()
    {
        if (_mpv == null) return;
        _mpv.CommandAsync("stop", "keep-playlist");
    }

    /// <param name="exact">true = precise (decodes to the exact frame), false = nearest keyframe (fast, for drags).</param>
    public void SeekTo(double seconds, bool exact = true)
    {
        if (_mpv == null || !HasMedia) return;
        if (Duration > 0) seconds = Math.Clamp(seconds, 0, Duration);
        Position = seconds;
        Volatile.Write(ref _latestTimePos, seconds);
        _mpv.CommandAsync("seek", seconds.ToString("0.###", Inv), exact ? "absolute+exact" : "absolute+keyframes");
    }

    public void SeekRelative(double seconds, bool exact = true)
    {
        if (_mpv == null || !HasMedia) return;
        _mpv.CommandAsync("seek", seconds.ToString("0.###", Inv), exact ? "relative+exact" : "relative+keyframes");
        var target = Math.Max(0, _position + seconds);
        if (Duration > 0) target = Math.Min(target, Duration);
        ShowOsd($"{(seconds >= 0 ? "▶▶" : "◀◀")} {Math.Abs(seconds):0.#}초   {Formatters.Time(target)} / {Formatters.Time(Duration)}", 900);
    }

    public void SeekPercent(double percent)
    {
        if (_mpv == null || !HasMedia) return;
        _mpv.CommandAsync("seek", percent.ToString("0.###", Inv), "absolute-percent+exact");
    }

    public void FrameStep() { if (HasMedia) _mpv?.CommandAsync("frame-step"); }
    public void FrameBackStep() { if (HasMedia) _mpv?.CommandAsync("frame-back-step"); }

    public void Next()
    {
        if (_mpv == null) return;
        if (_playlistPos < _playlist.Count - 1 || Loop == LoopMode.Playlist)
        {
            _mpv.CommandAsync("playlist-next", "force");
            _mpv.SetPropertyAsync("pause", false);
        }
        else
            ShowOsd("마지막 항목", 1200);
    }

    public void Previous()
    {
        if (_mpv == null) return;
        // Like most players: restart the current item if we're more than 3 s in.
        if (_position > 3 && HasMedia) { SeekTo(0); return; }
        if (_playlistPos > 0 || Loop == LoopMode.Playlist)
        {
            _mpv.CommandAsync("playlist-prev", "force");
            _mpv.SetPropertyAsync("pause", false);
        }
        else
            SeekTo(0);
    }

    public void NextChapter()
    {
        if (_chapters.Count == 0) { ShowOsd("챕터 없음"); return; }
        if (_currentChapter >= _chapters.Count - 1) { ShowOsd("마지막 챕터"); return; }
        var ch = _chapters[_currentChapter + 1];
        SeekTo(ch.Time);
        ShowOsd($"챕터: {ch.DisplayName}");
    }

    public void PreviousChapter()
    {
        if (_chapters.Count == 0) { ShowOsd("챕터 없음"); return; }
        int target = _currentChapter;
        // Within the first 3 s of a chapter go to the previous one, otherwise restart this one.
        if (target >= 0 && _position - _chapters[target].Time < 3) target--;
        if (target < 0) { SeekTo(0); return; }
        var ch = _chapters[target];
        SeekTo(ch.Time);
        ShowOsd($"챕터: {ch.DisplayName}");
    }

    public void PlayIndex(int index)
    {
        if (_mpv == null) return;
        _mpv.CommandAsync("playlist-play-index", index.ToString(Inv));
        _mpv.SetPropertyAsync("pause", false);
    }
    public void RemoveAt(int index) => _mpv?.CommandAsync("playlist-remove", index.ToString(Inv));
    public void MoveItem(int from, int to) => _mpv?.CommandAsync("playlist-move", from.ToString(Inv), to.ToString(Inv));

    /// <summary>Removes everything except the current item (mpv semantics), or stops when idle.</summary>
    public void ClearPlaylist()
    {
        if (_mpv == null) return;
        // Pending opens (folder scans) and folder siblings must not refill the list the user just cleared.
        SupersedeOpens();
        _siblingsPendingFor = null;
        _mpv.CommandAsync("playlist-clear");
        if (IsIdle) _mpv.CommandAsync("stop");
    }

    /// <summary>
    /// Shuffle is a mode. mpv's own 'shuffle' option follows it, so every list opened from then on (folders, saved and
    /// opened playlist files) is shuffled before mpv picks the entry to play; items added to the playlist keep their
    /// order. Turning it off restores the natural order (<see cref="Unshuffle"/>).
    /// </summary>
    public void ToggleShuffle()
    {
        if (_mpv == null) return;
        IsShuffled = !IsShuffled;
        _settings.Shuffle = IsShuffled;
        _shuffleSeq++;
        _mpv.SetPropertyAsync("shuffle", IsShuffled);
        if (IsShuffled)
        {
            lock (_unplacedIds) _unplacedIds.Clear(); // playlist-shuffle gives every entry its place
            _mpv.CommandAsync((err, _) => { if (err >= 0) _ui.BeginInvoke(MoveCurrentToFront); }, "playlist-shuffle");
        }
        else Unshuffle(_mpv);
        ShowOsd(IsShuffled ? "무작위 재생 켜짐" : "무작위 재생 꺼짐");
    }

    /// <summary>
    /// 'playlist-unshuffle' puts every entry back where it was before mpv shuffled it. Entries added in their given order
    /// while shuffle was on never had such a place, and mpv's sort only copes with those at the front (elsewhere they
    /// scramble the rest): they wait there during the sort and then go to the end, in their order.
    /// </summary>
    private void Unshuffle(MpvClient mpv)
    {
        long[] ids;
        lock (_unplacedIds) { ids = [.. _unplacedIds]; _unplacedIds.Clear(); }
        int toggles = _shuffleSeq, seq = _openSeq;
        bool Stale() => toggles != _shuffleSeq || seq != _openSeq;
        SendInChunks(mpv, MoveCommands(mpv, ids, toFront: true), 0, Stale, null, () =>
        {
            if (Stale()) return;
            mpv.CommandAsync((err, _) =>
            {
                if (err >= 0 && ids.Length > 0)
                    _ui.BeginInvoke(() => SendInChunks(mpv, MoveCommands(mpv, ids, toFront: false), 0, Stale, null, () => { }));
            }, "playlist-unshuffle");
        });
    }

    /// <summary>'playlist-move' commands that take the given entries to the front or to the end, keeping their order.</summary>
    private static List<string[]> MoveCommands(MpvClient mpv, long[] ids, bool toFront)
    {
        var commands = new List<string[]>();
        if (ids.Length == 0 || mpv.GetPropertyNode("playlist") is not List<object?> list) return commands;
        var set = ids.ToHashSet();
        int moved = 0;
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i] is not Dictionary<string, object?> m || m.GetLong("id") is not long id || !set.Contains(id)) continue;
            // Moving earlier entries to the front leaves this one in place; moving them to the end shifts it left.
            if (toFront && i != moved) commands.Add(["playlist-move", i.ToString(Inv), moved.ToString(Inv)]);
            else if (!toFront) commands.Add(["playlist-move", (i - moved).ToString(Inv), list.Count.ToString(Inv)]);
            moved++;
        }
        return commands;
    }

    /// <summary>playlist-shuffle moves the playing entry too: put it first, so every other entry still plays after it.</summary>
    private void MoveCurrentToFront()
    {
        if (_mpv?.GetPropertyLong("playlist-pos") is long pos && pos > 0)
            _mpv.CommandAsync("playlist-move", pos.ToString(Inv), "0");
    }

    public void SetLoop(LoopMode mode)
    {
        if (_mpv == null) return;
        _mpv.SetPropertyAsync("loop-file", mode == LoopMode.File ? "inf" : "no");
        _mpv.SetPropertyAsync("loop-playlist", mode == LoopMode.Playlist ? "inf" : "no");
        Loop = mode;
        _settings.LoopMode = mode switch { LoopMode.File => "file", LoopMode.Playlist => "playlist", _ => "none" };
        ShowOsd(mode switch { LoopMode.File => "한 곡 반복", LoopMode.Playlist => "전체 반복", _ => "반복 안 함" });
    }

    public void CycleLoop() => SetLoop(Loop switch { LoopMode.None => LoopMode.Playlist, LoopMode.Playlist => LoopMode.File, _ => LoopMode.None });

    // =====================================================================
    // Audio
    // =====================================================================

    public void SetVolume(double volume, bool osd = true)
    {
        if (_mpv == null) return;
        volume = Math.Clamp(Math.Round(volume), 0, VolumeMax);
        Volume = volume;
        _settings.Volume = volume;
        _mpv.SetPropertyAsync("volume", volume);
        if (IsMuted && volume > 0) SetMute(false, osd: false);
        if (osd) ShowOsd($"볼륨 {volume:0}%");
    }

    public void ChangeVolume(double delta) => SetVolume(Volume + delta);

    public void SetMute(bool mute, bool osd = true)
    {
        if (_mpv == null) return;
        IsMuted = mute;
        _settings.Muted = mute;
        _mpv.SetPropertyAsync("mute", mute);
        if (osd) ShowOsd(mute ? "음소거" : $"볼륨 {Volume:0}%");
    }

    public void ToggleMute() => SetMute(!IsMuted);

    public void SetSpeed(double speed)
    {
        if (_mpv == null) return;
        speed = Math.Clamp(Math.Round(speed, 2), 0.1, 8);
        Speed = speed;
        _mpv.SetPropertyAsync("speed", speed);
        ShowOsd($"속도 {speed:0.##}x");
    }

    public void ChangeSpeed(double delta) => SetSpeed(Speed + delta);

    public void SetAudioTrack(long? id)
    {
        _mpv?.SetPropertyAsync("aid", id?.ToString(Inv) ?? "no");
        var t = _audioTracks.FirstOrDefault(x => x.Id == id);
        ShowOsd(t == null ? "오디오 끔" : "오디오: " + t.DisplayName);
    }

    public void CycleAudioTrack()
    {
        if (_audioTracks.Count < 2) { ShowOsd(_audioTracks.Count == 0 ? "오디오 트랙 없음" : "오디오 트랙 1개"); return; }
        var list = _audioTracks;
        int i = list.ToList().FindIndex(t => t.IsSelected);
        SetAudioTrack(list[(i + 1) % list.Count].Id);
    }

    // =====================================================================
    // Video
    // =====================================================================

    public void SetVideoTrack(long? id) => _mpv?.SetPropertyAsync("vid", id?.ToString(Inv) ?? "no");

    public void Rotate(int deltaDegrees)
    {
        var deg = ((VideoRotate + deltaDegrees) % 360 + 360) % 360;
        _mpv?.SetPropertyAsync("video-rotate", (long)deg);
        ShowOsd($"회전 {deg}°");
    }

    public void ResetRotate() { _mpv?.SetPropertyAsync("video-rotate", 0L); ShowOsd("회전 0°"); }

    /// <param name="aspect">"no" (auto), "16:9", "4:3", "2.35:1", "1:1", ...</param>
    public void SetAspect(string aspect)
    {
        _mpv?.SetPropertyAsync("video-aspect-override", aspect);
        ShowOsd(aspect == "no" ? "화면 비율: 자동" : $"화면 비율: {aspect}");
    }

    private static readonly (double Ratio, string Label)[] AspectLabels =
        [(16.0 / 9, "16:9"), (4.0 / 3, "4:3"), (2.35, "2.35:1"), (1.85, "1.85:1"), (1.0, "1:1"), (9.0 / 16, "9:16")];

    /// <summary>The <see cref="SetAspect"/> label for mpv's video-aspect-override value (-2 = no override).</summary>
    private static string AspectLabel(double aspect)
    {
        if (aspect < 0) return "no";
        foreach (var (ratio, label) in AspectLabels)
            if (Math.Abs(aspect - ratio) < 0.001) return label;
        return aspect.ToString("0.###", Inv);
    }

    /// <summary>Ctrl+H: hardware decoding on (auto-safe) / off.</summary>
    public void ToggleHwdec()
    {
        bool on = !string.IsNullOrEmpty(HwdecCurrent) && HwdecCurrent != "no" || _settings.Hwdec != "no";
        var next = on ? "no" : "auto-safe";
        SetHwdec(next);
        ShowOsd(next == "no" ? "하드웨어 가속 끔" : "하드웨어 가속 켬");
    }

    // =====================================================================
    // Subtitles
    // =====================================================================

    public void SetSubtitleTrack(long? id)
    {
        _mpv?.SetPropertyAsync("sid", id?.ToString(Inv) ?? "no");
        var t = _subtitleTracks.FirstOrDefault(x => x.Id == id);
        ShowOsd(t == null ? "자막 끔" : "자막: " + t.DisplayName);
    }

    public void CycleSubtitleTrack()
    {
        if (_subtitleTracks.Count == 0) { ShowOsd("자막 없음"); return; }
        var list = _subtitleTracks.ToList();
        int i = list.FindIndex(t => t.IsSelected);
        if (i == list.Count - 1) SetSubtitleTrack(null);
        else SetSubtitleTrack(list[i + 1].Id);
    }

    public void ToggleSubtitleVisibility()
    {
        _mpv?.SetPropertyAsync("sub-visibility", !SubVisible);
        ShowOsd(SubVisible ? "자막 숨김" : "자막 표시");
    }

    public void SetSubDelay(double seconds)
    {
        _mpv?.SetPropertyAsync("sub-delay", seconds);
        ShowOsd($"자막 싱크 {seconds * 1000:+0;-0;0} ms");
    }

    private long _subMarginY = -1;

    /// <summary>Lifts subtitles above the player controls while they are visible (mpv scaled px, 720p reference).</summary>
    public void SetSubtitleBottomMargin(long scaledPixels)
    {
        if (_mpv == null || scaledPixels == _subMarginY) return;
        _subMarginY = scaledPixels;
        _mpv.SetPropertyAsync("sub-margin-y", scaledPixels);
    }

    // =====================================================================
    // Misc
    // =====================================================================

    /// <summary>Saves the current frame with subtitles to the screenshot folder.</summary>
    public void Screenshot()
    {
        if (_mpv == null || !HasMedia) return;
        try { Directory.CreateDirectory(_settings.ScreenshotDir); } catch { }
        _mpv.CommandAsync((err, result) =>
        {
            _ui.BeginInvoke(() =>
            {
                if (err < 0) ShowOsd("스크린샷 실패: " + MpvNative.ErrorString(err), 2500);
                else
                {
                    var file = (result as Dictionary<string, object?>)?.GetString("filename");
                    ShowOsd("스크린샷 저장: " + (file != null ? Path.GetFileName(file) : _settings.ScreenshotDir), 2500);
                }
            });
        }, "screenshot", "subtitles");
    }

    public void ShowOsd(string text, int durationMs = 1500)
        => _mpv?.CommandAsync("show-text", text, durationMs.ToString(Inv));

    /// <summary>
    /// Re-applies the cookie source for online streaming at runtime (call after YouTube sign-in/sign-out or a
    /// cookie setting change). change-list append needs no escaping for paths.
    /// </summary>
    public void RefreshYtdlCookies()
    {
        if (_mpv == null) return;
        _mpv.CommandAsync("change-list", "ytdl-raw-options", "remove", "cookies");
        if (YtDlpCookies.RawOption() is { } c)
            _mpv.CommandAsync("change-list", "ytdl-raw-options", "append", $"{c.Key}={c.Value}");
    }

    /// <summary>mpv streams with a copy of the cookie file: renew it when the file changed (e.g. exported again).</summary>
    private void RefreshYtdlCookiesIfChanged()
    {
        if (!YtDlpCookies.IsStreamCopyCurrent()) RefreshYtdlCookies();
    }

    /// <param name="value">The setting (auto-safe, d3d11va, nvdec, ...); mpv gets <see cref="MpvOptionBuilder.EffectiveHwdec"/>.</param>
    public void SetHwdec(string value)
    {
        _settings.Hwdec = value;
        _mpv?.SetPropertyAsync("hwdec", MpvOptionBuilder.EffectiveHwdec(value));
    }

    /// <summary>Sets an arbitrary mpv property (used by settings pages for live changes).</summary>
    public void SetRaw(string name, string value) => _mpv?.SetPropertyAsync(name, value);

    public string? GetRaw(string name) => _mpv?.GetPropertyString(name);

    // =====================================================================
    // Shutdown
    // =====================================================================

    /// <summary>Records the current position and writes the history file right away (Windows logoff/shutdown).</summary>
    public void SaveHistoryNow()
    {
        if (_loadedPath != null)
        {
            _historyPos = EofReached ? 0 : Position;
            HistoryService.Touch(_loadedPath, MediaTitle, Duration, _historyPos);
        }
        HistoryService.SaveNow();
    }

    public async Task ShutdownAsync()
    {
        var mpv = _mpv;
        if (mpv == null) return;
        _positionTimer.Stop();
        if (_loadedPath != null)
        {
            HistoryService.Touch(_loadedPath, MediaTitle, Duration, EofReached ? 0 : Position);
        }
        HistoryService.SaveNow();
        _mpv = null;
        await mpv.DisposeAsync();
        // mpv stopped its yt-dlp runs: the cookie copies made for them can go.
        YtDlpCookies.DeleteStreamCopies();
    }
}

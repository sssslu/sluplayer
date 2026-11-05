using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using SluPlayer.Core;

namespace SluPlayer.Services.Downloads;

public enum DownloadState { Queued, Analyzing, Downloading, Merging, Converting, Completed, Failed, Cancelled, Paused }

/// <summary>What to add to the list (from an analysis card or a playlist entry).</summary>
public sealed record DownloadRequest(
    string Url,
    DownloadOptions Options,
    string? Title = null,
    string? Uploader = null,
    string? ThumbnailUrl = null,
    double? Duration = null,
    string? Key = null,
    bool FromPlaylist = false,
    int? PlaylistItem = null);

/// <summary>One list/history entry. Observable; only touched on the UI thread.</summary>
public sealed class DownloadItem : ObservableObject
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public long Seq { get; set; }
    public string Url { get; init; } = "";
    public string Key { get; init; } = "";
    public DownloadOptions Options { get; init; } = new();
    public string OutputDir { get; init; } = "";
    public bool FromPlaylist { get; init; }
    /// <summary>Which video of a multi-video post <see cref="Url"/> is (1-based), or null.</summary>
    public int? PlaylistItem { get; init; }
    public DateTime CreatedAt { get; init; } = DateTime.Now;

    /// <summary>UTC time the item was first started (partial-file cleanup only deletes side files newer than this).</summary>
    public DateTime? FirstStartedUtc { get; set; }

    public string PresetLabel => Options.Label;
    public bool IsAudio => Options.IsAudio;

    private string _title = "";
    public string Title { get => _title; set => SetProperty(ref _title, value ?? ""); }

    private string? _uploader;
    public string? Uploader { get => _uploader; set => SetProperty(ref _uploader, value); }

    private string? _thumbnailUrl;
    public string? ThumbnailUrl { get => _thumbnailUrl; set => SetProperty(ref _thumbnailUrl, value); }

    private double? _duration;
    public double? Duration { get => _duration; set => SetProperty(ref _duration, value); }

    // ---- state ------------------------------------------------------------------------

    private DownloadState _state;
    public DownloadState State
    {
        get => _state;
        set
        {
            if (!SetProperty(ref _state, value)) return;
            foreach (var n in StateDependents) OnPropertyChanged(n);
        }
    }

    private static readonly string[] StateDependents =
    [
        nameof(StateText), nameof(DetailText), nameof(IsActive), nameof(IsFinished), nameof(IsRunning), nameof(CanPause),
        nameof(CanResume), nameof(CanCancel), nameof(CanRetry), nameof(CanPlay), nameof(IsCompleted), nameof(IsFailed),
        nameof(ShowProgress), nameof(IsIndeterminate), nameof(SizeText), nameof(ShowLoginAction),
    ];

    /// <summary>0..1</summary>
    private double _progress;
    public double Progress
    {
        get => _progress;
        set { if (SetProperty(ref _progress, Math.Clamp(value, 0, 1))) { OnPropertyChanged(nameof(DetailText)); OnPropertyChanged(nameof(ProgressPercent)); } }
    }

    public double ProgressPercent => _progress * 100;

    private bool _progressKnown;
    /// <summary>False while the size is unknown (extraction, post-processing) → indeterminate bar.</summary>
    public bool ProgressKnown { get => _progressKnown; set { if (SetProperty(ref _progressKnown, value)) OnPropertyChanged(nameof(IsIndeterminate)); } }

    public bool IsIndeterminate => _state is DownloadState.Analyzing or DownloadState.Merging or DownloadState.Converting
                                   || (_state == DownloadState.Downloading && !_progressKnown);

    private double? _speed;
    public double? Speed { get => _speed; set { if (SetProperty(ref _speed, value)) OnPropertyChanged(nameof(DetailText)); } }

    private double? _eta;
    public double? Eta { get => _eta; set { if (SetProperty(ref _eta, value)) OnPropertyChanged(nameof(DetailText)); } }

    private long? _totalBytes;
    public long? TotalBytes { get => _totalBytes; set { if (SetProperty(ref _totalBytes, value)) OnPropertyChanged(nameof(SizeText)); } }

    private long? _downloadedBytes;
    public long? DownloadedBytes { get => _downloadedBytes; set { if (SetProperty(ref _downloadedBytes, value)) OnPropertyChanged(nameof(SizeText)); } }

    private string? _stageText;
    /// <summary>Post-processing step ("썸네일 삽입 중") or download part ("영상 1/2").</summary>
    public string? StageText { get => _stageText; set { if (SetProperty(ref _stageText, value)) OnPropertyChanged(nameof(DetailText)); } }

    private string? _filePath;
    public string? FilePath { get => _filePath; set { if (SetProperty(ref _filePath, value)) OnPropertyChanged(nameof(CanPlay)); } }

    /// <summary>yt-dlp's prepared file name ([name] line): base for partial-file cleanup.</summary>
    public string? PartialName { get; set; }

    /// <summary>" (2)" when another download already owns the plain file name (kept so a resume finds its .part files).</summary>
    public string? NameSuffix { get; set; }

    /// <summary>Format ids yt-dlp chose ("399+251"): identifies this item's intermediate files (Title.f399.mp4.part).</summary>
    public string? FormatIds { get; set; }

    private string? _error;
    public string? Error { get => _error; set { if (SetProperty(ref _error, value)) OnPropertyChanged(nameof(DetailText)); } }

    public YtErrorKind? ErrorKind { get; set; }

    private bool _needsLogin;
    public bool NeedsLogin { get => _needsLogin; set { if (SetProperty(ref _needsLogin, value)) OnPropertyChanged(nameof(ShowLoginAction)); } }

    public bool ShowLoginAction => _needsLogin && _state == DownloadState.Failed;

    public DateTime? CompletedAt { get; set; }

    // ---- derived ------------------------------------------------------------------------

    public bool IsRunning => _state is DownloadState.Analyzing or DownloadState.Downloading or DownloadState.Merging or DownloadState.Converting;
    public bool IsActive => IsRunning || _state == DownloadState.Queued;
    public bool IsFinished => _state is DownloadState.Completed or DownloadState.Failed or DownloadState.Cancelled;
    public bool IsCompleted => _state == DownloadState.Completed;
    public bool IsFailed => _state == DownloadState.Failed;
    public bool ShowProgress => _state is not (DownloadState.Completed or DownloadState.Cancelled or DownloadState.Failed);

    public bool CanPause => _state is DownloadState.Queued or DownloadState.Analyzing or DownloadState.Downloading;
    public bool CanResume => _state == DownloadState.Paused;
    public bool CanCancel => _state is DownloadState.Queued or DownloadState.Analyzing or DownloadState.Downloading
        or DownloadState.Merging or DownloadState.Converting or DownloadState.Paused;
    public bool CanRetry => _state is DownloadState.Failed or DownloadState.Cancelled;
    public bool CanPlay => _state == DownloadState.Completed && _filePath != null;

    public string StateText => _state switch
    {
        DownloadState.Queued => "대기",
        DownloadState.Analyzing => "준비 중",
        DownloadState.Downloading => "다운로드 중",
        DownloadState.Merging => "병합 중",
        DownloadState.Converting => "변환 중",
        DownloadState.Completed => "완료",
        DownloadState.Failed => "실패",
        DownloadState.Cancelled => "취소됨",
        DownloadState.Paused => "일시정지",
        _ => "",
    };

    /// <summary>"45% · 12.3 MB/s · 00:32 남음", "병합 중", "네트워크 오류".</summary>
    public string DetailText
    {
        get
        {
            switch (_state)
            {
                case DownloadState.Downloading:
                {
                    var parts = new List<string>();
                    if (_progressKnown) parts.Add(((int)Math.Floor(_progress * 100)).ToString(CultureInfo.InvariantCulture) + "%");
                    else parts.Add(StateText);
                    if (_speed is > 0) parts.Add(Formatters.Speed(_speed.Value));
                    if (_eta is >= 0 and < 360000) parts.Add(FormatEta(_eta.Value) + " 남음");
                    if (!string.IsNullOrEmpty(_stageText)) parts.Add(_stageText!);
                    return string.Join(" · ", parts);
                }
                case DownloadState.Merging:
                case DownloadState.Converting:
                    return string.IsNullOrEmpty(_stageText) ? StateText : _stageText!;
                case DownloadState.Failed:
                    return string.IsNullOrEmpty(_error) ? StateText : _error!;
                case DownloadState.Paused:
                    return _progress > 0 && _progressKnown
                        ? $"{StateText} · {(int)Math.Floor(_progress * 100)}%"
                        : StateText;
                default:
                    return StateText;
            }
        }
    }

    public string SizeText
    {
        get
        {
            if (_state == DownloadState.Completed)
                return _totalBytes is > 0 ? Formatters.Bytes(_totalBytes.Value) : "";
            if (_state is DownloadState.Cancelled or DownloadState.Failed)
                return "";
            if (_totalBytes is > 0 && _downloadedBytes is > 0 && _state is DownloadState.Downloading or DownloadState.Paused)
                return $"{Formatters.Bytes(_downloadedBytes.Value)} / {Formatters.Bytes(_totalBytes.Value)}";
            return _totalBytes is > 0 ? Formatters.Bytes(_totalBytes.Value) : "";
        }
    }

    public static string FormatEta(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, Math.Round(seconds)));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";
    }

    /// <summary>Clears transient progress fields before a (re)start.</summary>
    public void ResetRuntime(bool keepProgress)
    {
        Speed = null;
        Eta = null;
        StageText = null;
        Error = null;
        ErrorKind = null;
        NeedsLogin = false;
        if (!keepProgress)
        {
            Progress = 0;
            ProgressKnown = false;
            DownloadedBytes = null;
        }
    }

    // ---- persistence -----------------------------------------------------------------------

    public DownloadRecord ToRecord() => new()
    {
        Id = Id, Seq = Seq, Url = Url, Key = Key, Options = Options, OutputDir = OutputDir, FromPlaylist = FromPlaylist,
        PlaylistItem = PlaylistItem, CreatedAt = CreatedAt, FirstStartedUtc = FirstStartedUtc, Title = Title, Uploader = Uploader,
        ThumbnailUrl = ThumbnailUrl, Duration = Duration, State = State, Progress = Progress, ProgressKnown = ProgressKnown,
        TotalBytes = TotalBytes, DownloadedBytes = DownloadedBytes, FilePath = FilePath, PartialName = PartialName,
        NameSuffix = NameSuffix, FormatIds = FormatIds, Error = Error, ErrorKind = ErrorKind, NeedsLogin = NeedsLogin,
        CompletedAt = CompletedAt,
    };

    private static readonly System.Text.RegularExpressions.Regex OldStyleMessage = new("(어|아|해|예|에|세|네|까|죠)요");

    public static DownloadItem FromRecord(DownloadRecord r)
    {
        var item = new DownloadItem
        {
            Id = string.IsNullOrEmpty(r.Id) ? Guid.NewGuid().ToString("N") : r.Id,
            Seq = r.Seq,
            Url = r.Url,
            Key = string.IsNullOrEmpty(r.Key) ? UrlTools.Key(r.Url) : r.Key,
            Options = (r.Options ?? new DownloadOptions()).Normalized(),
            OutputDir = r.OutputDir ?? "",
            FromPlaylist = r.FromPlaylist,
            PlaylistItem = r.PlaylistItem is > 0 ? r.PlaylistItem : null,
            CreatedAt = r.CreatedAt,
            FirstStartedUtc = r.FirstStartedUtc,
            CompletedAt = r.CompletedAt,
        };
        item._title = r.Title ?? r.Url;
        item._uploader = r.Uploader;
        item._thumbnailUrl = r.ThumbnailUrl;
        item._duration = r.Duration;
        item._progress = Math.Clamp(r.Progress, 0, 1);
        item._progressKnown = r.ProgressKnown;
        item._totalBytes = r.TotalBytes;
        item._downloadedBytes = r.DownloadedBytes;
        item._filePath = r.FilePath;
        item.PartialName = r.PartialName;
        item.NameSuffix = r.NameSuffix;
        item.FormatIds = r.FormatIds;
        // Messages saved by older versions are full sentences ("…받을 수 있어요."); the list shows just "실패" for those.
        item._error = r.Error is { } e && OldStyleMessage.IsMatch(e) ? null : r.Error;
        item.ErrorKind = r.ErrorKind;
        item._needsLogin = r.NeedsLogin;
        // Work that was running when the app closed comes back paused; .part files let it resume.
        item._state = r.State is DownloadState.Queued or DownloadState.Analyzing or DownloadState.Downloading
            or DownloadState.Merging or DownloadState.Converting ? DownloadState.Paused : r.State;
        return item;
    }
}

/// <summary>JSON shape of one item in downloads.json.</summary>
public sealed class DownloadRecord
{
    public string Id { get; set; } = "";
    public long Seq { get; set; }
    public string Url { get; set; } = "";
    public string Key { get; set; } = "";
    public DownloadOptions? Options { get; set; }
    public string? OutputDir { get; set; }
    public bool FromPlaylist { get; set; }
    public int? PlaylistItem { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? FirstStartedUtc { get; set; }
    public string? Title { get; set; }
    public string? Uploader { get; set; }
    public string? ThumbnailUrl { get; set; }
    public double? Duration { get; set; }
    public DownloadState State { get; set; }
    public double Progress { get; set; }
    public bool ProgressKnown { get; set; }
    public long? TotalBytes { get; set; }
    public long? DownloadedBytes { get; set; }
    public string? FilePath { get; set; }
    public string? PartialName { get; set; }
    public string? NameSuffix { get; set; }
    public string? FormatIds { get; set; }
    public string? Error { get; set; }
    public YtErrorKind? ErrorKind { get; set; }
    public bool NeedsLogin { get; set; }
    public DateTime? CompletedAt { get; set; }
}

public sealed class DownloadsFileModel
{
    public int Version { get; set; } = 1;
    public long NextSeq { get; set; }
    public List<DownloadRecord> Items { get; set; } = new();
}

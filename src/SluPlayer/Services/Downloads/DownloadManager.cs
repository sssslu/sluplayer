using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using SluPlayer.Services.Settings;
using SluPlayer.UI;

namespace SluPlayer.Services.Downloads;

/// <summary>
/// The download list (singleton, independent of any window). Runs up to two yt-dlp processes at a time,
/// aggregates their progress, persists the list to downloads.json and restores it when first used, i.e. when the
/// download window first opens (unfinished items come back paused; their .part files let them resume).
/// Create and use it on the UI thread only.
/// </summary>
public sealed class DownloadManager
{
    private static DownloadManager? _instance;

    /// <summary>The shared instance; the first access must happen on the UI thread.</summary>
    public static DownloadManager Instance
    {
        get
        {
            if (_instance != null) return _instance;
            var d = Application.Current?.Dispatcher;
            if (d != null && !d.CheckAccess())
                throw new InvalidOperationException("DownloadManager must be created on the UI thread.");
            _instance = new DownloadManager();
            return _instance;
        }
    }

    private const int MaxHistory = 1000;

    private readonly Dispatcher _ui;
    private readonly Dictionary<string, Job> _jobs = new();
    private readonly DispatcherTimer _saveTimer;
    private readonly object _saveLock = new();
    private long _seq;
    private bool _shuttingDown;

    /// <summary>Newest first. Bind to this.</summary>
    public ObservableCollection<DownloadItem> Items { get; } = new();

    /// <summary>Raised on the UI thread when counts/states change (badges, buttons).</summary>
    public event Action? Changed;

    private DownloadManager()
    {
        _ui = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _saveTimer = new DispatcherTimer(DispatcherPriority.Background, _ui) { Interval = TimeSpan.FromMilliseconds(800) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveNow(); };
        Load();
        AppLifetime.PendingWorkProbes.Add(() =>
        {
            int n = ActiveCount;
            return n > 0 ? $"다운로드 {n}개 진행 중" : null;
        });
        AppLifetime.ShutdownHandlers.Add(Shutdown);
    }

    private static AppSettings Settings => SettingsService.Current;

    public int ActiveCount => Items.Count(i => i.IsActive);

    // =====================================================================
    // Queue operations
    // =====================================================================

    /// <summary>An unfinished item for the same video and kind (video/audio), if any.</summary>
    public DownloadItem? FindDuplicate(string key, DownloadOptions options)
        => Items.FirstOrDefault(i => i.Key == key && i.Options.Kind == options.Kind
                                     && (i.IsActive || i.State == DownloadState.Paused));

    /// <summary>Queues several downloads as one block at the top of the list (in the given order).</summary>
    public IReadOnlyList<DownloadItem> EnqueueMany(IEnumerable<DownloadRequest> requests, out IReadOnlyList<DownloadItem> duplicates)
    {
        var added = new List<DownloadItem>();
        var dups = new List<DownloadItem>();
        var s = Settings;
        foreach (var r in requests)
        {
            var options = r.Options.Normalized();
            var key = r.Key ?? UrlTools.Key(r.Url);
            if ((FindDuplicate(key, options) ?? added.FirstOrDefault(a => a.Key == key && a.Options.Kind == options.Kind)) is { } dup)
            {
                dups.Add(dup);
                continue;
            }
            var dir = options.IsAudio ? s.AudioDownloadDir : s.DownloadDir;
            var item = new DownloadItem
            {
                Seq = ++_seq,
                Url = r.Url,
                Key = key,
                Options = options,
                OutputDir = dir,
                FromPlaylist = r.FromPlaylist,
                PlaylistItem = r.PlaylistItem,
                Title = string.IsNullOrWhiteSpace(r.Title) ? r.Url : r.Title!,
                Uploader = r.Uploader,
                ThumbnailUrl = r.ThumbnailUrl,
                Duration = r.Duration,
                State = DownloadState.Queued,
            };
            Items.Insert(added.Count, item);
            added.Add(item);
        }
        duplicates = dups;
        if (added.Count > 0)
        {
            Log.Info($"queued {added.Count} download(s): {string.Join(", ", added.Select(a => a.Key))}");
            TrimHistory();
            Pump();
            SaveSoon();
            Changed?.Invoke();
        }
        return added;
    }

    public void Pause(DownloadItem item)
    {
        if (item.State == DownloadState.Queued)
        {
            item.State = DownloadState.Paused;
        }
        else if (_jobs.TryGetValue(item.Id, out var job) && item.CanPause)
        {
            job.Stop = StopReason.Pause;
            item.StageText = "일시정지 중…";
            job.Process?.Kill();
        }
        SaveSoon();
        Changed?.Invoke();
    }

    public void Resume(DownloadItem item)
    {
        if (item.State != DownloadState.Paused) return;
        item.ResetRuntime(keepProgress: true);
        item.State = DownloadState.Queued;
        Pump();
        SaveSoon();
        Changed?.Invoke();
    }

    public void Cancel(DownloadItem item)
    {
        if (_jobs.TryGetValue(item.Id, out var job))
        {
            job.Stop = StopReason.Cancel;
            item.StageText = "취소 중…";
            job.Process?.Kill();
            return; // Finish() deletes the partial files once the process is gone
        }
        if (!item.CanCancel) return;
        if (item.State == DownloadState.Paused)
        {
            DeletePartialsAsync(item, finalExistedBefore: true);
            item.PartialName = null;
            item.FormatIds = null;
        }
        item.ResetRuntime(keepProgress: false);
        item.State = DownloadState.Cancelled;
        SaveSoon();
        Changed?.Invoke();
    }

    public void Retry(DownloadItem item)
    {
        if (!item.CanRetry) return;
        item.ResetRuntime(keepProgress: false);
        item.FilePath = null;
        item.State = DownloadState.Queued;
        Pump();
        SaveSoon();
        Changed?.Invoke();
    }

    /// <summary>Removes the item from the list (stopping it and deleting partial files when unfinished).</summary>
    public void Remove(DownloadItem item)
    {
        if (_jobs.TryGetValue(item.Id, out var job))
        {
            job.Stop = StopReason.Remove;
            job.Process?.Kill();
            return;
        }
        if (item.State is DownloadState.Paused or DownloadState.Failed) DeletePartialsAsync(item, finalExistedBefore: true);
        Items.Remove(item);
        SaveSoon();
        Changed?.Invoke();
    }

    /// <summary>"완료 지우기": removes completed and cancelled entries (files stay on disk).</summary>
    public void ClearFinished()
    {
        foreach (var i in Items.Where(i => i.State is DownloadState.Completed or DownloadState.Cancelled).ToList())
            Items.Remove(i);
        SaveSoon();
        Changed?.Invoke();
    }

    public void ResumeAll()
    {
        foreach (var i in Items.Where(i => i.State == DownloadState.Paused).OrderBy(i => i.Seq).ToList())
        {
            i.ResetRuntime(keepProgress: true);
            i.State = DownloadState.Queued;
        }
        Pump();
        SaveSoon();
        Changed?.Invoke();
    }

    /// <summary>Re-queues failed items that failed because YouTube wanted a sign-in (after signing in).</summary>
    public int RetryLoginFailures()
    {
        var list = Items.Where(i => i.State == DownloadState.Failed && i.NeedsLogin).ToList();
        foreach (var i in list)
        {
            i.ResetRuntime(keepProgress: false);
            i.State = DownloadState.Queued;
        }
        if (list.Count > 0) { Pump(); SaveSoon(); Changed?.Invoke(); }
        return list.Count;
    }

    private const int MaxConcurrent = 2;

    /// <summary>Starts queued items while there are free slots.</summary>
    public void Pump()
    {
        if (_shuttingDown) return;
        while (_jobs.Count < MaxConcurrent)
        {
            var next = Items.Where(i => i.State == DownloadState.Queued).OrderBy(i => i.Seq).FirstOrDefault();
            if (next == null) break;
            Start(next);
        }
    }

    // =====================================================================
    // Running one item
    // =====================================================================

    private enum StopReason { None, Pause, Cancel, Remove, Shutdown, Restart }

    private readonly Dictionary<string, int> _restarts = new();

    private sealed class Job(DownloadItem item)
    {
        public DownloadItem Item { get; } = item;
        public YtDlpProcess? Process;
        public StopReason Stop;
        public string[] SelParts = [];
        public long? SelSize;
        public readonly Dictionary<string, PartProgress> Parts = new();
        public readonly List<string> StderrTail = new();
        public bool FinalExistedBefore;
        public int ProgressLines;
    }

    private DateTime _lastProgressSaveUtc;

    private record struct PartProgress(long Downloaded, long? Total, bool Finished);

    private void Start(DownloadItem item)
    {
        var job = new Job(item);
        _jobs[item.Id] = job;
        item.ResetRuntime(keepProgress: true);
        item.State = DownloadState.Analyzing;
        Changed?.Invoke();
        _ = RunAsync(job);
    }

    private async Task RunAsync(Job job)
    {
        var item = job.Item;
        CookieLease? cookies = null;
        try
        {
            // Disk work runs on the thread pool: the folder may be a sleeping or offline network share (CreateDirectory
            // then blocks for the SMB timeout). Awaiting first also returns Start() before anything can fail, so a
            // queue of failing items is never worked through inside one call.
            var dir = item.OutputDir;
            var (tools, lease, folderError) = await Task.Run(() => Prepare(dir));
            cookies = lease;
            if (job.Stop != StopReason.None)
            {
                Finish(job, -1); // paused, cancelled, removed or shut down meanwhile
                return;
            }
            if (tools == null)
            {
                Fail(item, YtDlpError.EngineMissing());
                return;
            }
            if (lease == null)
            {
                Log.Warn($"cannot create download folder '{dir}': {folderError?.Message}");
                Fail(item, new YtDlpError(YtErrorKind.Permission, $"저장 폴더 생성 실패: {dir}", false, folderError?.Message));
                return;
            }

            var args = DownloadArgs.Build(item.Options, dir, item.Url, item.FromPlaylist, tools, lease, item.NameSuffix, item.PlaylistItem);
            item.FirstStartedUtc ??= DateTime.UtcNow;
            job.Process = YtDlpProcess.Start(tools, args, (line, isErr) => _ui.BeginInvoke(() => OnLine(job, line, isErr)));
            if (job.Stop != StopReason.None) job.Process.Kill(); // paused/cancelled while starting

            int code = await job.Process.Completion;
            // Line handlers were queued before this continuation; flush them before deciding the outcome.
            await _ui.InvokeAsync(static () => { }, DispatcherPriority.Background);
            Finish(job, code);
        }
        catch (Exception ex)
        {
            Log.Error($"download failed to run: {item.Url}", ex);
            if (job.Stop is StopReason.None)
                Fail(item, new YtDlpError(YtErrorKind.Other, "다운로드 시작 실패: " + ex.Message, false, ex.Message));
            else
                Finish(job, -1);
        }
        finally
        {
            cookies?.Dispose();
            job.Process?.Dispose();
            _jobs.Remove(item.Id);
            if (!_shuttingDown)
            {
                Pump();
                SaveSoon();
                Changed?.Invoke();
                if (_jobs.Count == 0) YtDlpUpdater.OnDownloadsIdle();
            }
        }
    }

    /// <summary>
    /// The thread-pool part of starting a download: engine paths, the output folder and the cookies copy
    /// (null when the engine is missing or the folder could not be created).
    /// </summary>
    private static (ToolPaths? Tools, CookieLease? Cookies, Exception? FolderError) Prepare(string dir)
    {
        var tools = YtDlpService.Tools;
        if (tools == null) return (null, null, null);
        try
        {
            Directory.CreateDirectory(dir);
        }
        catch (Exception ex)
        {
            return (tools, null, ex);
        }
        return (tools, CookieLease.Acquire(), null);
    }

    private void Finish(Job job, int exitCode)
    {
        var item = job.Item;
        item.Speed = null;
        item.Eta = null;
        // The process may have finished on its own just before a pause/cancel took effect: keep the finished file.
        if (job.Stop is StopReason.Pause or StopReason.Shutdown or StopReason.Cancel && exitCode == 0
            && item.FilePath is { } done && File.Exists(done))
        {
            Complete(item, done);
            return;
        }
        switch (job.Stop)
        {
            case StopReason.Pause:
            case StopReason.Shutdown:
                item.StageText = null;
                item.State = DownloadState.Paused;
                Log.Info($"download paused: {item.Key} at {item.Progress:P0}");
                return;
            case StopReason.Cancel:
                DeletePartialsAsync(item, job.FinalExistedBefore);
                item.PartialName = null; // its files are gone; a retry starts from scratch
                item.FormatIds = null;
                item.ResetRuntime(keepProgress: false);
                item.State = DownloadState.Cancelled;
                Log.Info($"download cancelled: {item.Key}");
                return;
            case StopReason.Remove:
                DeletePartialsAsync(item, job.FinalExistedBefore);
                Items.Remove(item);
                return;
            case StopReason.Restart:
                // Stopped right after yt-dlp chose the (taken) file name: start again under the new name.
                // Nothing was downloaded yet; only side files this run may have written are removed.
                DeletePartialsAsync(item, finalExistedBefore: true, sideFilesOnly: true);
                item.PartialName = null;
                item.FilePath = null;
                item.StageText = null;
                item.State = DownloadState.Queued;
                return;
        }

        var file = item.FilePath;
        if (file == null && exitCode == 0 && item.PartialName is { } n && File.Exists(n)) file = n;
        if (file != null && File.Exists(file))
        {
            if (exitCode != 0) Log.Warn($"yt-dlp exited with {exitCode} but produced {file}: {string.Join(" | ", job.StderrTail.TakeLast(3))}");
            Complete(item, file);
            return;
        }

        var stderr = string.Join("\n", job.StderrTail);
        var err = YtDlpError.From(stderr, item.Url, downloading: true);
        if (exitCode == 0 && err.Kind == YtErrorKind.Other)
            err = err with { Message = "받은 파일 없음" };
        Log.Warn($"download failed ({exitCode}): {item.Url}: {err.Detail ?? err.Message}");
        Fail(item, err);
    }

    private void Complete(DownloadItem item, string file)
    {
        item.FilePath = file;
        try { item.TotalBytes = new FileInfo(file).Length; } catch { }
        item.StageText = null;
        item.Progress = 1;
        item.ProgressKnown = true;
        item.CompletedAt = DateTime.Now;
        item.State = DownloadState.Completed;
        Log.Info($"download completed: {file}");
        Shell?.ShowToast($"다운로드 완료: {item.Title}", ToastKind.Success);
    }

    private void Fail(DownloadItem item, YtDlpError err)
    {
        item.StageText = null;
        item.Error = err.Message;
        item.ErrorKind = err.Kind;
        item.NeedsLogin = err.NeedsLogin;
        item.State = DownloadState.Failed;
    }

    private static IShell? Shell => Application.Current?.MainWindow as IShell;

    // =====================================================================
    // yt-dlp output
    // =====================================================================

    private void OnLine(Job job, string line, bool isErr)
    {
        var item = job.Item;
        var ev = YtDlpParser.Parse(line, isErr);
        switch (ev)
        {
            case ProgressEvent p:
                OnProgress(job, p);
                break;
            case SelectionEvent sel:
                job.SelParts = sel.Parts;
                if (item.FormatIds != sel.FormatId)
                {
                    item.FormatIds = sel.FormatId;
                    SaveSoon();
                }
                job.SelSize = sel.Size is > 0 ? sel.Size : null;
                if (job.SelSize is long total) item.TotalBytes = total;
                if (item.State == DownloadState.Analyzing) item.State = DownloadState.Downloading;
                break;
            case NameEvent name:
                // Printed before anything is downloaded: an existing final file was not made by this run. Another
                // unfinished item may already use the name too (the video and the audio job of a muxed-only video in
                // one folder): its file doesn't exist yet, but both runs would write the same .part file.
                job.FinalExistedBefore = File.Exists(name.Path);
                bool usedByOther = Items.Any(i => !ReferenceEquals(i, item) && (i.IsActive || i.State == DownloadState.Paused)
                                                  && string.Equals(i.PartialName, name.Path, StringComparison.OrdinalIgnoreCase));
                if (job.Stop == StopReason.None
                    && (usedByOther || (job.FinalExistedBefore && !IsSameDownload(item, name.Path)))
                    && FreeNameSuffix(item, name.Path) is { } suffix
                    && (_restarts[item.Id] = _restarts.GetValueOrDefault(item.Id) + 1) <= 3)
                {
                    // yt-dlp would report "already downloaded" and reuse a file of another quality/format, or resume
                    // the other download's .part file.
                    Log.Info($"'{Path.GetFileName(name.Path)}' belongs to another download; restarting as '…{suffix}'");
                    item.PartialName = name.Path; // side files this run may have written get cleaned up
                    item.NameSuffix = suffix;
                    job.Stop = StopReason.Restart;
                    job.Process?.Kill();
                    break;
                }
                if (!string.Equals(item.PartialName, name.Path, StringComparison.OrdinalIgnoreCase))
                {
                    item.PartialName = name.Path;
                    SaveSoon();
                }
                break;
            case PostprocessEvent pp when pp.Status == "started":
                OnPostprocess(job, pp.Postprocessor);
                break;
            case FileEvent f:
                item.FilePath = f.Path;
                break;
            case MessageEvent m:
                AddTail(job, (m.IsError ? "ERROR: " : "WARNING: ") + m.Text);
                if (m.IsError) Log.Warn($"yt-dlp [{item.Key}]: {m.Text}");
                break;
            case null when isErr && !string.IsNullOrWhiteSpace(line):
                AddTail(job, line); // tracebacks and other unstructured stderr
                break;
        }
    }

    /// <summary>
    /// An existing file may be reused only when it is this item's own earlier result (also a file this item merged
    /// before the app closed during post-processing) or an identical earlier download (same video, same options).
    /// </summary>
    private bool IsSameDownload(DownloadItem item, string path)
        => string.Equals(item.FilePath, path, StringComparison.OrdinalIgnoreCase)
           || string.Equals(item.PartialName, path, StringComparison.OrdinalIgnoreCase)
           || Items.Any(i => !ReferenceEquals(i, item) && i.State == DownloadState.Completed
                             && string.Equals(i.FilePath, path, StringComparison.OrdinalIgnoreCase)
                             && i.Key == item.Key && i.Options == item.Options);

    /// <summary>" (2)", " (3)" … — the first suffix with no file (or partial file) of that name.</summary>
    private string? FreeNameSuffix(DownloadItem item, string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path)!;
            var ext = Path.GetExtension(path);
            var stem = Path.GetFileNameWithoutExtension(path);
            if (!string.IsNullOrEmpty(item.NameSuffix) && stem.EndsWith(item.NameSuffix, StringComparison.Ordinal))
                stem = stem[..^item.NameSuffix.Length];
            for (int n = 2; n < 100; n++)
            {
                var suffix = $" ({n})";
                var candidate = stem + suffix;
                if (File.Exists(Path.Combine(dir, candidate + ext))) continue;
                if (Directory.EnumerateFiles(dir, candidate + ".*").Any()) continue;
                if (Items.Any(i => !ReferenceEquals(i, item) && (i.IsActive || i.State == DownloadState.Paused) && i.PartialName != null
                                   && string.Equals(Path.GetFileNameWithoutExtension(i.PartialName), candidate, StringComparison.OrdinalIgnoreCase))) continue;
                return suffix;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("could not pick a free file name: " + ex.Message);
        }
        return null;
    }

    private static void AddTail(Job job, string line)
    {
        job.StderrTail.Add(line);
        if (job.StderrTail.Count > 60) job.StderrTail.RemoveAt(0);
    }

    private void OnProgress(Job job, ProgressEvent p)
    {
        var item = job.Item;
        if (item.State is DownloadState.Analyzing or DownloadState.Queued) item.State = DownloadState.Downloading;
        // yt-dlp's first samples of a (re)started download report tiny speeds → absurd ETAs; skip them.
        bool warm = ++job.ProgressLines > 2;
        // Persist progress now and then so a crash/kill restores a meaningful "일시정지 · 45%".
        if (DateTime.UtcNow - _lastProgressSaveUtc > TimeSpan.FromSeconds(5))
        {
            _lastProgressSaveUtc = DateTime.UtcNow;
            SaveSoon();
        }

        var id = string.IsNullOrEmpty(p.FormatId) || p.FormatId == "NA" ? "_" : p.FormatId;
        bool finished = p.Status == "finished";
        long downloaded = p.Downloaded ?? (finished ? p.BestTotal ?? 0 : 0);
        long? partTotal = finished ? Math.Max(p.Total ?? downloaded, downloaded) : p.BestTotal;
        job.Parts[id] = new PartProgress(downloaded, partTotal, finished);

        var parts = job.SelParts.Length > 0 ? job.SelParts : job.Parts.Keys.ToArray();
        long sumDownloaded = 0;
        bool allTotalsKnown = true;
        long sumTotals = 0;
        double fractionSum = 0;
        foreach (var part in parts)
        {
            if (job.Parts.TryGetValue(part, out var pp))
            {
                sumDownloaded += pp.Downloaded;
                if (pp.Total is > 0) sumTotals += pp.Total.Value; else allTotalsKnown = false;
                fractionSum += pp.Finished ? 1 : pp.Total is > 0 ? Math.Clamp((double)pp.Downloaded / pp.Total.Value, 0, 1) : 0;
            }
            else
            {
                allTotalsKnown = false;
            }
        }

        double overall;
        long? overallTotal;
        bool known;
        if (allTotalsKnown && sumTotals > 0)
        {
            overallTotal = sumTotals;
            overall = (double)sumDownloaded / Math.Max(sumTotals, sumDownloaded);
            known = true;
        }
        else if (job.SelSize is long sel && sel > 0)
        {
            overallTotal = Math.Max(sel, sumDownloaded);
            overall = (double)sumDownloaded / overallTotal.Value;
            known = true;
        }
        else
        {
            overallTotal = null;
            overall = parts.Length > 0 ? fractionSum / parts.Length : 0;
            known = partTotal is > 0 || finished;
        }

        // Never move backwards within a run (estimates of HLS sizes jitter).
        if (overall >= item.Progress || !item.ProgressKnown) item.Progress = Math.Min(overall, finished && parts.All(x => job.Parts.TryGetValue(x, out var q) && q.Finished) ? 1 : 0.999);
        item.ProgressKnown = known;
        item.DownloadedBytes = sumDownloaded;
        if (overallTotal is > 0) item.TotalBytes = overallTotal;
        item.Speed = finished || !warm ? null : p.Speed;
        if (finished || !warm)
            item.Eta = null;
        else if (p.Speed is > 0 && overallTotal is > 0)
            item.Eta = Math.Max(0, (overallTotal.Value - sumDownloaded) / p.Speed.Value);
        else
            item.Eta = p.Eta;

        if (parts.Length > 1)
        {
            int idx = Array.IndexOf(parts, id);
            if (idx >= 0) item.StageText = (idx == 0 ? "영상" : idx == 1 ? "소리" : "파일") + $" {idx + 1}/{parts.Length}";
        }
        else if (p.FragmentCount is > 1 && p.FragmentIndex is int fi && !known)
        {
            item.StageText = $"조각 {fi}/{p.FragmentCount}";
        }
    }

    private static void OnPostprocess(Job job, string name)
    {
        var item = job.Item;
        item.Speed = null;
        item.Eta = null;
        string? stage;
        DownloadState state = DownloadState.Converting;
        switch (name)
        {
            case "Merger":
                state = DownloadState.Merging;
                stage = null;
                break;
            case "ExtractAudio":
                stage = item.Options.AudioFormat.ToUpperInvariant() + " 변환 중";
                break;
            case "EmbedThumbnail":
            case "ThumbnailsConvertor":
                stage = "썸네일 삽입 중";
                break;
            case "Metadata":
            case "FFmpegMetadata":
                stage = "정보 삽입 중";
                break;
            case "MoveFiles":
                stage = "마무리 중";
                if (item.State == DownloadState.Downloading) state = DownloadState.Downloading;
                break;
            default:
                stage = name.StartsWith("Fixup", StringComparison.Ordinal) ? "파일 정리 중" : null;
                break;
        }
        item.State = state;
        item.StageText = stage;
    }

    // =====================================================================
    // Partial files
    // =====================================================================

    private static readonly Regex FormatPartRe = new(@"^\.f[\w-]+\.\w+$", RegexOptions.Compiled);
    private static readonly HashSet<string> SideExts = new(StringComparer.OrdinalIgnoreCase)
        { ".webp", ".jpg", ".jpeg", ".png", ".vtt", ".srt", ".ass", ".lrc", ".ttml", ".srv3", ".json3" };

    /// <summary>
    /// Deletes what an unfinished download left behind: *.part, *.ytdl, fragment files, per-format intermediates
    /// (Title.f399.mp4) and side files written by this job (thumbnails/subtitles). Runs in the background with retries
    /// because the killed processes may still hold the files for a moment.
    /// When another unfinished item uses the same file stem (e.g. the audio and the video version of one video), only
    /// files that provably belong to this item are touched: its own name + ".part"/".ytdl"/"-Frag…" and its own format
    /// intermediates. <paramref name="sideFilesOnly"/> limits the cleanup to thumbnails/subtitles written by this job.
    /// </summary>
    private void DeletePartialsAsync(DownloadItem item, bool finalExistedBefore, bool sideFilesOnly = false)
    {
        var name = item.PartialName;
        if (string.IsNullOrEmpty(name)) return;
        var since = (item.FirstStartedUtc ?? DateTime.UtcNow).AddSeconds(-5);
        var dir = Path.GetDirectoryName(name);
        var stem = Path.GetFileNameWithoutExtension(name);
        if (dir == null || stem.Length == 0) return;

        // Decided on the UI thread (Items is UI-thread only).
        bool shared = Items.Any(i => !ReferenceEquals(i, item) && (i.IsActive || i.State == DownloadState.Paused)
                                     && i.PartialName is { } other
                                     && string.Equals(Path.GetDirectoryName(other), dir, StringComparison.OrdinalIgnoreCase)
                                     && string.Equals(Path.GetFileNameWithoutExtension(other), stem, StringComparison.OrdinalIgnoreCase));
        var ownPrefixes = new List<string> { Path.GetFileName(name) };
        foreach (var id in (item.FormatIds ?? "").Split('+', StringSplitOptions.RemoveEmptyEntries))
            ownPrefixes.Add(stem + ".f" + id + ".");

        _ = Task.Run(async () =>
        {
            for (int attempt = 0; attempt < 6; attempt++)
            {
                if (attempt > 0) await Task.Delay(500 * attempt);
                bool pending = false;
                try
                {
                    if (!Directory.Exists(dir)) return;
                    foreach (var f in Directory.EnumerateFiles(dir, stem + ".*"))
                    {
                        var fileName = Path.GetFileName(f);
                        var rest = fileName[stem.Length..];
                        bool partial = rest.Contains(".part", StringComparison.OrdinalIgnoreCase)
                                       || rest.EndsWith(".ytdl", StringComparison.OrdinalIgnoreCase)
                                       || rest.Contains(".temp.", StringComparison.OrdinalIgnoreCase)
                                       || FormatPartRe.IsMatch(rest);
                        bool side = SideExts.Contains(Path.GetExtension(f)) && File.GetLastWriteTimeUtc(f) >= since;
                        bool final = !finalExistedBefore && string.Equals(f, name, StringComparison.OrdinalIgnoreCase)
                                     && File.GetLastWriteTimeUtc(f) >= since;
                        bool own = ownPrefixes.Any(p => fileName.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                                   && !string.Equals(fileName, Path.GetFileName(name), StringComparison.OrdinalIgnoreCase);
                        bool delete = sideFilesOnly ? side && !shared
                            : shared ? own && partial
                            : partial || side || final;
                        if (!delete) continue;
                        try
                        {
                            File.Delete(f);
                            Log.Info("deleted partial file: " + fileName);
                        }
                        catch (IOException) { pending = true; }
                        catch (UnauthorizedAccessException) { pending = true; }
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn("partial cleanup failed: " + ex.Message);
                    return;
                }
                if (!pending) return;
            }
        });
    }

    // =====================================================================
    // Persistence + shutdown
    // =====================================================================

    private void Load()
    {
        try
        {
            if (!File.Exists(AppPaths.DownloadsFile)) return;
            var model = JsonSerializer.Deserialize<DownloadsFileModel>(File.ReadAllText(AppPaths.DownloadsFile), SettingsService.JsonOptions);
            if (model?.Items == null) return;
            var items = model.Items.Where(r => !string.IsNullOrWhiteSpace(r.Url)).Select(DownloadItem.FromRecord)
                .OrderByDescending(i => i.Seq).ToList();
            foreach (var i in items) Items.Add(i);
            _seq = Math.Max(model.NextSeq, items.Count > 0 ? items.Max(i => i.Seq) : 0);
            Log.Info($"downloads restored: {items.Count} item(s), {items.Count(i => i.State == DownloadState.Paused)} paused");
        }
        catch (Exception ex)
        {
            Log.Error("downloads.json unreadable", ex);
            try { File.Copy(AppPaths.DownloadsFile, AppPaths.DownloadsFile + ".bad", overwrite: true); } catch { }
        }
    }

    private void TrimHistory()
    {
        if (Items.Count <= MaxHistory) return;
        foreach (var old in Items.Where(i => i.IsFinished).OrderBy(i => i.Seq).Take(Items.Count - MaxHistory).ToList())
            Items.Remove(old);
    }

    public void SaveSoon()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private long _saveVersion, _savedVersion; // UI thread / under _saveLock

    public void SaveNow()
    {
        var model = new DownloadsFileModel { NextSeq = _seq, Items = Items.Select(i => i.ToRecord()).ToList() };
        string json;
        try { json = JsonSerializer.Serialize(model, SettingsService.JsonOptions); }
        catch (Exception ex) { Log.Error("failed to serialize downloads", ex); return; }
        // WriteAtomic flushes to disk, which can stall for a moment on a busy hard disk (progress saves come every 5 s
        // during downloads): write on a worker, except at exit, where the save must finish before the process ends.
        long version = ++_saveVersion;
        if (_shuttingDown) WriteSnapshot(json, version);
        else _ = Task.Run(() => WriteSnapshot(json, version));
    }

    private void WriteSnapshot(string json, long version)
    {
        lock (_saveLock)
        {
            if (version < _savedVersion) return; // a newer snapshot is already on disk
            _savedVersion = version;
            try { SettingsService.WriteAtomic(AppPaths.DownloadsFile, json); }
            catch (Exception ex) { Log.Error("failed to save downloads.json", ex); }
        }
    }

    /// <summary>App exit: stop yt-dlp (partial files allow resuming next time) and save the queue.</summary>
    private void Shutdown()
    {
        _shuttingDown = true;
        _saveTimer.Stop();
        foreach (var job in _jobs.Values.ToList())
        {
            job.Stop = StopReason.Shutdown;
            job.Process?.Kill();
            job.Item.Speed = null;
            job.Item.Eta = null;
            job.Item.State = DownloadState.Paused;
        }
        foreach (var i in Items.Where(i => i.State == DownloadState.Queued)) i.State = DownloadState.Paused;
        SaveNow();
    }
}

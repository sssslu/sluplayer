using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SluPlayer.Services;
using SluPlayer.Services.Downloads;
using SluPlayer.UI;
using SluPlayer.UI.Dialogs;
using SluPlayer.ViewModels.Online;

namespace SluPlayer.ViewModels;

/// <summary>The download window: link analysis, the download list, engine and sign-in state.</summary>
public sealed class OnlineViewModel : ObservableObject, IDisposable
{
    private readonly IShell _shell;
    private CancellationTokenSource? _analysisCts;
    private bool? _lastWholePlaylist;

    public OnlineViewModel(IShell shell)
    {
        _shell = shell;
        // First use restores downloads.json (unfinished items come back paused).
        Manager = DownloadManager.Instance;
        Manager.Changed += RefreshListState;
        Manager.Items.CollectionChanged += OnItemsChanged;
        YtDlpUpdater.EngineUpdated += OnEngineUpdated;
        YouTubeLoginDialog.LoginChanged += OnLoginChanged;

        AnalyzeCommand = new AsyncRelayCommand(() => AnalyzeAsync(UrlText, null));
        PasteCommand = new AsyncRelayCommand(PasteAsync);
        CancelAnalysisCommand = new RelayCommand(() => _analysisCts?.Cancel());
        VideoOnlyCommand = new AsyncRelayCommand(() => ResolvePlaylistChoiceAsync(false));
        WholePlaylistCommand = new AsyncRelayCommand(() => ResolvePlaylistChoiceAsync(true));
        CloseAnalysisCommand = new RelayCommand(CloseAnalysis);
        RetryAnalysisCommand = new AsyncRelayCommand(() => AnalyzeAsync(_errorUrl ?? UrlText, _lastWholePlaylist));

        PauseCommand = new RelayCommand<DownloadItem>(i => { if (i != null) Manager.Pause(i); });
        ResumeCommand = new RelayCommand<DownloadItem>(i => { if (i != null) Manager.Resume(i); });
        CancelCommand = new RelayCommand<DownloadItem>(i => { if (i != null) Manager.Cancel(i); });
        RetryCommand = new RelayCommand<DownloadItem>(i => { if (i != null) Manager.Retry(i); });
        RemoveCommand = new RelayCommand<DownloadItem>(i => { if (i != null) Manager.Remove(i); });
        PlayItemCommand = new RelayCommand<DownloadItem>(PlayItem);
        OpenItemFolderCommand = new RelayCommand<DownloadItem>(i => { if (i != null) OpenFolder(i.OutputDir, i.FilePath); });
        ResumeAllCommand = new RelayCommand(Manager.ResumeAll);
        ClearFinishedCommand = new RelayCommand(Manager.ClearFinished);
        OpenDownloadFolderCommand = new RelayCommand(() => OpenFolder(_shell.Settings.DownloadDir, null));

        UpdateEngineCommand = new AsyncRelayCommand(UpdateEngineAsync);
        LoginCommand = new AsyncRelayCommand(LoginAsync);
        LogoutCommand = new AsyncRelayCommand(LogoutAsync);

        RefreshListState();
        RefreshLoginState();
        _ = LoadEngineVersionAsync();
    }

    private DownloadManager Manager { get; }

    // =====================================================================
    // Link + analysis
    // =====================================================================

    private string _urlText = "";
    public string UrlText { get => _urlText; set => SetProperty(ref _urlText, value ?? ""); }

    public IAsyncRelayCommand AnalyzeCommand { get; }
    public IAsyncRelayCommand PasteCommand { get; }
    public IRelayCommand CancelAnalysisCommand { get; }
    public IAsyncRelayCommand VideoOnlyCommand { get; }
    public IAsyncRelayCommand WholePlaylistCommand { get; }
    public IRelayCommand CloseAnalysisCommand { get; }
    public IAsyncRelayCommand RetryAnalysisCommand { get; }

    private bool _isAnalyzing;
    public bool IsAnalyzing { get => _isAnalyzing; private set => SetProperty(ref _isAnalyzing, value); }

    private VideoAnalysisViewModel? _video;
    public VideoAnalysisViewModel? Video { get => _video; private set => SetProperty(ref _video, value); }

    private PlaylistAnalysisViewModel? _playlist;
    public PlaylistAnalysisViewModel? Playlist { get => _playlist; private set => SetProperty(ref _playlist, value); }

    /// <summary>A "watch?v=…&amp;list=…" link waiting for the choice between the video and the whole playlist.</summary>
    private string? _playlistChoiceUrl;
    public bool HasPlaylistChoice => _playlistChoiceUrl != null;

    private void SetPlaylistChoice(string? url)
    {
        _playlistChoiceUrl = url;
        OnPropertyChanged(nameof(HasPlaylistChoice));
    }

    // ---- message line under the link box (errors, notes) ----

    private string? _message;
    public string? Message { get => _message; private set => SetProperty(ref _message, value); }

    private bool _messageIsError;
    public bool MessageIsError { get => _messageIsError; private set => SetProperty(ref _messageIsError, value); }

    private bool _messageNeedsLogin;
    public bool MessageNeedsLogin { get => _messageNeedsLogin; private set => SetProperty(ref _messageNeedsLogin, value); }

    private bool _canRetryAnalysis;
    public bool CanRetryAnalysis { get => _canRetryAnalysis; private set => SetProperty(ref _canRetryAnalysis, value); }

    private string? _errorUrl;

    private void ShowMessage(string? text, bool error = false, bool needsLogin = false, string? retryUrl = null)
    {
        _errorUrl = retryUrl;
        Message = text;
        MessageIsError = error;
        MessageNeedsLogin = needsLogin;
        CanRetryAnalysis = retryUrl != null;
        if (text != null && !error) _ = ClearNoteLaterAsync(text);
    }

    /// <summary>Notes ("이미 목록에 있음") disappear by themselves; errors stay until the next action.</summary>
    private async Task ClearNoteLaterAsync(string text)
    {
        await Task.Delay(TimeSpan.FromSeconds(5));
        if (Message == text && !MessageIsError) ShowMessage(null);
    }

    private async Task PasteAsync()
    {
        string? clip = null;
        try
        {
            if (Clipboard.ContainsText()) clip = Clipboard.GetText();
        }
        catch (Exception ex)
        {
            Log.Debug("clipboard unavailable: " + ex.Message);
        }
        var url = UrlTools.ExtractUrl(clip);
        if (url == null)
        {
            ShowMessage("클립보드에 링크 없음");
            return;
        }
        UrlText = url;
        await AnalyzeAsync(url, null);
    }

    /// <param name="wholePlaylist">null = ask for "watch?v=&amp;list=" links.</param>
    public async Task AnalyzeAsync(string? text, bool? wholePlaylist)
    {
        var input = (text ?? "").Trim();
        if (input.Length == 0) return;
        var url = UrlTools.ExtractUrl(input);
        if (url == null)
        {
            ShowMessage("잘못된 주소", error: true);
            return;
        }
        if (url != _urlText.Trim()) UrlText = url;

        _analysisCts?.Cancel();
        Video = null;
        Playlist = null;
        ShowMessage(null);
        if (wholePlaylist == null && UrlTools.IsYouTubeVideoInList(url))
        {
            SetPlaylistChoice(url);
            return;
        }
        SetPlaylistChoice(null);
        if (UrlTools.IsDirectMessage(url))
        {
            ShowMessage("DM 링크 미지원. 게시물 링크 필요", error: true);
            return;
        }

        var cts = new CancellationTokenSource();
        _analysisCts = cts;
        IsAnalyzing = true;
        _lastWholePlaylist = wholePlaylist;
        try
        {
            var analyzeUrl = url;
            if (wholePlaylist == false && UrlTools.YouTubeVideoId(url) is { } vid) analyzeUrl = UrlTools.YouTubeWatchUrl(vid);
            // Mixes and radios (list=RD…) have no /playlist page ("This playlist type is unviewable"): yt-dlp expands
            // them from the watch page, so the original link is kept for them.
            if (wholePlaylist == true && UrlTools.YouTubeListId(url) is { } list && !list.StartsWith("RD", StringComparison.Ordinal))
                analyzeUrl = UrlTools.YouTubePlaylistUrl(list);
            var result = await YtDlpService.AnalyzeAsync(analyzeUrl, wholePlaylist == true, cts.Token);
            if (cts.IsCancellationRequested) return;
            if (result.Video != null)
                Video = new VideoAnalysisViewModel(_shell, result.Video, v => Enqueue([v.CreateRequest()]));
            else if (result.Playlist != null)
                Playlist = new PlaylistAnalysisViewModel(_shell, result.Playlist, Enqueue, u => _ = AnalyzeAsync(u, null));
        }
        catch (OperationCanceledException) { }
        catch (YtDlpException ex)
        {
            if (!cts.IsCancellationRequested) ShowMessage(ex.Error.Message, error: true, ex.Error.NeedsLogin, url);
        }
        catch (Exception ex)
        {
            if (cts.IsCancellationRequested) return;
            Log.Error("analysis failed", ex);
            ShowMessage("분석 실패: " + ex.Message, error: true, retryUrl: url);
        }
        finally
        {
            if (ReferenceEquals(_analysisCts, cts))
            {
                IsAnalyzing = false;
                _analysisCts = null;
            }
            cts.Dispose();
        }
    }

    private async Task ResolvePlaylistChoiceAsync(bool whole)
    {
        var url = _playlistChoiceUrl;
        if (url == null) return;
        SetPlaylistChoice(null);
        await AnalyzeAsync(url, whole);
    }

    private void CloseAnalysis()
    {
        _analysisCts?.Cancel();
        Video = null;
        Playlist = null;
        SetPlaylistChoice(null);
        ShowMessage(null);
    }

    /// <summary>Adds requests to the list; the card closes when something was added.</summary>
    private void Enqueue(IReadOnlyList<DownloadRequest> requests)
    {
        if (requests.Count == 0) return;
        var added = Manager.EnqueueMany(requests, out var duplicates);
        if (added.Count == 0)
        {
            ShowMessage("이미 목록에 있음");
            return;
        }
        CloseAnalysis();
        if (duplicates.Count > 0) ShowMessage($"{added.Count}개 추가 · {duplicates.Count}개는 이미 목록에 있음");
    }

    // =====================================================================
    // Download list
    // =====================================================================

    public ObservableCollection<DownloadItem> Downloads => Manager.Items;

    public IRelayCommand<DownloadItem> PauseCommand { get; }
    public IRelayCommand<DownloadItem> ResumeCommand { get; }
    public IRelayCommand<DownloadItem> CancelCommand { get; }
    public IRelayCommand<DownloadItem> RetryCommand { get; }
    public IRelayCommand<DownloadItem> RemoveCommand { get; }
    public IRelayCommand<DownloadItem> PlayItemCommand { get; }
    public IRelayCommand<DownloadItem> OpenItemFolderCommand { get; }
    public IRelayCommand ResumeAllCommand { get; }
    public IRelayCommand ClearFinishedCommand { get; }
    public IRelayCommand OpenDownloadFolderCommand { get; }

    private string _listSummary = "";
    public string ListSummary { get => _listSummary; private set => SetProperty(ref _listSummary, value); }

    private bool _canResumeAll;
    public bool CanResumeAll { get => _canResumeAll; private set => SetProperty(ref _canResumeAll, value); }

    private bool _canClearFinished;
    public bool CanClearFinished { get => _canClearFinished; private set => SetProperty(ref _canClearFinished, value); }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshListState();

    private void RefreshListState()
    {
        var items = Manager.Items;
        int running = items.Count(i => i.IsRunning);
        int queued = items.Count(i => i.State == DownloadState.Queued);
        int paused = items.Count(i => i.State == DownloadState.Paused);
        int failed = items.Count(i => i.State == DownloadState.Failed);
        CanResumeAll = paused > 0;
        CanClearFinished = items.Any(i => i.State is DownloadState.Completed or DownloadState.Cancelled);
        ListSummary = OnlineFormat.Join(
            running > 0 ? $"진행 {running}" : null,
            queued > 0 ? $"대기 {queued}" : null,
            paused > 0 ? $"일시정지 {paused}" : null,
            failed > 0 ? $"실패 {failed}" : null);
    }

    private void PlayItem(DownloadItem? item)
    {
        if (item?.FilePath == null) return;
        if (!File.Exists(item.FilePath))
        {
            ShowMessage("파일 없음: " + Path.GetFileName(item.FilePath), error: true);
            return;
        }
        _shell.Play(item.FilePath);
    }

    private void OpenFolder(string dir, string? file)
    {
        try
        {
            if (file != null && File.Exists(file))
            {
                Process.Start(new ProcessStartInfo("explorer.exe") { Arguments = $"/select,\"{file}\"", UseShellExecute = false });
                return;
            }
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo("explorer.exe") { Arguments = $"\"{dir}\"", UseShellExecute = false });
        }
        catch (Exception ex)
        {
            Log.Warn("could not open folder: " + ex.Message);
            ShowMessage("폴더 열기 실패: " + dir, error: true);
        }
    }

    // =====================================================================
    // Engine (yt-dlp) version + update
    // =====================================================================

    private string _engineText = "yt-dlp";
    public string EngineText { get => _engineText; private set => SetProperty(ref _engineText, value); }

    private string? _engineStatus;
    public string? EngineStatus { get => _engineStatus; private set => SetProperty(ref _engineStatus, value); }

    public IAsyncRelayCommand UpdateEngineCommand { get; }

    private async Task LoadEngineVersionAsync()
    {
        var v = await YtDlpService.GetVersionAsync();
        EngineText = v != null ? "yt-dlp " + v : "yt-dlp 없음";
    }

    private async Task UpdateEngineAsync()
    {
        var progress = new Progress<string>(s => EngineStatus = s);
        var outcome = await YtDlpUpdater.UpdateAsync(progress, CancellationToken.None);
        EngineStatus = outcome.Kind switch
        {
            UpdateOutcomeKind.UpToDate => "최신 버전",
            UpdateOutcomeKind.Updated => null,
            UpdateOutcomeKind.Staged => $"{outcome.Version} 준비됨 · 다운로드 후 적용",
            _ => outcome.Message ?? "업데이트 실패",
        };
        await LoadEngineVersionAsync();
    }

    private void OnEngineUpdated(string exe) => _ = LoadEngineVersionAsync();

    // =====================================================================
    // YouTube sign-in
    // =====================================================================

    private bool _isLoggedIn;
    public bool IsLoggedIn { get => _isLoggedIn; private set => SetProperty(ref _isLoggedIn, value); }

    private string _loginText = "";
    public string LoginText { get => _loginText; private set => SetProperty(ref _loginText, value); }

    public IAsyncRelayCommand LoginCommand { get; }
    public IAsyncRelayCommand LogoutCommand { get; }

    private void RefreshLoginState()
    {
        IsLoggedIn = YtDlpCookies.HasYouTubeLogin;
        LoginText = IsLoggedIn ? "YouTube 로그인됨" : "YouTube 로그인 안 됨";
    }

    /// <summary>Sign-in or sign-out from any window (this one, the player, settings).</summary>
    private void OnLoginChanged(bool signedIn)
    {
        RefreshLoginState();
        if (signedIn) Manager.RetryLoginFailures();
    }

    private async Task LoginAsync()
    {
        // The dialog saves the cookies, refreshes mpv's ytdl cookies and raises LoginChanged (state + retries).
        bool ok = await YouTubeLoginDialog.ShowAsync(_shell, OnlineFormat.ActiveWindow(_shell));
        if (!ok) return;
        // Re-run the analysis that failed because of the sign-in requirement.
        if (MessageNeedsLogin && _errorUrl != null) await AnalyzeAsync(_errorUrl, _lastWholePlaylist);
        else if (MessageNeedsLogin) ShowMessage(null);
    }

    private Task LogoutAsync() => YouTubeLoginDialog.SignOutAsync(_shell);

    // =====================================================================
    // Window lifecycle
    // =====================================================================

    /// <summary>Called whenever the download window is shown.</summary>
    public void OnShown()
    {
        RefreshLoginState();
        Manager.Pump();
        // Engine housekeeping and the daily update check: once per app run, never at startup.
        _ = YtDlpUpdater.CheckOnceAsync(_shell.Settings);
    }

    public void Dispose()
    {
        _analysisCts?.Cancel();
        Manager.Changed -= RefreshListState;
        Manager.Items.CollectionChanged -= OnItemsChanged;
        YtDlpUpdater.EngineUpdated -= OnEngineUpdated;
        YouTubeLoginDialog.LoginChanged -= OnLoginChanged;
    }
}

using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using SluPlayer.Core.Interop;
using SluPlayer.Core.Player;
using SluPlayer.Services;
using SluPlayer.Services.Settings;
using SluPlayer.UI.Views;

namespace SluPlayer.UI;

public partial class MainWindow : Window, IShell
{
    private readonly AppSettings _settings;
    private readonly PlayerEngine _player;
    private readonly List<string> _pendingOpen = new();
    private readonly DispatcherTimer _saveTimer;
    private nint _hwnd;
    private bool _engineReady;
    private bool _autoFitPending;
    private AudioView? _audioView;
    private PlaylistPanel? _playlistPanel;
    private PageWindow? _downloadWindow;
    private PageWindow? _settingsWindow;

    public MainWindow(AppSettings settings, string[] args)
    {
        _settings = settings;
        _player = new PlayerEngine(Dispatcher, settings);
        InitializeComponent();

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SettingsService.Save(); };

        _pendingOpen.AddRange(args.Where(a => !string.IsNullOrWhiteSpace(a) && !a.StartsWith("--", StringComparison.Ordinal)));
        _startupCommands.AddRange(args.Where(a => a.StartsWith("--", StringComparison.Ordinal)));

        Video.HwndCreated += OnVideoHwndCreated;
        SourceInitialized += OnSourceInitialized;
        Root.LayoutUpdated += (_, _) => SyncVideoHost();
        Closing += OnClosing;
        Drop += OnDrop;
        DragOver += OnDragOver;
        StateChanged += (_, _) => OnWindowStateChanged();
        LocationChanged += (_, _) => PlaceOverlays();
        SizeChanged += (_, _) => PlaceOverlays();
        Activated += (_, _) => PlaceOverlays();
        Loaded += (_, _) =>
        {
            if (IsActive) Keyboard.Focus(this);
            Dispatcher.BeginInvoke(ShowControls, DispatcherPriority.Loaded);
        };

        _player.PropertyChanged += OnPlayerPropertyChanged;
        _player.FileLoaded += OnFileLoaded;
        _player.FileStarted += (_, _) => _mediaReady = false;
        _player.MediaReady += (_, _) => { _mediaReady = true; TryAutoFit(); };
        _player.EndFile += OnEndFile;
        _player.MpvShutdown += (_, _) => RequestClose();
        _player.OpenRejected += (_, message) => ShowToast(message, ToastKind.Warning);

        RestoreWindowBounds(noActivate: args.Contains("--noactivate", StringComparer.OrdinalIgnoreCase));
        if (_settings.PlaylistPanelOpen) ShowPlaylistPanel(true);
        ApplyOnTop();
        UpdateTitle();
        UpdateSurface();
        InitTaskbarButtons();
    }

    // =====================================================================
    // IShell
    // =====================================================================

    Window IShell.Window => this;
    public PlayerEngine Player => _player;
    public AppSettings Settings => _settings;

    public void Play(IReadOnlyList<string> items, bool append = false)
    {
        var list = items.Where(i => !string.IsNullOrWhiteSpace(i)).ToList();
        if (list.Count == 0) return;
        _playRequests++;
        if (!_engineReady)
        {
            if (!append) _pendingOpen.Clear();
            _pendingOpen.AddRange(list);
            return;
        }
        if (list.All(i => MediaFormats.Classify(i) == MediaKind.Subtitle) && _player.HasMedia)
        {
            foreach (var s in list) _player.AddSubtitle(s);
            return;
        }
        if (append) _player.OpenMany(list, append: true);
        else
        {
            _autoFitPending = true;
            _openingUrl = MediaFormats.IsUrl(list[0]);
            _surfaceError = null;
            if (list.Count == 1) _player.Open(list[0]);
            else _player.OpenMany(list);
            UpdateSurface();
            ShowPlayer();
        }
    }

    public void ShowPlayer()
    {
        // A request from the download/settings window (e.g. play a finished download) brings the player forward.
        if (!QuietCommand && !IsActive && (_downloadWindow?.IsActive == true || _settingsWindow?.IsActive == true))
        {
            if (WindowState == WindowState.Minimized) WindowState = _wasMaximizedBeforeMinimize ? WindowState.Maximized : WindowState.Normal;
            Activate();
        }
        ShowControls();
    }

    public void ShowToast(string message, ToastKind kind = ToastKind.Info)
    {
        // The download/settings window the user works in shows its own messages.
        foreach (var w in new[] { _downloadWindow, _settingsWindow })
        {
            if (w is { IsActive: true })
            {
                w.ShowToast(message, kind);
                return;
            }
        }
        if (WindowState == WindowState.Minimized)
        {
            var open = new[] { _downloadWindow, _settingsWindow }.FirstOrDefault(w => w is { IsVisible: true, WindowState: not WindowState.Minimized });
            if (open != null)
            {
                open.ShowToast(message, kind);
                return;
            }
        }
        // Over visible video the native window hides WPF content, so use mpv's OSD there.
        if (_player.HasVideo && Video.ActualWidth > 4)
        {
            _player.ShowOsd(message, 2500);
            return;
        }
        // The floating control bar (a separate window) covers the bottom of the video area.
        ToastHost.Margin = new Thickness(0, 0, 20, Overlay.ControlBarWindow.HeightDip + 8);
        Toasts.Show(ToastHost, message, kind);
    }

    public void OpenFilesDialog(bool append = false)
    {
        if (SkipModalForScript("open-files dialog")) return;
        var dlg = new OpenFileDialog
        {
            Title = append ? "파일 추가" : "파일 열기",
            Filter = MediaFormats.OpenFileFilter,
            Multiselect = true,
        };
        if (dlg.ShowDialog(this) == true) Play(dlg.FileNames, append);
    }

    public void OpenFolderDialog()
    {
        if (SkipModalForScript("open-folder dialog")) return;
        var dlg = new OpenFolderDialog { Title = "폴더 열기" };
        if (dlg.ShowDialog(this) == true) Play([dlg.FolderName]);
    }

    public void OpenUrlDialog()
    {
        if (SkipModalForScript("URL dialog")) return;
        var url = Dialogs.OpenUrlDialog.Prompt(this);
        if (!string.IsNullOrWhiteSpace(url)) Play([url!]);
    }

    public void OpenSubtitleDialog()
    {
        if (SkipModalForScript("subtitle dialog")) return;
        var dlg = new OpenFileDialog { Title = "자막 파일 열기", Filter = MediaFormats.SubtitleFileFilter };
        if (dlg.ShowDialog(this) == true) _player.AddSubtitle(dlg.FileName);
    }

    public void SaveSettings()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    // =====================================================================
    // Download / settings windows
    // =====================================================================

    public void ShowDownloads() =>
        ShowPage(ref _downloadWindow, () => new PageWindow("다운로드", "Icon.Download", new OnlineView(this), 900, 640));

    public void ShowSettings() =>
        ShowPage(ref _settingsWindow, () => new PageWindow("설정", "Icon.Settings", new SettingsView(this), 860, 640));

    /// <summary>Opens the download window with this link analysed.</summary>
    public void DownloadUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        ShowDownloads();
        (_downloadWindow?.Page as OnlineView)?.Analyze(url);
    }

    /// <summary>
    /// Shows a page window, creating it on first use next to the player. A quiet command (--noactivate while SluPlayer
    /// is not active) shows it without activation and never brings an open one forward.
    /// </summary>
    private void ShowPage(ref PageWindow? field, Func<PageWindow> create)
    {
        if (_closing) return;
        bool quiet = QuietCommand;
        var w = field;
        if (w == null)
        {
            w = field = create();
            w.PlayerPinned = () => Topmost;
            var created = w;
            Application.Current.Deactivated += (_, _) => created.Unpin();
            PlaceNear(w);
        }
        if (!w.IsVisible)
        {
            // WPF only shows a maximized window activated.
            if (quiet && w.WindowState == WindowState.Maximized)
            {
                Log.Info($"--noactivate command: '{w.Title}' window not shown (maximized windows always activate)");
                return;
            }
            w.ShowActivated = !quiet;
            w.Show();
            // Closed while minimized: it comes back hidden in the taskbar otherwise.
            if (!quiet && w.WindowState == WindowState.Minimized) w.BringToFront();
        }
        else if (!quiet)
        {
            w.BringToFront();
        }
        (w.Page as IPage)?.OnNavigatedTo();
    }

    /// <summary>Centres a new page window on the player, inside the work area of the player's monitor.</summary>
    private void PlaceNear(Window w)
    {
        if (_hwnd == 0 || !Win32.GetWindowRect(_hwnd, out var r)) return;
        var info = new Win32.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Win32.MONITORINFO>() };
        if (!Win32.GetMonitorInfo(Win32.MonitorFromWindow(_hwnd, Win32.MONITOR_DEFAULTTONEAREST), ref info)) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        double sx = dpi.DpiScaleX, sy = dpi.DpiScaleY;
        var work = info.rcWork;
        double width = Math.Min(w.Width, work.Width / sx), height = Math.Min(w.Height, work.Height / sy);
        double cx = (r.Left + r.Right) / 2.0 / sx, cy = (r.Top + r.Bottom) / 2.0 / sy;
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Width = width;
        w.Height = height;
        w.Left = Math.Clamp(cx - width / 2, work.Left / sx, Math.Max(work.Left / sx, work.Right / sx - width));
        w.Top = Math.Clamp(cy - height / 2, work.Top / sy, Math.Max(work.Top / sy, work.Bottom / sy - height));
    }

    // =====================================================================
    // Remote commands (second instance, scripts)
    // =====================================================================

    /// <summary>Files forwarded from a second instance (Explorer "open with", a shortcut, scripts).</summary>
    public void HandleExternalArgs(string[] args)
    {
        var items = args.Where(a => !string.IsNullOrWhiteSpace(a) && !a.StartsWith("--", StringComparison.Ordinal)).ToList();
        var commands = args.Where(a => a.StartsWith("--", StringComparison.Ordinal)).ToList();
        bool noActivate = commands.Contains("--noactivate", StringComparer.OrdinalIgnoreCase);
        if (_closing)
        {
            // This instance is shutting down (mpv may already be gone): start a fresh one that waits for us.
            // That includes the instance a restart started, when our shutdown took longer than it waits.
            if (items.Count > 0 || commands.Count == 0
                || commands.Any(c => c.StartsWith("--restart-wait=", StringComparison.OrdinalIgnoreCase)))
                RelaunchAfterExit(args);
            return;
        }
        // A plain start (Start menu, taskbar, desktop shortcut) while running brings the window back.
        if (!noActivate && (items.Count > 0 || commands.Count == 0))
        {
            if (WindowState == WindowState.Minimized) WindowState = _wasMaximizedBeforeMinimize ? WindowState.Maximized : WindowState.Normal;
            Activate();
            Win32Foreground();
        }
        var outer = _scriptNoActivate;
        _scriptNoActivate = noActivate;
        try
        {
            if (items.Count > 0)
            {
                // Selecting several files in Explorer starts one process per file: opens arriving in quick
                // succession are queued instead of replacing each other.
                long now = Environment.TickCount64;
                bool burst = now - _lastExternalOpenTick < 1500;
                _lastExternalOpenTick = now;
                Play(items, append: burst);
            }
            foreach (var c in commands) TryExecuteCommandArg(c);
        }
        finally
        {
            _scriptNoActivate = outer;
        }
    }

    /// <summary>True while a forwarded or startup command line that says --noactivate (scripts, tests) is handled.</summary>
    private bool _scriptNoActivate;

    /// <summary>
    /// A --noactivate command is running while SluPlayer is not the active window: nothing may take the focus from
    /// the user's app. Window state changes then use non-activating calls, and modal dialogs and prompts are skipped.
    /// </summary>
    private bool QuietCommand => _scriptNoActivate && !IsActive;

    /// <summary>File/folder pickers and the URL box are modal and always activate: never opened by a quiet command.</summary>
    private bool SkipModalForScript(string what)
    {
        if (!QuietCommand) return false;
        Log.Info($"--noactivate command: {what} not opened (it would take the focus)");
        return true;
    }

    private void TryExecuteCommandArg(string arg)
    {
        try { ExecuteCommandArg(arg); }
        catch (Exception ex) { Log.Warn($"command '{arg}' failed: {ex.Message}"); }
    }

    /// <summary>Starts a new instance that waits for this process to exit, then opens <paramref name="args"/>.</summary>
    private static bool RelaunchAfterExit(string[] args)
    {
        var exe = Environment.ProcessPath;
        if (exe == null) return false;
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false };
            psi.ArgumentList.Add($"--restart-wait={Environment.ProcessId}");
            foreach (var a in args)
                if (!a.StartsWith("--restart-wait=", StringComparison.OrdinalIgnoreCase)) psi.ArgumentList.Add(a);
            System.Diagnostics.Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("relaunch failed", ex);
            return false;
        }
    }

    // =====================================================================
    // Startup
    // =====================================================================

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProcHook);
        ApplyDwmStyle();
    }

    private void OnVideoHwndCreated(object? sender, EventArgs e)
    {
        try
        {
            _player.Initialize(Video.VideoHwnd);
            _engineReady = true;
            Video.EnsureChildrenDisabled();
        }
        catch (DllNotFoundException ex)
        {
            Log.Error("libmpv-2.dll missing", ex);
            MessageBox.Show(this, "재생 엔진 없음: libmpv-2.dll", "SluPlayer", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        catch (Exception ex)
        {
            Log.Error("player init failed", ex);
            MessageBox.Show(this, "재생 엔진 시작 실패\n\n" + ex.Message, "SluPlayer", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        // Started by a script with --noactivate: its startup commands must not take the focus either.
        var outer = _scriptNoActivate;
        _scriptNoActivate = _startupCommands.Contains("--noactivate", StringComparer.OrdinalIgnoreCase);
        try
        {
            if (_pendingOpen.Count > 0)
            {
                var items = _pendingOpen.ToList();
                _pendingOpen.Clear();
                Play(items);
            }
            foreach (var c in _startupCommands) TryExecuteCommandArg(c);
        }
        finally
        {
            _scriptNoActivate = outer;
        }
        _startupCommands.Clear();

        // An engine update activated from the download window: online playback uses the new yt-dlp right away.
        Services.Downloads.YtDlpUpdater.EngineUpdated += exe => Dispatcher.BeginInvoke(() =>
            _player.Mpv?.CommandAsync("change-list", "script-opts", "append", "ytdl_hook-ytdl_path=" + exe));
        Dialogs.YouTubeLoginDialog.LoginChanged += OnLoginChanged;
    }

    private readonly List<string> _startupCommands = new();
    private long _lastExternalOpenTick = Environment.TickCount64;

    // =====================================================================
    // Player area: audio view, playlist panel, idle / loading / error state
    // =====================================================================

    public bool IsPlaylistPanelOpen => PlaylistPanelHost.Visibility == Visibility.Visible;

    public void TogglePlaylistPanel()
    {
        ShowPlaylistPanel(!IsPlaylistPanelOpen);
        _settings.PlaylistPanelOpen = IsPlaylistPanelOpen;
        SaveSettings();
        Dispatcher.BeginInvoke(PlaceOverlays, DispatcherPriority.Loaded);
    }

    private void ShowPlaylistPanel(bool open)
    {
        if (open)
        {
            _playlistPanel ??= new PlaylistPanel(this);
            PlaylistPanelHost.Content ??= _playlistPanel;
        }
        PlaylistPanelHost.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Audio-only view, the idle panel (or the last error) and the loading line under the video host.</summary>
    private void UpdateSurface()
    {
        bool audio = _player.HasMedia && _player.IsAudioOnly;
        if (audio && _audioView == null)
        {
            _audioView = new AudioView(this);
            AudioViewHost.Content = _audioView;
        }
        AudioViewHost.Visibility = audio ? Visibility.Visible : Visibility.Collapsed;

        bool loading = (_player.IsLoading || (_openingUrl && _player.IsIdle)) && !_player.HasVideo;
        LoadingText.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;

        bool idle = !_player.HasMedia && !loading;
        IdlePanel.Visibility = idle ? Visibility.Visible : Visibility.Collapsed;
        IdleText.Text = _surfaceError ?? "파일을 끌어다 놓거나 열기";
        IdleLoginButton.Visibility = idle && _surfaceError != null && YtDlpErrors.NeedsLogin(_surfaceError)
            ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Text the surface shows instead of video (loading or error), for --dump-state.</summary>
    private string? SurfaceMessage => LoadingText.Visibility == Visibility.Visible ? LoadingText.Text
        : IdlePanel.Visibility == Visibility.Visible ? _surfaceError : null;

    private void IdleOpen_Click(object sender, RoutedEventArgs e) => OpenFilesDialog();
    private void IdleDownload_Click(object sender, RoutedEventArgs e) => ShowDownloads();
    private void IdleLogin_Click(object sender, RoutedEventArgs e) => ShowYouTubeLogin();

    /// <summary>Opens the in-app YouTube sign-in (the online video that failed is retried by OnLoginChanged).</summary>
    public void ShowYouTubeLogin()
    {
        // The dialog exports the cookies and refreshes mpv's yt-dlp options itself.
        _ = Dialogs.YouTubeLoginDialog.ShowAsync(this);
    }

    /// <summary>Signed in, from any window: retry the online video that failed for lack of a sign-in.</summary>
    private void OnLoginChanged(bool signedIn)
    {
        var retry = _lastFailedUrl;
        if (signedIn && retry != null && !_closing && !_player.HasMedia && _surfaceError != null && YtDlpErrors.NeedsLogin(_surfaceError))
            Play([retry]);
    }

    private string? _lastFailedUrl;

    private string? _surfaceError;

    /// <summary>Moves the single native video window over whichever slot should show video right now.</summary>
    private void SyncVideoHost()
    {
        FrameworkElement? target = null;
        if (_player.HasMedia)
        {
            if (_player.HasVideo) target = VideoSurface;
            else if (_player.HasAlbumArt && _player.IsAudioOnly) target = _audioView?.CoverSlot;
        }

        // Never hide the host window: when mpv finds its (child) window invisible on the next video reconfig it
        // calls ShowWindow(SW_SHOW), which ACTIVATES our top-level window and steals focus from other apps.
        // Shrinking it to 0x0 keeps it "visible" while showing nothing.
        if (target == null || !target.IsVisible || target.ActualWidth < 4 || target.ActualHeight < 4)
        {
            if (Video.Width != 0 || Video.Height != 0)
            {
                Video.Width = 0;
                Video.Height = 0;
            }
            return;
        }

        var p = target.TranslatePoint(new Point(0, 0), Root);
        double w = target.ActualWidth, h = target.ActualHeight;
        var m = Video.Margin;
        if (Math.Abs(m.Left - p.X) > 0.5 || Math.Abs(m.Top - p.Y) > 0.5) Video.Margin = new Thickness(p.X, p.Y, 0, 0);
        if (Math.Abs(Video.Width - w) > 0.5) Video.Width = w;
        if (Math.Abs(Video.Height - h) > 0.5) Video.Height = h;
    }

    // =====================================================================
    // Player events
    // =====================================================================

    private void OnPlayerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PlayerEngine.IsIdle):
                // A stop clears the last error; a failed file keeps it in the idle panel.
                if (_player.IsIdle && !_player.IsLoading && !_lastEndWasError) _surfaceError = null;
                NoteActivity(showControls: false);
                UpdateTitle();
                UpdateSurface();
                SyncVideoHost();
                UpdateTaskbar();
                ApplyOnTop();
                break;
            case nameof(PlayerEngine.HasVideo):
            case nameof(PlayerEngine.HasAlbumArt):
            case nameof(PlayerEngine.IsAudioOnly):
            case nameof(PlayerEngine.IsLoading):
                UpdateSurface();
                SyncVideoHost();
                if (e.PropertyName == nameof(PlayerEngine.HasVideo)) TryAutoFit();
                NoteActivity(showControls: false);
                break;
            case nameof(PlayerEngine.MediaTitle):
            case nameof(PlayerEngine.CurrentPath):
                UpdateTitle();
                break;
            case nameof(PlayerEngine.IsPaused):
                UpdateTaskbar();
                if (_player.IsPaused) ShowControls();
                else NoteActivity(showControls: false);
                ApplyOnTop();
                break;
            case nameof(PlayerEngine.Position):
            case nameof(PlayerEngine.Duration):
                UpdateTaskbarProgress();
                break;
            case nameof(PlayerEngine.Volume):
            case nameof(PlayerEngine.IsMuted):
            case nameof(PlayerEngine.Loop):
            case nameof(PlayerEngine.IsShuffled):
                // The engine keeps these in the in-memory settings; write them out shortly after a change.
                SaveSettings();
                break;
            case nameof(PlayerEngine.SourceWidth):
            case nameof(PlayerEngine.SourceHeight):
                TryAutoFit();
                break;
            case nameof(PlayerEngine.IsBuffering):
            case nameof(PlayerEngine.BufferingPercent):
                // Network streams: show cache fill while playback waits for data.
                if (_player.IsBuffering) _player.ShowOsd($"버퍼링 {_player.BufferingPercent}%", 1200);
                break;
        }
    }

    private bool _openingUrl;

    private void OnFileLoaded(object? sender, EventArgs e)
    {
        _openingUrl = false;
        _lastEndWasError = false;
        _surfaceError = null;
        UpdateSurface();
        Video.EnsureChildrenDisabled();
    }

    private bool _lastEndWasError;

    private void OnEndFile(object? sender, EndFileEventArgs e)
    {
        if (e.Reason == Core.Mpv.MpvEndFileReason.Redirect) return;
        _openingUrl = false;
        _lastEndWasError = e.Reason == Core.Mpv.MpvEndFileReason.Error;
        if (_lastEndWasError)
        {
            bool isUrl = e.Path != null && MediaFormats.IsUrl(e.Path);
            _lastFailedUrl = isUrl ? e.Path : null;
            var name = e.Path == null ? "" : isUrl ? e.Path : Path.GetFileName(e.Path);
            var reason = TranslateError(e.ErrorText, isUrl);
            _surfaceError = (reason.Length > 0 ? "재생 실패: " + reason : "재생 실패") + (name.Length > 0 ? "\n" + name : "");
        }
        UpdateSurface();
    }

    /// <summary>mpv's end-file error in Korean. Failed yt-dlp lookups already arrive translated (PlayerEngine).</summary>
    private static string TranslateError(string? err, bool isUrl) => err switch
    {
        null or "" => "",
        // A stream that cannot be opened (camera/server offline, wrong address, no network) is not a damaged file.
        "loading failed" or "unrecognized file format" when isUrl => "연결 실패",
        "loading failed" => "손상됐거나 지원하지 않는 형식",
        "unrecognized file format" => "지원하지 않는 형식",
        "no audio or video data played" => "재생할 데이터 없음",
        "audio output initialization failed" => "오디오 장치 열기 실패",
        "video output initialization failed" => "비디오 출력 실패",
        "nothing to play" => "재생할 항목 없음",
        _ => err,
    };

    private void UpdateTitle()
    {
        var title = _player.HasMedia
            ? _player.MediaTitle ?? (_player.CurrentPath != null ? Path.GetFileName(_player.CurrentPath) : null)
            : null;
        Title = !string.IsNullOrWhiteSpace(title) ? $"{title} - SluPlayer" : "SluPlayer";
    }

    // =====================================================================
    // Shutdown
    // =====================================================================

    private bool _closing;
    private bool _closeReady;
    private bool _confirmingClose;

    /// <summary>
    /// Close from buttons and commands. Ignored while the exit prompt is open: WPF is still inside Closing then and a
    /// second Close() throws, and the owned overlay windows stay clickable under the (Win32) message box.
    /// </summary>
    public void RequestClose()
    {
        if (_closing || _confirmingClose) return;
        Close();
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closeReady) return;
        e.Cancel = true;
        if (_closing || _confirmingClose) return;

        var pending = AppLifetime.GetPendingWork();
        if (pending.Count > 0 && QuietCommand)
        {
            // --quit from a --noactivate script: a prompt would take the focus and wait for someone to answer.
            // Downloads are paused and saved by their shutdown handler and resume on the next start.
            Log.Info($"closing for a --noactivate command without asking (pending: {pending.Count})");
        }
        else if (pending.Count > 0)
        {
            // The overlays would stay clickable under the message box (it only disables this window).
            _confirmingClose = true;
            HideOverlays();
            MessageBoxResult answer;
            try
            {
                answer = MessageBox.Show(this, string.Join("\n", pending) + "\n\n종료하시겠습니까?",
                    "SluPlayer", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            }
            finally
            {
                _confirmingClose = false;
            }
            if (answer != MessageBoxResult.Yes)
            {
                ShowControls();
                return;
            }
        }

        _closing = true;
        SaveWindowBounds();
        HideOverlays();
        try
        {
            AppLifetime.RunShutdownHandlers();
            foreach (var w in new[] { _downloadWindow, _settingsWindow })
            {
                if (w == null) continue;
                w.CloseForExit();
                (w.Page as IDisposable)?.Dispose();
            }
            await _player.ShutdownAsync();
        }
        catch (Exception ex)
        {
            Log.Error("shutdown error", ex);
        }
        SettingsService.Save();
        HistoryService.SaveNow();
        _closeReady = true;
        // ShutdownAsync can complete synchronously (engine never started): Close() is not allowed while the
        // Closing event is still running, so close from a fresh dispatcher turn.
        _ = Dispatcher.BeginInvoke(Close);
    }

    /// <summary>Persists settings, window bounds and the playback position immediately (Windows logoff/shutdown).</summary>
    public void SaveStateNow()
    {
        if (!_closing) SaveWindowBounds();
        _player.SaveHistoryNow();
        _saveTimer.Stop();
        SettingsService.Save();
    }
}

/// <summary>Optional interface for pages that want a notification each time their window is shown.</summary>
public interface IPage
{
    void OnNavigatedTo();
}

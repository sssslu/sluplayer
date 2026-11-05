using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SluPlayer.Core.Interop;
using SluPlayer.UI.Overlay;

namespace SluPlayer.UI;

// Floating control bars over the video + auto-hide + cursor hiding.
public partial class MainWindow
{
    private ControlBarWindow? _controlBar;
    private TopBarWindow? _topBar;
    private DispatcherTimer? _hideTimer;
    private long _lastActivityTick;
    private bool _cursorHidden;
    private int _menuOpenCount;
    private long _menuClosedTick;
    private const int AutoHideMs = 2500;

    /// <summary>True right after a menu closed: the click that dismissed it must not also toggle playback.</summary>
    private bool MenuJustClosed => _menuOpenCount > 0 || Environment.TickCount64 - _menuClosedTick < 350;

    private void EnsureOverlays()
    {
        if (_controlBar != null) return;
        _controlBar = new ControlBarWindow(this) { Owner = this };
        _topBar = new TopBarWindow(this) { Owner = this };
        _controlBar.PassThroughTest = IsOnResizeEdge;
        _topBar.PassThroughTest = IsOnResizeEdge;
        _controlBar.MouseMove += (_, _) => NoteActivity(showControls: false);
        _topBar.MouseMove += (_, _) => NoteActivity(showControls: false);
        ForwardSurfaceGestures(_controlBar);
        ForwardSurfaceGestures(_topBar);
        // Runs only while the controls are up over playing video (no timer while idle or paused).
        _hideTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _hideTimer.Tick += HideTimer_Tick;
        _topBar.UpdateWindowState(WindowState == WindowState.Maximized, _isFullscreen);
        _controlBar.UpdateFullscreen(_isFullscreen);
        ApplyOnTop();
    }

    /// <summary>Records user activity (resets the auto-hide timer) and optionally reveals the controls.</summary>
    public void NoteActivity(bool showControls)
    {
        _lastActivityTick = Environment.TickCount64;
        if (showControls) ShowControls();
        else StartAutoHide();
    }

    /// <summary>Arms the auto-hide check (after activity or a playback state change) while the controls are up.</summary>
    private void StartAutoHide()
    {
        if (_hideTimer is { IsEnabled: false } && _controlBar?.IsShownLogically == true) _hideTimer.Start();
    }

    public void ShowControls()
    {
        // Not while the exit prompt is open: the bars would stay clickable under its message box.
        if (_closing || _confirmingClose || WindowState == WindowState.Minimized || _inSizeMove) return;
        EnsureOverlays();
        _lastActivityTick = Environment.TickCount64;
        PlaceOverlays();
        _controlBar!.FadeIn();
        _topBar!.FadeIn();
        SetCursorHidden(false);
        LiftSubtitles(true);
        StartAutoHide();
    }

    private void HideControls()
    {
        _controlBar?.FadeOut();
        _topBar?.FadeOut();
        if (VideoSurface.IsMouseOver) SetCursorHidden(true);
        LiftSubtitles(false);
    }

    /// <summary>Keeps subtitles clear of the control bar (like YouTube) while the controls are shown.</summary>
    private void LiftSubtitles(bool lift)
    {
        const long mpvDefault = 34;
        double h = VideoSurface.ActualHeight;
        if (!lift || h < 100) { _player.SetSubtitleBottomMargin(mpvDefault); return; }
        // mpv margins are in "scaled pixels" of a 720-line reference frame.
        long m = (long)Math.Round(84.0 / h * 720) + 12;
        _player.SetSubtitleBottomMargin(Math.Max(mpvDefault, m));
    }

    private void HideOverlays()
    {
        _controlBar?.HideNow();
        _topBar?.HideNow();
        _controlBar?.ClosePopups();
    }

    private void HideTimer_Tick(object? sender, EventArgs e)
    {
        // Nothing to hide, or a state that keeps the controls up until it changes (OnPlayerPropertyChanged re-arms).
        if (_controlBar == null || _topBar == null || !_controlBar.IsShownLogically
            || !_player.HasMedia || _player.IsPaused || _player.IsAudioOnly || _player.IsLoading)
        {
            _hideTimer!.Stop();
            return;
        }
        if (_menuOpenCount > 0 || _controlBar.IsBusy || _controlBar.IsMouseOver || _topBar.IsMouseOver)
        {
            _lastActivityTick = Environment.TickCount64;
            return;
        }
        if (Environment.TickCount64 - _lastActivityTick >= AutoHideMs)
        {
            _hideTimer!.Stop();
            HideControls();
        }
    }

    private void SetCursorHidden(bool hidden)
    {
        if (_cursorHidden == hidden) return;
        _cursorHidden = hidden;
        VideoSurface.Cursor = hidden ? Cursors.None : null;
    }

    /// <summary>Positions the overlays over the video area in physical pixels.</summary>
    private void PlaceOverlays()
    {
        if (_controlBar == null || _topBar == null || _hwnd == 0) return;
        if (WindowState == WindowState.Minimized || !VideoSurface.IsVisible) return;
        if (_inSizeMove)
        {
            HideOverlays();
            return;
        }
        if (PresentationSource.FromVisual(VideoSurface) == null) return;
        var tl = VideoSurface.PointToScreen(new Point(0, 0));
        var br = VideoSurface.PointToScreen(new Point(VideoSurface.ActualWidth, VideoSurface.ActualHeight));
        int x = (int)Math.Round(tl.X), y = (int)Math.Round(tl.Y);
        int w = (int)Math.Round(br.X - tl.X), h = (int)Math.Round(br.Y - tl.Y);
        if (w < 10 || h < 10) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        int barH = Math.Min(h, (int)Math.Round(ControlBarWindow.HeightDip * dpi.DpiScaleY));
        int topH = Math.Min(h / 2, (int)Math.Round(TopBarWindow.HeightDip * dpi.DpiScaleY));
        _controlBar.PlaceAt(x, y + h - barH, w, barH);
        _topBar.PlaceAt(x, y, w, topH);
    }

    /// <summary>
    /// The bars cover the top and bottom of the video with a (non-transparent) gradient, so mouse input there goes to
    /// them. On their empty background, behave like the video: right-click menu, double-click fullscreen, wheel,
    /// middle/X buttons and file drops. Buttons, sliders and the seek bar keep their own handling.
    /// </summary>
    private void ForwardSurfaceGestures(OverlayWindow bar)
    {
        static bool OnBackground(object source)
        {
            for (var d = source as DependencyObject; d != null; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            {
                if (d is ButtonBase or Slider or Controls.SeekBar or TextBox or FrameworkElement { Name: "TimeText" }) return false;
                if (d is OverlayWindow) return true;
            }
            return true;
        }
        bar.MouseRightButtonUp += (_, e) =>
        {
            if (e.Handled || !OnBackground(e.OriginalSource)) return;
            ShowContextMenu(placementTarget: null);
            e.Handled = true;
        };
        bar.MouseLeftButtonDown += (_, e) =>
        {
            // The top bar handles its own double-click (maximize) and drag on its background.
            if (e.Handled || e.ClickCount != 2 || bar is TopBarWindow || !OnBackground(e.OriginalSource)) return;
            ToggleFullscreen();
            e.Handled = true;
        };
        bar.MouseDown += (_, e) =>
        {
            if (e.Handled || !OnBackground(e.OriginalSource)) return;
            if (e.ChangedButton is MouseButton.Middle or MouseButton.XButton1 or MouseButton.XButton2)
                VideoSurface_MouseDown(VideoSurface, e);
        };
        bar.MouseWheel += (_, e) =>
        {
            if (e.Handled || !OnBackground(e.OriginalSource)) return;
            VideoSurface_MouseWheel(VideoSurface, e);
        };
        bar.AllowDrop = true;
        bar.DragOver += OnDragOver;
        bar.Drop += OnDrop;
    }

    /// <summary>Lets the main window's resize border work through the overlays (windowed mode only).</summary>
    private bool IsOnResizeEdge(int screenX, int screenY)
    {
        if (_isFullscreen || WindowState != WindowState.Normal || _hwnd == 0) return false;
        if (!Win32.GetWindowRect(_hwnd, out var r)) return false;
        int edge = (int)Math.Round(6 * VisualTreeHelper.GetDpi(this).DpiScaleX);
        return screenX < r.Left + edge || screenX >= r.Right - edge || screenY < r.Top + edge || screenY >= r.Bottom - edge;
    }

    // ---- context menu -------------------------------------------------------------

    /// <summary>Opens the full player menu at the mouse (or under a control-bar button).</summary>
    public void ShowContextMenu(UIElement? placementTarget, string? section = null)
    {
        var menu = Menus.PlayerMenu.Build(this, section);
        OpenMenu(menu, placementTarget);
    }

    public void OpenMenu(ContextMenu menu, UIElement? placementTarget)
    {
        if (placementTarget != null)
        {
            menu.PlacementTarget = placementTarget;
            menu.Placement = PlacementMode.Top;
        }
        else
        {
            menu.Placement = PlacementMode.MousePoint;
        }
        _menuOpenCount++;
        ShowControls();
        menu.Closed += (_, _) =>
        {
            _menuOpenCount = Math.Max(0, _menuOpenCount - 1);
            _lastActivityTick = Environment.TickCount64;
            _menuClosedTick = Environment.TickCount64;
            // Clicking another app dismisses the menu too: activation then belongs to that app.
            if (IsVisible && !_closing && Win32.IsOwnProcessWindow(Win32.GetForegroundWindow())) Activate();
        };
        menu.IsOpen = true;
    }
}

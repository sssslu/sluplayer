using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shell;
using SluPlayer.Core.Interop;
using SluPlayer.Services.Settings;

namespace SluPlayer.UI;

// Window chrome, maximize/fullscreen, sizing to the video, always-on-top, taskbar integration.
public partial class MainWindow
{
    private bool _isFullscreen;
    private WindowState _preFullscreenState;
    private Rect _preFullscreenBounds;
    private bool _inSizeMove;
    private bool _wasMaximizedBeforeMinimize;

    /// <summary>
    /// The window should be maximized (remembered state, or the state before fullscreen) but a --noactivate start or
    /// command could not do that: maximizing always activates. Kept as the saved preference until the user maximizes.
    /// </summary>
    private bool _keepMaximized;

    public bool IsFullscreen => _isFullscreen;

    private nint WndProcHook(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        switch (msg)
        {
            case Win32.WM_GETMINMAXINFO:
                // Maximized custom-chrome windows must fit the monitor work area (not overflow it).
                if (!_isFullscreen) AdjustMaximizedBounds(hwnd, lParam);
                break;
            case Win32.WM_ENTERSIZEMOVE:
                _inSizeMove = true;
                break;
            case Win32.WM_EXITSIZEMOVE:
                _inSizeMove = false;
                PlaceOverlays();
                ShowControls();
                break;
            case Win32.WM_APPCOMMAND:
                if (HandleAppCommand(Win32.HiWord(lParam) & 0x0FFF)) { handled = true; return 1; }
                break;
        }
        return 0;
    }

    internal static void AdjustMaximizedBounds(nint hwnd, nint lParam)
    {
        var monitor = Win32.MonitorFromWindow(hwnd, Win32.MONITOR_DEFAULTTONEAREST);
        if (monitor == 0) return;
        var info = new Win32.MONITORINFO { cbSize = Marshal.SizeOf<Win32.MONITORINFO>() };
        if (!Win32.GetMonitorInfo(monitor, ref info)) return;
        var mmi = Marshal.PtrToStructure<Win32.MINMAXINFO>(lParam);
        var work = info.rcWork;
        var mon = info.rcMonitor;
        mmi.ptMaxPosition.X = work.Left - mon.Left;
        mmi.ptMaxPosition.Y = work.Top - mon.Top;
        mmi.ptMaxSize.X = work.Width;
        mmi.ptMaxSize.Y = work.Height;
        Marshal.StructureToPtr(mmi, lParam, true);
    }

    private void ApplyDwmStyle()
    {
        Win32.SetDwmInt(_hwnd, Win32.DWMWA_USE_IMMERSIVE_DARK_MODE, 1);
        Win32.SetDwmInt(_hwnd, Win32.DWMWA_WINDOW_CORNER_PREFERENCE, Win32.DWMWCP_ROUND);
        // COLORREF is 0x00BBGGRR: border #2A303B
        Win32.SetDwmInt(_hwnd, Win32.DWMWA_BORDER_COLOR, 0x003B302A);
    }

    private void OnWindowStateChanged()
    {
        if (WindowState == WindowState.Minimized)
        {
            HideOverlays();
            return;
        }
        _wasMaximizedBeforeMinimize = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Maximized) _keepMaximized = false;
        _topBar?.UpdateWindowState(WindowState == WindowState.Maximized, _isFullscreen);
        Dispatcher.BeginInvoke(() =>
        {
            PlaceOverlays();
            ShowControls();
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    public void ToggleMaximize()
    {
        if (_isFullscreen) { ExitFullscreen(); return; }
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    /// <summary>
    /// Leaves maximized/minimized. WPF restores with SW_RESTORE, which activates the window: for a --noactivate
    /// command restore with SW_SHOWNOACTIVATE first (WPF then sees the normal state and calls nothing itself).
    /// </summary>
    private void RestoreNormalState()
    {
        if (QuietCommand && _hwnd != 0)
        {
            // Minimized after maximized comes back maximized first: at most two steps.
            for (int i = 0; i < 2 && (Win32.IsIconic(_hwnd) || Win32.IsZoomed(_hwnd)); i++)
                Win32.ShowWindow(_hwnd, Win32.SW_SHOWNOACTIVATE);
        }
        if (WindowState != WindowState.Normal) WindowState = WindowState.Normal;
    }

    // ---- fullscreen -----------------------------------------------------------

    public void ToggleFullscreen()
    {
        if (_isFullscreen) ExitFullscreen();
        else EnterFullscreen();
    }

    public void EnterFullscreen()
    {
        if (_isFullscreen || _hwnd == 0) return;
        _preFullscreenState = WindowState;
        _preFullscreenBounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        _isFullscreen = true;

        var monitor = Win32.MonitorFromWindow(_hwnd, Win32.MONITOR_DEFAULTTONEAREST);
        var info = new Win32.MONITORINFO { cbSize = Marshal.SizeOf<Win32.MONITORINFO>() };
        Win32.GetMonitorInfo(monitor, ref info);
        var r = info.rcMonitor;

        Chrome.ResizeBorderThickness = new Thickness(0);
        Chrome.GlassFrameThickness = new Thickness(0);
        RestoreNormalState();
        ResizeMode = ResizeMode.NoResize;
        Win32.SetDwmInt(_hwnd, Win32.DWMWA_WINDOW_CORNER_PREFERENCE, Win32.DWMWCP_DONOTROUND);
        // No SWP_NOOWNERZORDER here: the owned overlay windows must move up together with us.
        // Without SWP_NOACTIVATE this activates the window, which a click on the (non-activating) control bar needs;
        // a --noactivate command only changes the size: no activation, and no raising over the user's windows.
        Win32.SetWindowPos(_hwnd, Win32.HWND_TOP, r.Left, r.Top, r.Width, r.Height,
            Win32.SWP_FRAMECHANGED | Win32.SWP_SHOWWINDOW | (QuietCommand ? Win32.SWP_NOACTIVATE | Win32.SWP_NOZORDER : 0));
        _topBar?.UpdateWindowState(false, true);
        _controlBar?.UpdateFullscreen(true);
        Dispatcher.BeginInvoke(() => { PlaceOverlays(); ShowControls(); }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    public void ExitFullscreen()
    {
        if (!_isFullscreen) return;
        _isFullscreen = false;
        ResizeMode = ResizeMode.CanResize;
        Chrome.ResizeBorderThickness = new Thickness(6);
        Chrome.GlassFrameThickness = new Thickness(0, 0, 0, 1);
        Win32.SetDwmInt(_hwnd, Win32.DWMWA_WINDOW_CORNER_PREFERENCE, Win32.DWMWCP_ROUND);
        if (_preFullscreenState == WindowState.Maximized)
        {
            Left = _preFullscreenBounds.Left; Top = _preFullscreenBounds.Top;
            Width = _preFullscreenBounds.Width; Height = _preFullscreenBounds.Height;
            // SW_MAXIMIZE always activates: a --noactivate command leaves the window at its normal bounds.
            if (QuietCommand) _keepMaximized = true;
            else WindowState = WindowState.Maximized;
        }
        else
        {
            Left = _preFullscreenBounds.Left; Top = _preFullscreenBounds.Top;
            Width = _preFullscreenBounds.Width; Height = _preFullscreenBounds.Height;
        }
        Win32.SetWindowPos(_hwnd, 0, 0, 0, 0, 0,
            Win32.SWP_FRAMECHANGED | Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE);
        _topBar?.UpdateWindowState(WindowState == WindowState.Maximized, false);
        _controlBar?.UpdateFullscreen(false);
        Dispatcher.BeginInvoke(() => { PlaceOverlays(); ShowControls(); }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    // ---- fit window to video ----------------------------------------------------

    private bool _mediaReady;
    private (int W, int H) _lastFitSize;

    /// <summary>
    /// Sizes the window to a newly opened video once its first frame is up (the engine refreshes the source size
    /// right then, so this never uses the previous file's size). Runs for files the user opened, and when the
    /// playlist moves on to a video of a different size; same-size episodes keep a manually resized window.
    /// </summary>
    private void TryAutoFit()
    {
        if (!_mediaReady || !_player.HasVideo) return;
        int w = _player.SourceWidth, h = _player.SourceHeight;
        if (w <= 0 || h <= 0) return;
        if (!_autoFitPending && (w, h) == _lastFitSize) return;
        _autoFitPending = false;
        _lastFitSize = (w, h);
        if (_settings.AutoFitWindow && !_isFullscreen && WindowState == WindowState.Normal)
            FitWindowToVideo(1.0);
    }

    /// <summary>Resizes the window so the video area shows the video at <paramref name="scale"/> (1 = 100%, clamped to 85% of the work area).</summary>
    public void FitWindowToVideo(double scale)
    {
        // Source size (display size of the decoded video, rotation applied).
        int vw = _player.SourceWidth > 0 ? _player.SourceWidth : _player.VideoWidth;
        int vh = _player.SourceHeight > 0 ? _player.SourceHeight : _player.VideoHeight;
        if (vw <= 0 || vh <= 0 || _hwnd == 0 || _isFullscreen) return;
        RestoreNormalState();

        var dpi = VisualTreeHelper.GetDpi(this);
        var monitor = Win32.MonitorFromWindow(_hwnd, Win32.MONITOR_DEFAULTTONEAREST);
        var info = new Win32.MONITORINFO { cbSize = Marshal.SizeOf<Win32.MONITORINFO>() };
        Win32.GetMonitorInfo(monitor, ref info);
        double workW = info.rcWork.Width / dpi.DpiScaleX, workH = info.rcWork.Height / dpi.DpiScaleY;
        double workL = info.rcWork.Left / dpi.DpiScaleX, workT = info.rcWork.Top / dpi.DpiScaleY;

        double panel = PlaylistPanelHost.Visibility == Visibility.Visible ? PlaylistPanelHost.ActualWidth : 0;
        // Video pixels → DIPs (1:1 physical pixels at 100%).
        double w = vw * scale / dpi.DpiScaleX, h = vh * scale / dpi.DpiScaleY;
        double maxW = workW * 0.85 - panel, maxH = workH * 0.85;
        double minW = 480, minH = 270;
        double k = Math.Min(1, Math.Min(maxW / w, maxH / h));
        w *= k; h *= k;
        if (w < minW && h < minH)
        {
            double up = Math.Min(minW / w, minH / h);
            w *= up; h *= up;
        }
        double newW = Math.Round(w + panel), newH = Math.Round(h);

        // Keep the window centred where it was, clamped to the work area.
        double cx = Left + Width / 2, cy = Top + Height / 2;
        double left = Math.Clamp(cx - newW / 2, workL, Math.Max(workL, workL + workW - newW));
        double top = Math.Clamp(cy - newH / 2, workT, Math.Max(workT, workT + workH - newH));
        Left = left; Top = top; Width = newW; Height = newH;
    }

    // ---- always on top ----------------------------------------------------------

    public void ToggleOnTop()
    {
        _settings.OnTop = _settings.OnTop == OnTopMode.Never ? OnTopMode.Always : OnTopMode.Never;
        ApplyOnTop();
        SaveSettings();
        ShowToast(_settings.OnTop == OnTopMode.Always ? "항상 위 켬" : "항상 위 끔");
    }

    public void SetOnTopMode(OnTopMode mode)
    {
        _settings.OnTop = mode;
        ApplyOnTop();
        SaveSettings();
    }

    private void ApplyOnTop()
    {
        bool top = _settings.OnTop switch
        {
            OnTopMode.Always => true,
            OnTopMode.WhilePlaying => _player.IsPlaying,
            _ => false,
        };
        if (Topmost != top) Topmost = top;
        _topBar?.SetOnTop(_settings.OnTop != OnTopMode.Never);
        if (_controlBar != null) _controlBar.Topmost = top;
        if (_topBar != null) _topBar.Topmost = top;
        // The download/settings windows rise above a pinned player only while they are active (see PageWindow).
        _downloadWindow?.SyncPinned(top);
        _settingsWindow?.SyncPinned(top);
    }

    private void Win32Foreground()
    {
        if (_hwnd != 0) Win32.SetForegroundWindow(_hwnd);
    }

    // ---- window bounds persistence ------------------------------------------------

    private void RestoreWindowBounds(bool noActivate)
    {
        var b = _settings.Window;
        if (b == null || b.Width < 300 || b.Height < 200) return;
        // Only restore if the rectangle is still (mostly) on a screen.
        var vs = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var r = new Rect(b.Left, b.Top, b.Width, b.Height);
        if (!vs.IntersectsWith(r) || r.Left < vs.Left - 50 || r.Top < vs.Top - 20) return;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = b.Left; Top = b.Top; Width = b.Width; Height = b.Height;
        // WPF cannot show a window maximized without activating it (Show() throws when ShowActivated is false):
        // a --noactivate start opens at the normal bounds and keeps the preference for the next start.
        if (b.Maximized)
        {
            if (noActivate) _keepMaximized = true;
            else WindowState = WindowState.Maximized;
        }
    }

    private void SaveWindowBounds()
    {
        Rect r;
        bool maximized;
        if (_isFullscreen)
        {
            r = _preFullscreenBounds;
            maximized = _preFullscreenState == WindowState.Maximized;
        }
        else
        {
            r = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            // Closed while minimized: remember the state it will be restored to.
            maximized = WindowState == WindowState.Maximized
                        || (WindowState == WindowState.Minimized && _wasMaximizedBeforeMinimize);
        }
        maximized |= _keepMaximized;
        if (r.Width > 0 && r.Height > 0 && !double.IsInfinity(r.Width))
            _settings.Window = new WindowBounds { Left = r.Left, Top = r.Top, Width = r.Width, Height = r.Height, Maximized = maximized };
    }

    // ---- taskbar: thumbnail buttons + progress -------------------------------------

    private ThumbButtonInfo? _thumbPlay;

    private void InitTaskbarButtons()
    {
        ThumbButtonInfo Make(string iconKey, string tip, Action click)
        {
            var b = new ThumbButtonInfo { Description = tip, ImageSource = GlyphImage.Render((string)FindResource(iconKey)) };
            b.Click += (_, _) => click();
            return b;
        }
        _thumbPlay = Make("Icon.Play", "재생", () => _player.PlayPause());
        Taskbar.ThumbButtonInfos = new ThumbButtonInfoCollection
        {
            Make("Icon.Previous", "이전", () => _player.Previous()),
            _thumbPlay,
            Make("Icon.Next", "다음", () => _player.Next()),
        };
        UpdateTaskbar();
    }

    private void UpdateTaskbar()
    {
        if (_thumbPlay == null) return;
        bool playing = _player.IsPlaying;
        _thumbPlay.ImageSource = GlyphImage.Render((string)FindResource(playing ? "Icon.Pause" : "Icon.Play"));
        _thumbPlay.Description = playing ? "일시정지" : "재생";
        foreach (var b in Taskbar.ThumbButtonInfos) b.IsEnabled = _player.HasMedia || b == _thumbPlay;
        UpdateTaskbarProgress();
    }

    private void UpdateTaskbarProgress()
    {
        if (!_player.HasMedia || _player.Duration <= 0)
        {
            if (Taskbar.ProgressState != TaskbarItemProgressState.None) Taskbar.ProgressState = TaskbarItemProgressState.None;
            return;
        }
        var state = _player.IsPaused ? TaskbarItemProgressState.Paused : TaskbarItemProgressState.Normal;
        if (Taskbar.ProgressState != state) Taskbar.ProgressState = state;
        Taskbar.ProgressValue = Math.Clamp(_player.Position / _player.Duration, 0, 1);
    }

    private bool HandleAppCommand(int cmd)
    {
        switch (cmd)
        {
            case Win32.APPCOMMAND_MEDIA_PLAY_PAUSE: _player.PlayPause(); return true;
            case Win32.APPCOMMAND_MEDIA_PLAY: _player.Play(); return true;
            case Win32.APPCOMMAND_MEDIA_PAUSE: _player.Pause(); return true;
            case Win32.APPCOMMAND_MEDIA_STOP: _player.Stop(); return true;
            case Win32.APPCOMMAND_MEDIA_NEXTTRACK: _player.Next(); return true;
            case Win32.APPCOMMAND_MEDIA_PREVIOUSTRACK: _player.Previous(); return true;
        }
        return false;
    }
}

/// <summary>Renders an icon-font glyph into a small bitmap (taskbar thumbnail buttons need ImageSource).</summary>
internal static class GlyphImage
{
    private static readonly Dictionary<string, ImageSource> Cache = new();

    public static ImageSource Render(string glyph, double size = 32)
    {
        if (Cache.TryGetValue(glyph, out var img)) return img;
        var tf = new Typeface(new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var ft = new FormattedText(glyph, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, size * 0.62, Brushes.White, 1.0);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
            dc.DrawText(ft, new Point((size - ft.Width) / 2, (size - ft.Height) / 2));
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)size, (int)size, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv);
        bmp.Freeze();
        Cache[glyph] = bmp;
        return bmp;
    }
}

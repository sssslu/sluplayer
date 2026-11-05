using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using SluPlayer.Core.Interop;

namespace SluPlayer.UI;

/// <summary>
/// Resizable dark window that hosts one page (the download view or the settings view) next to the player.
/// The main window keeps one instance of each; closing only hides it, so the page keeps its state until the app exits.
/// </summary>
public sealed class PageWindow : Window
{
    private readonly StackPanel _toasts;
    private readonly Button _maxButton;
    private bool _exiting;
    private bool _wasMaximized;

    public PageWindow(string title, string iconKey, FrameworkElement page, double width, double height)
    {
        Page = page;
        Title = title;
        Width = width;
        Height = height;
        MinWidth = 760;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        UseLayoutRounding = true;
        SetResourceReference(BackgroundProperty, "Brush.Bg");
        SetResourceReference(ForegroundProperty, "Brush.Text");
        SetResourceReference(FontFamilyProperty, "Font.UI");
        SetResourceReference(FontSizeProperty, "FontSize.Body");
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 40,
            ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(0, 0, 0, 1),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        });

        // Title bar: icon + title, minimize / maximize / close.
        var glyph = new TextBlock { Text = (string)FindResource(iconKey), FontSize = 15, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        glyph.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Icons");
        glyph.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Accent");
        var caption = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(16, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Children = { glyph, new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center } },
        };
        _maxButton = ChromeButton("Btn.Chrome", "Icon.Maximize", "최대화", ToggleMaximize);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children =
            {
                ChromeButton("Btn.Chrome", "Icon.Minimize", "최소화", () => WindowState = WindowState.Minimized),
                _maxButton,
                ChromeButton("Btn.ChromeClose", "Icon.ChromeClose", "닫기", Close),
            },
        };
        WindowChrome.SetIsHitTestVisibleInChrome(buttons, true);
        var bar = new Grid { Height = 40, Children = { caption, buttons } };
        bar.SetResourceReference(Panel.BackgroundProperty, "Brush.Surface1");
        DockPanel.SetDock(bar, Dock.Top);

        _toasts = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 20, 20),
            IsHitTestVisible = false,
        };
        Content = new DockPanel { Children = { bar, new Grid { Children = { page, _toasts } } } };

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            Win32.SetDwmInt(hwnd, Win32.DWMWA_USE_IMMERSIVE_DARK_MODE, 1);
            Win32.SetDwmInt(hwnd, Win32.DWMWA_WINDOW_CORNER_PREFERENCE, Win32.DWMWCP_ROUND);
            Win32.SetDwmInt(hwnd, Win32.DWMWA_BORDER_COLOR, 0x003B302A);
            HwndSource.FromHwnd(hwnd)?.AddHook(WndProcHook);
        };
        StateChanged += (_, _) =>
        {
            bool max = WindowState == WindowState.Maximized;
            if (WindowState != WindowState.Minimized) _wasMaximized = max;
            _maxButton.Content = FindResource(max ? "Icon.Restore" : "Icon.Maximize");
            _maxButton.ToolTip = max ? "이전 크기로" : "최대화";
        };
    }

    /// <summary>The hosted view (OnlineView / SettingsView).</summary>
    public FrameworkElement Page { get; }

    public void ShowToast(string message, ToastKind kind) => Toasts.Show(_toasts, message, kind);

    /// <summary>Restores a minimized window to its previous state and activates it (user requests only).</summary>
    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized) WindowState = _wasMaximized ? WindowState.Maximized : WindowState.Normal;
        Activate();
    }

    /// <summary>Really closes the window (app exit); a normal close only hides it.</summary>
    public void CloseForExit()
    {
        _exiting = true;
        Close();
    }

    // ---- 항상 위 -------------------------------------------------------------------------------
    // A pinned (topmost) player would cover this window. While the user works in it, the window joins the topmost band;
    // as soon as they switch away it leaves it again, so it never floats over other apps. WPF's Topmost isn't used:
    // clearing it sends HWND_NOTOPMOST, which raises the window over the app the user just switched to.

    /// <summary>Whether the player is pinned right now (set by the main window).</summary>
    public Func<bool>? PlayerPinned { get; set; }

    private bool _pinned;

    /// <summary>The player's 항상 위 state changed.</summary>
    public void SyncPinned(bool playerPinned)
    {
        if (IsActive) SetPinned(playerPinned);
        else if (_pinned && !playerPinned) SetPinned(false);
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        if (PlayerPinned?.Invoke() == true) SetPinned(true);
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        // Switching to our own windows (a dialog this window opened, or the player, which rises above anyway) keeps the
        // pin: dropping it would take the owned dialog out of the band and hide it behind the pinned player.
        // Only another app takes it away (see Unpin).
        if (_pinned) Dispatcher.BeginInvoke(() =>
        {
            if (!IsActive && _pinned && !Win32.IsOwnProcessWindow(Win32.GetForegroundWindow())) SetPinned(false);
        }, DispatcherPriority.Input);
    }

    /// <summary>The app lost activation (another app is in front now): leave the topmost band below it.</summary>
    public void Unpin()
    {
        if (_pinned) Dispatcher.BeginInvoke(() => { if (_pinned && !IsActive) SetPinned(false); }, DispatcherPriority.Input);
    }

    private void SetPinned(bool on)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == 0) return;
        _pinned = on;
        nint after = Win32.HWND_TOPMOST;
        if (!on)
        {
            // Below the window the user switched to (a window placed under a non-topmost one leaves the topmost band).
            // Our own player, a topmost window or no foreground window: just leave the band at its top.
            var fg = Win32.GetForegroundWindow();
            bool fgTopmost = fg != 0 && (Win32.GetWindowLongPtr(fg, Win32.GWL_EXSTYLE) & WS_EX_TOPMOST) != 0;
            after = fg == 0 || fg == hwnd || fgTopmost || Win32.IsOwnProcessWindow(fg) ? Win32.HWND_NOTOPMOST : fg;
        }
        Win32.SetWindowPos(hwnd, after, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
    }

    private const nint WS_EX_TOPMOST = 0x0008;

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_exiting)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }

    private void ToggleMaximize() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private Button ChromeButton(string style, string icon, string tip, Action click)
    {
        var b = new Button { Style = (Style)FindResource(style), Content = FindResource(icon), ToolTip = tip, Height = 40 };
        b.Click += (_, _) => click();
        return b;
    }

    private static nint WndProcHook(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        // Maximized custom-chrome windows must fit the monitor work area (not overflow it).
        if (msg == Win32.WM_GETMINMAXINFO) MainWindow.AdjustMaximizedBounds(hwnd, lParam);
        return 0;
    }
}

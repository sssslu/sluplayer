using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using SluPlayer.Core.Interop;

namespace SluPlayer.UI.Overlay;

/// <summary>
/// Transparent, non-activating window owned by the main window and floated over the video (which is a
/// native child HWND that WPF content cannot overlap). Controls inside must not be focusable so clicking
/// them never steals activation/keyboard focus from the main window.
/// </summary>
public class OverlayWindow : Window
{
    private nint _hwnd;
    private bool _shown;
    private bool _fadingOut;

    public OverlayWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        Focusable = false;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        Opacity = 0;
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            var ex = (uint)Win32.GetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE);
            Win32.SetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE,
                (nint)(ex | Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOOLWINDOW));
            HwndSource.FromHwnd(_hwnd)?.AddHook(HitTestHook);
        };
    }

    /// <summary>Screen-pixel test; when it returns true the point falls through to windows below (same thread).</summary>
    public Func<int, int, bool>? PassThroughTest { get; set; }

    private nint HitTestHook(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == Win32.WM_NCHITTEST && PassThroughTest != null)
        {
            int x = Win32.LoWord(lParam), y = Win32.HiWord(lParam);
            if (PassThroughTest(x, y))
            {
                handled = true;
                return Win32.HTTRANSPARENT;
            }
        }
        return 0;
    }

    /// <summary>True while visible or fading in (not while fading out).</summary>
    public bool IsShownLogically => _shown && !_fadingOut;

    /// <summary>Moves/resizes the window in physical screen pixels (DPI independent).</summary>
    public void PlaceAt(int x, int y, int width, int height)
    {
        if (_hwnd == 0)
        {
            // Not created yet: create hidden so we have an HWND to position.
            new WindowInteropHelper(this).EnsureHandle();
            _hwnd = new WindowInteropHelper(this).Handle;
        }
        Win32.SetWindowPos(_hwnd, 0, x, y, Math.Max(1, width), Math.Max(1, height),
            Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE | Win32.SWP_NOOWNERZORDER);
        EnsureAboveOwner();
    }

    /// <summary>
    /// Owned windows normally stay above their owner, but some SetWindowPos calls on the owner can leave them
    /// underneath. Re-insert this window directly above the owner without disturbing other applications.
    /// </summary>
    public void EnsureAboveOwner()
    {
        var owner = Owner is { } o ? new WindowInteropHelper(o).Handle : 0;
        if (owner == 0 || _hwnd == 0) return;
        // Walk upwards from the owner: if we are found above it, the order is already fine.
        for (nint w = Win32.GetWindow(owner, Win32.GW_HWNDPREV); w != 0; w = Win32.GetWindow(w, Win32.GW_HWNDPREV))
            if (w == _hwnd) return;
        nint above = Win32.GetWindow(owner, Win32.GW_HWNDPREV);
        Win32.SetWindowPos(_hwnd, above == 0 ? Win32.HWND_TOP : above, 0, 0, 0, 0,
            Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE | Win32.SWP_NOOWNERZORDER);
    }

    public void FadeIn(int ms = 140)
    {
        if (_shown && !_fadingOut) return;
        _shown = true;
        _fadingOut = false;
        if (!IsVisible) Show();
        var anim = new DoubleAnimation(1, TimeSpan.FromMilliseconds(ms));
        BeginAnimation(OpacityProperty, anim);
    }

    public void FadeOut(int ms = 260)
    {
        if (!_shown || _fadingOut) return;
        _fadingOut = true;
        var anim = new DoubleAnimation(0, TimeSpan.FromMilliseconds(ms));
        anim.Completed += (_, _) =>
        {
            if (!_fadingOut) return; // re-shown meanwhile
            _fadingOut = false;
            _shown = false;
            Hide();
        };
        BeginAnimation(OpacityProperty, anim);
    }

    public void HideNow()
    {
        BeginAnimation(OpacityProperty, null);
        Opacity = 0;
        _shown = false;
        _fadingOut = false;
        if (IsVisible) Hide();
    }
}

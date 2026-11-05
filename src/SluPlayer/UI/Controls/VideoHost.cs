using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using SluPlayer.Core.Interop;

namespace SluPlayer.UI.Controls;

/// <summary>
/// Child HWND that mpv renders into (passed as <c>wid</c>). mpv creates its own disabled child window inside
/// it, so mouse input falls through to us; we answer WM_NCHITTEST with HTTRANSPARENT so it continues to the
/// WPF window underneath, where all input handling lives.
/// </summary>
public sealed class VideoHost : HwndHost
{
    private const string ClassName = "SluPlayerVideoHost";
    private static readonly Win32.WndProc s_wndProc = (h, m, w, l) => Win32.DefWindowProc(h, m, w, l);
    private static bool s_registered;
    private nint _hwnd;

    /// <summary>The HWND to give mpv as <c>wid</c>; 0 until the host is built.</summary>
    public nint VideoHwnd => _hwnd;

    public event EventHandler? HwndCreated;

    public VideoHost()
    {
        // WPF hit-testing should go to the element underneath (the video input surface).
        IsHitTestVisible = false;
        Focusable = false;
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        EnsureClass();
        _hwnd = Win32.CreateWindowEx(
            Win32.WS_EX_NOPARENTNOTIFY, ClassName, "SluPlayer Video",
            Win32.WS_CHILD | Win32.WS_VISIBLE | Win32.WS_CLIPCHILDREN | Win32.WS_CLIPSIBLINGS,
            0, 0, Math.Max(1, (int)ActualWidth), Math.Max(1, (int)ActualHeight),
            hwndParent.Handle, 0, Win32.GetModuleHandle(null), 0);
        if (_hwnd == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateWindowEx failed for the video host");
        Dispatcher.BeginInvoke(() => HwndCreated?.Invoke(this, EventArgs.Empty));
        return new HandleRef(this, _hwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        Win32.DestroyWindow(hwnd.Handle);
        _hwnd = 0;
    }

    protected override nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        switch (msg)
        {
            case Win32.WM_NCHITTEST:
                handled = true;
                return Win32.HTTRANSPARENT;
        }
        return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
    }

    /// <summary>mpv disables its own child window; enforce it in case a build doesn't.</summary>
    public void EnsureChildrenDisabled()
    {
        if (_hwnd == 0) return;
        Win32.EnumChildWindows(_hwnd, (child, _) =>
        {
            if (Win32.IsWindowEnabled(child)) Win32.EnableWindow(child, false);
            return true;
        }, 0);
    }

    private static void EnsureClass()
    {
        if (s_registered) return;
        var wc = new Win32.WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<Win32.WNDCLASSEX>(),
            style = Win32.CS_HREDRAW | Win32.CS_VREDRAW,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(s_wndProc),
            hInstance = Win32.GetModuleHandle(null),
            hCursor = Win32.LoadCursor(0, Win32.IDC_ARROW),
            hbrBackground = Win32.GetStockObject(Win32.BLACK_BRUSH),
            lpszClassName = ClassName,
        };
        if (Win32.RegisterClassEx(ref wc) == 0)
        {
            int err = Marshal.GetLastWin32Error();
            const int ERROR_CLASS_ALREADY_EXISTS = 1410;
            if (err != ERROR_CLASS_ALREADY_EXISTS) throw new Win32Exception(err, "RegisterClassEx failed for the video host");
        }
        s_registered = true;
    }
}

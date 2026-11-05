using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using SluPlayer.Core.Interop;

namespace SluPlayer.UI.Dialogs;

/// <summary>
/// Base class for SluPlayer dialogs: dark custom title bar (title + optional icon glyph + close button),
/// Esc closes, centred on the owner, no taskbar entry. Set <see cref="Window.Content"/> as usual.
/// </summary>
public class DialogWindow : Window
{
    public static readonly DependencyProperty DialogIconProperty = DependencyProperty.Register(
        nameof(DialogIcon), typeof(string), typeof(DialogWindow), new PropertyMetadata(""));

    /// <summary>Optional Segoe Fluent Icons glyph shown before the title (use an Icon.* resource).</summary>
    public string DialogIcon
    {
        get => (string)GetValue(DialogIconProperty);
        set => SetValue(DialogIconProperty, value);
    }

    public DialogWindow()
    {
        SetResourceReference(StyleProperty, "Window.SluDialog");
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 44,
            ResizeBorderThickness = new Thickness(4),
            GlassFrameThickness = new Thickness(0, 0, 0, 1),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        });
        CommandBindings.Add(new CommandBinding(SystemCommands.CloseWindowCommand, (_, _) => Close()));
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            Win32.SetDwmInt(hwnd, Win32.DWMWA_USE_IMMERSIVE_DARK_MODE, 1);
            Win32.SetDwmInt(hwnd, Win32.DWMWA_WINDOW_CORNER_PREFERENCE, Win32.DWMWCP_ROUND);
            Win32.SetDwmInt(hwnd, Win32.DWMWA_BORDER_COLOR, 0x003B302A);
        };
        // WPF can size a SizeToContent + WindowChrome window before the frame is removed (black strip).
        FixChromeSizing(this);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        // Esc from a hosted native window belongs to it: WebView2 (an HwndHost) re-raises every Esc pressed in the
        // page (pop-ups, menus, find bar on the YouTube sign-in page) as a key event on itself.
        if (!e.Handled && e.Key == Key.Escape && e.OriginalSource is not HwndHost)
        {
            e.Handled = true;
            Close();
        }
    }

    /// <summary>
    /// WPF can size a SizeToContent window for the standard non-client frame before WindowChrome removes that frame
    /// (it depends on timing), leaving an unpainted black strip right and bottom (16×39 px at 100%). The window is
    /// kept cloaked (invisible, DWM) until its first frame has rendered; any mismatch between the window and its
    /// root visual is then corrected by re-running SizeToContent, keeping the window centred where it was placed.
    /// </summary>
    private static void FixChromeSizing(Window w)
    {
        const int DWMWA_CLOAK = 13;
        nint hwnd = 0;
        bool cloaked = false, busy = false, watching = true;

        void Uncloak()
        {
            if (!cloaked) return;
            cloaked = false;
            Win32.SetDwmInt(hwnd, DWMWA_CLOAK, 0);
        }

        void Check(object? sender, EventArgs e)
        {
            if (busy || !watching || w.SizeToContent != SizeToContent.WidthAndHeight || VisualTreeHelper.GetChildrenCount(w) == 0) return;
            if (VisualTreeHelper.GetChild(w, 0) is not FrameworkElement root || root.ActualWidth <= 0) return;
            double oldW = w.ActualWidth, oldH = w.ActualHeight;
            if (oldW - root.ActualWidth < 0.5 && oldH - root.ActualHeight < 0.5) return;
            busy = true;
            try
            {
                w.SizeToContent = SizeToContent.Manual;
                w.SizeToContent = SizeToContent.WidthAndHeight;
                w.UpdateLayout();
                w.Left += (oldW - w.ActualWidth) / 2;
                w.Top += (oldH - w.ActualHeight) / 2;
            }
            finally { busy = false; }
        }

        void StopWatching()
        {
            watching = false;
            w.LayoutUpdated -= Check;
        }

        w.SourceInitialized += (_, _) =>
        {
            hwnd = new WindowInteropHelper(w).Handle;
            Win32.SetDwmInt(hwnd, DWMWA_CLOAK, 1);
            cloaked = true;
            // Safety net: never leave a dialog invisible, whatever happens during the first render.
            var safety = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            safety.Tick += (_, _) => { safety.Stop(); Uncloak(); };
            safety.Start();
        };
        // LayoutUpdated is dispatcher-wide: only watch while the dialog settles.
        w.LayoutUpdated += Check;
        w.ContentRendered += (_, _) => w.Dispatcher.BeginInvoke(() =>
        {
            Check(null, EventArgs.Empty);
            Uncloak();
            var settle = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            settle.Tick += (_, _) => { settle.Stop(); Check(null, EventArgs.Empty); StopWatching(); };
            settle.Start();
        }, DispatcherPriority.Background);
        w.Closed += (_, _) => StopWatching();
    }
}

using System.Windows;
using System.Windows.Controls;
using SluPlayer.ViewModels;

namespace SluPlayer.UI.Views;

/// <summary>Settings: one short page of rows. All values live in <see cref="SettingsViewModel"/>.</summary>
public partial class SettingsView : UserControl, IPage, IDisposable
{
    private readonly SettingsViewModel _vm;
    private Window? _hostWindow;
    private bool _disposed;

    public SettingsView(IShell shell)
    {
        _vm = new SettingsViewModel(shell);
        InitializeComponent();
        DataContext = _vm;
        Loaded += OnLoaded;
        Unloaded += (_, _) => DetachWindow();
    }

    public void OnNavigatedTo()
    {
        if (!_disposed) _vm.Refresh();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var window = Window.GetWindow(this);
        if (window == _hostWindow) return;
        DetachWindow();
        _hostWindow = window;
        // Settings can change behind our back (menus, shortcuts like Ctrl+T, the sign-in window): re-read on return.
        if (window != null) window.Activated += OnHostActivated;
    }

    private void DetachWindow()
    {
        if (_hostWindow == null) return;
        _hostWindow.Activated -= OnHostActivated;
        _hostWindow = null;
    }

    private void OnHostActivated(object? sender, EventArgs e)
    {
        if (!_disposed && IsVisible) _vm.Refresh();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DetachWindow();
        _vm.Dispose();
    }
}

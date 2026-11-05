using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using SluPlayer.Core.Interop;
using SluPlayer.Core.Player;

namespace SluPlayer.UI.Overlay;

public partial class TopBarWindow : OverlayWindow
{
    public const double HeightDip = 64;

    private readonly MainWindow _shell;
    private readonly PlayerEngine _player;

    public TopBarWindow(MainWindow shell)
    {
        _shell = shell;
        _player = shell.Player;
        InitializeComponent();
        _player.PropertyChanged += OnPlayerChanged;
        UpdateTitle();
    }

    public void UpdateWindowState(bool maximized, bool fullscreen)
    {
        MaxButton.Content = FindResource(maximized ? "Icon.Restore" : "Icon.Maximize");
        MaxButton.ToolTip = maximized ? "이전 크기로" : "최대화";
        MinButton.Visibility = fullscreen ? Visibility.Collapsed : Visibility.Visible;
        MaxButton.Visibility = fullscreen ? Visibility.Collapsed : Visibility.Visible;
        ExitFsButton.Visibility = fullscreen ? Visibility.Visible : Visibility.Collapsed;
    }

    public void SetOnTop(bool on) => OnTopToggle.IsChecked = on;

    private void OnPlayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PlayerEngine.MediaTitle) or nameof(PlayerEngine.CurrentPath)
            or nameof(PlayerEngine.Artist) or nameof(PlayerEngine.PlaylistPos) or nameof(PlayerEngine.PlaylistCount))
            UpdateTitle();
    }

    private void UpdateTitle()
    {
        var title = _player.MediaTitle;
        if (string.IsNullOrWhiteSpace(title) && _player.CurrentPath != null)
            title = MediaFormats.IsUrl(_player.CurrentPath) ? _player.CurrentPath : Path.GetFileName(_player.CurrentPath);
        TitleText.Text = title ?? "";
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(_player.Artist)) parts.Add(_player.Artist!);
        if (_player.PlaylistCount > 1) parts.Add($"{_player.PlaylistPos + 1} / {_player.PlaylistCount}");
        SubText.Text = string.Join("  ·  ", parts);
        SubText.Visibility = parts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not (System.Windows.Controls.Grid or System.Windows.Controls.Border
            or System.Windows.Controls.TextBlock or System.Windows.Controls.DockPanel or System.Windows.Controls.StackPanel))
            return;
        if (e.ClickCount == 2)
        {
            _shell.ToggleMaximize();
            e.Handled = true;
            return;
        }
        if (_shell.IsFullscreen) return;
        // Drag the main window by its (overlaid) title bar.
        var main = new WindowInteropHelper(_shell).Handle;
        Win32.ReleaseCapture();
        Win32.SendMessage(main, Win32.WM_NCLBUTTONDOWN, Win32.HTCAPTION, 0);
        e.Handled = true;
    }

    private void Download_Click(object sender, RoutedEventArgs e) => _shell.ShowDownloads();
    private void Settings_Click(object sender, RoutedEventArgs e) => _shell.ShowSettings();
    private void OnTop_Click(object sender, RoutedEventArgs e) => _shell.ToggleOnTop();
    private void Min_Click(object sender, RoutedEventArgs e) => _shell.WindowState = WindowState.Minimized;
    private void Max_Click(object sender, RoutedEventArgs e) => _shell.ToggleMaximize();
    private void ExitFs_Click(object sender, RoutedEventArgs e) => _shell.ExitFullscreen();
    private void Close_Click(object sender, RoutedEventArgs e) => _shell.RequestClose();
}

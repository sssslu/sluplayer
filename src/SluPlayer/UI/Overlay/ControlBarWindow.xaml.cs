using System.ComponentModel;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using SluPlayer.Core;
using SluPlayer.Core.Player;
using SluPlayer.UI.Controls;

namespace SluPlayer.UI.Overlay;

public partial class ControlBarWindow : OverlayWindow
{
    public const double HeightDip = 112;

    private readonly MainWindow _shell;
    private readonly PlayerEngine _player;
    private bool _updatingVolume;

    public ControlBarWindow(MainWindow shell)
    {
        _shell = shell;
        _player = shell.Player;
        InitializeComponent();
        DataContext = _player;
        Typography.SetNumeralAlignment(TimeText, FontNumeralAlignment.Tabular);
        _player.PropertyChanged += OnPlayerChanged;
        Seek.SeekRequested += (_, e) =>
        {
            _player.SeekTo(e.Time, e.Exact);
            UpdateTimeText(e.Time);
        };
        Seek.HoverChanged += OnSeekHover;
        Seek.DragCompleted += (_, _) => _shell.NoteActivity(false);
        // Wheel over the seek bar seeks, over the volume slider changes volume: one step per whole notch, like over
        // the video (touchpads and smooth wheels send many small deltas).
        Seek.PreviewMouseWheel += (_, e) =>
        {
            e.Handled = true;
            int steps = _shell.WheelSteps(e.Delta);
            if (steps != 0) _player.SeekRelative(steps * 5);
        };
        VolumeSlider.PreviewMouseWheel += (_, e) =>
        {
            e.Handled = true;
            int steps = _shell.WheelSteps(e.Delta);
            if (steps != 0) _player.ChangeVolume(steps * 5);
        };
        VolumeButton.PreviewMouseWheel += (_, e) =>
        {
            e.Handled = true;
            int steps = _shell.WheelSteps(e.Delta);
            if (steps != 0) _player.ChangeVolume(steps * 5);
        };
        RefreshAll();
    }

    private bool _showRemaining;

    private void TimeText_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _showRemaining = !_showRemaining;
        TimeText.ToolTip = _showRemaining ? "전체 시간" : "남은 시간";
        UpdateDurationText();
        e.Handled = true;
    }

    private void UpdateDurationText()
    {
        if (_player.Duration <= 0)
        {
            DurRun.Text = _player.IsNetworkStream ? "LIVE" : "0:00";
            return;
        }
        DurRun.Text = _showRemaining
            ? "-" + Formatters.Time(Math.Max(0, _player.Duration - _player.Position), forceHours: _player.Duration >= 3600)
            : Formatters.Time(_player.Duration);
    }

    /// <summary>True while the user drags the seek bar or the volume slider — prevents auto-hide.</summary>
    public bool IsBusy => Seek.IsDragging || VolumeSlider.IsMouseCaptureWithin;

    public void UpdateFullscreen(bool fullscreen)
    {
        FullscreenButton.Content = FindResource(fullscreen ? "Icon.ExitFullScreen" : "Icon.FullScreen");
        FullscreenButton.ToolTip = fullscreen ? "전체 화면 종료 (Esc)" : "전체 화면 (Enter)";
    }

    public void ClosePopups() => PreviewPopup.IsOpen = false;

    // ---- state -> UI -----------------------------------------------------------

    private void RefreshAll()
    {
        foreach (var name in new[]
                 {
                     nameof(PlayerEngine.Position), nameof(PlayerEngine.Duration), nameof(PlayerEngine.IsPaused),
                     nameof(PlayerEngine.Volume), nameof(PlayerEngine.Speed), nameof(PlayerEngine.CurrentChapter),
                     nameof(PlayerEngine.PlaylistCount), nameof(PlayerEngine.SubtitleTracks), nameof(PlayerEngine.AudioTracks),
                 })
            OnPlayerChanged(this, new PropertyChangedEventArgs(name));
    }

    private void OnPlayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PlayerEngine.Position):
                if (!Seek.IsDragging)
                {
                    Seek.Value = _player.Position;
                    UpdateTimeText(_player.Position);
                    if (_showRemaining) UpdateDurationText();
                }
                break;
            case nameof(PlayerEngine.Duration):
            case nameof(PlayerEngine.IsSeekable):
                UpdateDurationText();
                Seek.IsEnabled = _player.IsSeekable;
                break;
            case nameof(PlayerEngine.IsPaused):
            case nameof(PlayerEngine.IsIdle):
            case nameof(PlayerEngine.EofReached):
                bool playing = _player.IsPlaying && !_player.EofReached;
                PlayPauseButton.Content = FindResource(playing ? "Icon.Pause" : "Icon.Play");
                PlayPauseButton.ToolTip = playing ? "일시정지 (Space)" : "재생 (Space)";
                break;
            case nameof(PlayerEngine.Volume):
            case nameof(PlayerEngine.IsMuted):
            case nameof(PlayerEngine.VolumeMax):
                UpdateVolume();
                break;
            case nameof(PlayerEngine.Speed):
                SpeedText.Text = $"{_player.Speed:0.0#}x";
                SpeedText.Foreground = Math.Abs(_player.Speed - 1) > 0.001 ? (Brush)FindResource("Brush.Accent") : Brushes.White;
                break;
            case nameof(PlayerEngine.CurrentChapter):
            case nameof(PlayerEngine.Chapters):
                UpdateInfo();
                break;
            case nameof(PlayerEngine.PlaylistCount):
            case nameof(PlayerEngine.PlaylistPos):
            case nameof(PlayerEngine.Loop):
                NextButton.Opacity = _player.PlaylistPos < _player.PlaylistCount - 1 || _player.Loop == LoopMode.Playlist ? 1 : 0.45;
                break;
            case nameof(PlayerEngine.SubtitleTracks):
                SubButton.Opacity = _player.SubtitleTracks.Count > 0 ? 1 : 0.55;
                break;
            case nameof(PlayerEngine.AudioTracks):
                AudioButton.Visibility = _player.AudioTracks.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
                break;
            case nameof(PlayerEngine.CurrentPath):
                PreviewPopup.IsOpen = false;
                break;
        }
    }

    private void UpdateTimeText(double pos)
    {
        PosRun.Text = Formatters.Time(pos, forceHours: _player.Duration >= 3600);
    }

    private void UpdateInfo()
    {
        var chapters = _player.Chapters;
        int c = _player.CurrentChapter;
        InfoText.Text = c >= 0 && c < chapters.Count ? "· " + chapters[c].DisplayName : "";
    }

    private void UpdateVolume()
    {
        _updatingVolume = true;
        VolumeSlider.Maximum = _player.VolumeMax;
        VolumeSlider.Value = _player.IsMuted ? 0 : _player.Volume;
        _updatingVolume = false;
        double v = _player.Volume;
        string key = _player.IsMuted || v <= 0 ? "Icon.Mute" : v < 34 ? "Icon.Volume0" : v < 67 ? "Icon.Volume1" : v <= 100 ? "Icon.Volume2" : "Icon.Volume";
        VolumeButton.Content = FindResource(key);
        VolumeText.Text = _player.IsMuted ? "음소거" : $"{v:0}%";
        VolumeText.Foreground = v > 100 && !_player.IsMuted ? (Brush)FindResource("Brush.Warning") : new SolidColorBrush(Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF));
    }

    // ---- seek hover: time and chapter under the pointer -----------------------------

    private void OnSeekHover(object? sender, SeekHoverEventArgs e)
    {
        if (!e.IsOver || _player.Duration <= 0)
        {
            PreviewPopup.IsOpen = false;
            return;
        }
        PreviewTime.Text = Formatters.Time(e.Time, forceHours: _player.Duration >= 3600);
        var chapters = _player.Chapters;
        var ch = chapters.LastOrDefault(x => x.Time <= e.Time + 0.01);
        PreviewChapter.Text = ch?.DisplayName ?? "";
        PreviewChapter.Visibility = ch != null && chapters.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        PositionPreview(e.X);
        PreviewPopup.IsOpen = true;
    }

    private void PositionPreview(double x)
    {
        var child = (FrameworkElement)PreviewPopup.Child;
        child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = child.DesiredSize;
        double left = Math.Clamp(x - size.Width / 2, 0, Math.Max(0, Seek.ActualWidth - size.Width));
        PreviewPopup.HorizontalOffset = left;
        PreviewPopup.VerticalOffset = -size.Height - 6;
    }

    // ---- buttons -------------------------------------------------------------------

    private void PlayPause_Click(object sender, RoutedEventArgs e) => _player.PlayPause();
    private void Prev_Click(object sender, RoutedEventArgs e) => _player.Previous();
    private void Next_Click(object sender, RoutedEventArgs e) => _player.Next();
    private void Mute_Click(object sender, RoutedEventArgs e) => _player.ToggleMute();
    private void Playlist_Click(object sender, RoutedEventArgs e) => _shell.TogglePlaylistPanel();
    private void Fullscreen_Click(object sender, RoutedEventArgs e) => _shell.ToggleFullscreen();

    private void Speed_Click(object sender, RoutedEventArgs e) => _shell.ShowContextMenu(SpeedButton, "speed");
    private void Subtitles_Click(object sender, RoutedEventArgs e) => _shell.ShowContextMenu(SubButton, "subtitles");
    private void Audio_Click(object sender, RoutedEventArgs e) => _shell.ShowContextMenu(AudioButton, "audio");

    private void Volume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingVolume || !IsLoaded) return;
        _player.SetVolume(e.NewValue, osd: false);
        if (_player.IsMuted && e.NewValue > 0) _player.SetMute(false, osd: false);
        VolumeText.Text = $"{e.NewValue:0}%";
    }
}

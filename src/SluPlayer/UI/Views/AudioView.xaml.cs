using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using SluPlayer.Core.Player;

namespace SluPlayer.UI.Views;

/// <summary>
/// Player view for media without a picture track: the embedded cover (if any), title and artist.
/// <see cref="CoverSlot"/> is the square the main window moves the native video window over when the file has
/// embedded art (mpv draws the art there).
/// </summary>
public partial class AudioView : UserControl
{
    private const double MaxCover = 360, MinCover = 120;

    private readonly PlayerEngine _player;

    /// <summary>Square element the native video window is placed over when the media has cover art.</summary>
    public FrameworkElement CoverSlot => CoverSlotElement;

    public AudioView(IShell shell)
    {
        _player = shell.Player;
        InitializeComponent();
        _player.PropertyChanged += OnPlayerChanged;
        SafeArea.SizeChanged += (_, _) => LayoutCover();
        UpdateTexts();
        UpdateArt();
    }

    private void OnPlayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PlayerEngine.MediaTitle):
            case nameof(PlayerEngine.Metadata):
            case nameof(PlayerEngine.CurrentPath):
                UpdateTexts();
                break;
            case nameof(PlayerEngine.HasAlbumArt):
                UpdateArt();
                break;
        }
    }

    private void UpdateTexts()
    {
        var path = _player.CurrentPath;
        var title = _player.MediaTitle;
        if (string.IsNullOrWhiteSpace(title) && path != null) title = SafeName(path);
        TitleText.Text = title ?? "";
        var artist = _player.Artist ?? (path != null && MediaFormats.IsUrl(path) ? HostOf(path) : null);
        ArtistText.Text = artist ?? "";
        ArtistText.Visibility = string.IsNullOrEmpty(artist) ? Visibility.Collapsed : Visibility.Visible;
        LayoutCover();
    }

    private void UpdateArt()
    {
        bool art = _player.HasAlbumArt;
        MusicGlyph.Visibility = art ? Visibility.Collapsed : Visibility.Visible;
        LayoutCover();
    }

    /// <summary>Largest square cover that leaves room for the text (hidden when the window is too small).</summary>
    private void LayoutCover()
    {
        double w = SafeArea.ActualWidth, h = SafeArea.ActualHeight;
        double size = Math.Floor(Math.Min(Math.Min(w * 0.8, h - 90), MaxCover));
        bool show = _player.HasAlbumArt && size >= MinCover;
        CoverSlotElement.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show && Math.Abs(CoverSlotElement.Width - size) > 0.5)
        {
            CoverSlotElement.Width = size;
            CoverSlotElement.Height = size;
        }
        double textWidth = Math.Max(120, Math.Min(w, 720));
        TitleText.MaxWidth = textWidth;
        ArtistText.MaxWidth = textWidth;
    }

    private static string SafeName(string path)
    {
        if (MediaFormats.IsUrl(path)) return path;
        try { return Path.GetFileNameWithoutExtension(path); } catch { return path; }
    }

    private static string HostOf(string url)
    {
        try { return new Uri(url).Host.Replace("www.", "", StringComparison.OrdinalIgnoreCase); }
        catch { return ""; }
    }
}

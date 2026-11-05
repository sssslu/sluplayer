using System.Windows;
using SluPlayer.Core.Player;
using SluPlayer.Services.Settings;

namespace SluPlayer.UI;

public enum ToastKind { Info, Success, Warning, Error }

/// <summary>
/// Services the main window offers to pages/view-models (playback entry points, the download and settings windows,
/// notifications). Everything here must be called on the UI thread.
/// </summary>
public interface IShell
{
    /// <summary>The main (player) window.</summary>
    Window Window { get; }
    PlayerEngine Player { get; }
    AppSettings Settings { get; }

    /// <summary>Opens files/folders/URLs in the player.</summary>
    void Play(IReadOnlyList<string> items, bool append = false);

    void Play(string item, bool append = false) => Play([item], append);

    /// <summary>Non-blocking notification (in the active SluPlayer window; mpv's OSD over visible video).</summary>
    void ShowToast(string message, ToastKind kind = ToastKind.Info);

    /// <summary>Prompts for local media files and plays them.</summary>
    void OpenFilesDialog(bool append = false);

    void OpenFolderDialog();

    void OpenUrlDialog();

    /// <summary>Persists settings (debounced).</summary>
    void SaveSettings();

    /// <summary>Opens the download window (one instance).</summary>
    void ShowDownloads();

    /// <summary>Opens the settings window (one instance).</summary>
    void ShowSettings();

    /// <summary>Opens the download window with this link analysed (quality/format choice).</summary>
    void DownloadUrl(string url);
}

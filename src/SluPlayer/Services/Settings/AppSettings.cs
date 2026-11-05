namespace SluPlayer.Services.Settings;

public enum OnTopMode { Never, Always, WhilePlaying }

/// <summary>
/// Persisted user settings (settings.json): what the settings window and the download window offer, plus state the
/// app remembers itself. Fixed behaviour lives in the code that uses it (e.g. MpvOptionBuilder), not here.
/// </summary>
public sealed class AppSettings
{
    /// <summary>Upper end of the volume range (mpv volume-max).</summary>
    public const double MaxVolume = 150;

    // ---- settings window -------------------------------------------------
    public OnTopMode OnTop { get; set; } = OnTopMode.Never;
    public bool ResumePlayback { get; set; } = true;
    public bool AutoFitWindow { get; set; } = true;
    /// <summary>mpv hwdec value: auto-safe (recommended), d3d11va, nvdec, d3d11va-copy, no.</summary>
    public string Hwdec { get; set; } = "auto-safe";
    public int SubtitleFontSize { get; set; } = 46;
    public string ScreenshotDir { get; set; } = AppPaths.DefaultScreenshotDir;
    public string DownloadDir { get; set; } = AppPaths.DefaultDownloadDir;
    public string AudioDownloadDir { get; set; } = AppPaths.DefaultMusicDownloadDir;
    /// <summary>best | 2160 | 1440 | 1080 | 720 | 480 | 360</summary>
    public string DownloadQuality { get; set; } = "best";

    // ---- download window (last choices) ------------------------------------
    /// <summary>video | audio</summary>
    public string DownloadKind { get; set; } = "video";
    /// <summary>mp4 | mkv</summary>
    public string DownloadVideoFormat { get; set; } = "mp4";
    /// <summary>mp3 | m4a</summary>
    public string DownloadAudioFormat { get; set; } = "mp3";

    // ---- remembered state ----------------------------------------------------
    public WindowBounds? Window { get; set; }
    public double Volume { get; set; } = 100;
    public bool Muted { get; set; }
    /// <summary>none | file | playlist</summary>
    public string LoopMode { get; set; } = "none";
    public bool Shuffle { get; set; }
    public bool PlaylistPanelOpen { get; set; }
    public DateTime LastYtDlpUpdateCheck { get; set; }
}

public sealed class WindowBounds
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Maximized { get; set; }
}

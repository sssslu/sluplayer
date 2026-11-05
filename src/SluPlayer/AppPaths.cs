using System.IO;

namespace SluPlayer;

/// <summary>Every file-system location the app uses. Portable mode: put "portable.txt" next to the exe.</summary>
public static class AppPaths
{
    public static string AppDir { get; } = AppContext.BaseDirectory;

    public static bool IsPortable { get; } = File.Exists(Path.Combine(AppDir, "portable.txt"));

    public static string DataDir { get; } = IsPortable
        ? Path.Combine(AppDir, "data")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SluPlayer");

    public static string LocalDir { get; } = IsPortable
        ? Path.Combine(AppDir, "data", "local")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SluPlayer");

    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string HistoryFile => Path.Combine(DataDir, "history.json");
    public static string DownloadsFile => Path.Combine(DataDir, "downloads.json");

    public static string ShaderCacheDir => Path.Combine(LocalDir, "shader-cache");
    public static string LogDir => Path.Combine(LocalDir, "logs");

    /// <summary>Tools shipped with the app (read-only location, e.g. Program Files).</summary>
    public static string BundledToolsDir => Path.Combine(AppDir, "tools");

    /// <summary>Writable tools folder used for self-updated copies (e.g. a newer yt-dlp.exe).</summary>
    public static string UpdatedToolsDir => Path.Combine(LocalDir, "tools");

    /// <summary>yt-dlp's own cache (player JS etc.), passed as --cache-dir.</summary>
    public static string YtDlpCacheDir => Path.Combine(LocalDir, "yt-dlp-cache");

    /// <summary>Netscape cookies.txt exported from the in-app YouTube sign-in (passed to yt-dlp as --cookies).</summary>
    public static string YouTubeCookiesFile => Path.Combine(LocalDir, "cookies", "youtube-cookies.txt");

    /// <summary>WebView2 user-data folder for the in-app YouTube sign-in window.</summary>
    public static string WebView2DataDir => Path.Combine(LocalDir, "webview2");

    public static string DefaultDownloadDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "SluPlayer");

    public static string DefaultMusicDownloadDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "SluPlayer");

    public static string DefaultScreenshotDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "SluPlayer");

    public static void EnsureCreated()
    {
        foreach (var dir in new[] { DataDir, LocalDir, ShaderCacheDir, LogDir })
        {
            try { Directory.CreateDirectory(dir); } catch { /* reported later on first use */ }
        }
    }
}

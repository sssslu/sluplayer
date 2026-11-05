using System.IO;

namespace SluPlayer.Services;

/// <summary>
/// The cookies yt-dlp gets (shared by the downloader and mpv's ytdl_hook for streaming): the file saved by the
/// in-app YouTube sign-in, if there is one (sign-out deletes it). YouTube may demand a signed-in session ("Sign in to
/// confirm you're not a bot") depending on the network.
/// </summary>
public static class YtDlpCookies
{
    public static bool HasYouTubeLogin => File.Exists(AppPaths.YouTubeCookiesFile);

    /// <summary>
    /// Key/value for mpv's ytdl-raw-options (null when signed out). yt-dlp rewrites its --cookies file when it exits,
    /// so mpv never gets the cookie file itself: each call hands out a new private copy and deletes the older ones.
    /// A yt-dlp run still going then only rewrites a copy nothing uses any more (sign-out or a new login stays).
    /// </summary>
    public static (string Key, string Value)? RawOption()
    {
        DeleteStreamCopies();
        _streamStamp = Stamp();
        if (!HasYouTubeLogin) return null;
        try
        {
            Directory.CreateDirectory(StreamCopyDir);
            var copy = Path.Combine(StreamCopyDir, Guid.NewGuid().ToString("N") + ".txt");
            // A new file: writable even when the original is read-only (yt-dlp fails at exit otherwise).
            File.WriteAllBytes(copy, File.ReadAllBytes(AppPaths.YouTubeCookiesFile));
            return ("cookies", copy);
        }
        catch (Exception ex)
        {
            Log.Warn("cookie copy for streaming failed: " + ex.Message);
            _streamStamp = null; // try again at the next online open
            return null;         // never the original
        }
    }

    /// <summary>
    /// False when the cookies changed since the last <see cref="RawOption"/> (sign-in, sign-out), so mpv's copy is
    /// out of date.
    /// </summary>
    public static bool IsStreamCopyCurrent() => _streamStamp == Stamp();

    /// <summary>Deletes the copies made for mpv (one a running yt-dlp still holds goes on a later call).</summary>
    public static void DeleteStreamCopies()
    {
        try
        {
            if (!Directory.Exists(StreamCopyDir)) return;
            foreach (var f in Directory.EnumerateFiles(StreamCopyDir))
            {
                try { File.Delete(f); } catch { }
            }
        }
        catch { }
    }

    /// <summary>mpv's copies; not CookieLease's run folder, which it cleans by age.</summary>
    private static string StreamCopyDir => Path.Combine(Path.GetDirectoryName(AppPaths.YouTubeCookiesFile)!, "stream");

    private static string? _streamStamp;

    private static string Stamp()
    {
        try
        {
            var fi = new FileInfo(AppPaths.YouTubeCookiesFile);
            return fi.Exists ? $"{fi.Length}|{fi.LastWriteTimeUtc.Ticks}" : "none";
        }
        catch
        {
            return "unknown";
        }
    }
}

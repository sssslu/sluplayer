using System.IO;
using SluPlayer.Services.Downloads;

namespace SluPlayer.Services;

/// <summary>
/// The cookies yt-dlp gets (shared by the downloader and mpv's ytdl_hook for streaming): the files saved by the
/// in-app sign-ins (<see cref="LoginSite"/>), merged into one private copy per use (sign-out deletes a site's file).
/// yt-dlp only sends each cookie to its own site. YouTube may demand a signed-in session ("Sign in to confirm you're
/// not a bot") depending on the network; X shows sensitive and protected posts only to accounts.
/// </summary>
public static class YtDlpCookies
{
    public static bool HasAny => LoginSite.All.Any(s => s.IsLoggedIn);

    /// <summary>
    /// Writes every saved sign-in into a new cookies file at <paramref name="path"/> (never one of the saved files:
    /// yt-dlp rewrites its --cookies file when it exits). False, and nothing written, when there is none.
    /// </summary>
    public static bool WriteMerged(string path)
    {
        var cookies = new List<CookieRecord>();
        foreach (var site in LoginSite.All)
        {
            string text;
            try { text = File.ReadAllText(site.CookiesFile); }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { continue; } // signed out
            cookies.AddRange(NetscapeCookies.Parse(text).Where(site.IsSiteCookie));
        }
        if (cookies.Count == 0) return false;
        File.WriteAllText(path, NetscapeCookies.Serialize(cookies));
        return true;
    }

    /// <summary>
    /// Key/value for mpv's ytdl-raw-options (null when signed out). yt-dlp rewrites its --cookies file when it exits,
    /// so mpv never gets a saved file itself: each call hands out a new private copy and deletes the older ones.
    /// A yt-dlp run still going then only rewrites a copy nothing uses any more (sign-out or a new login stays).
    /// </summary>
    public static (string Key, string Value)? RawOption()
    {
        DeleteStreamCopies();
        _streamStamp = Stamp();
        if (!HasAny) return null;
        try
        {
            Directory.CreateDirectory(StreamCopyDir);
            var copy = Path.Combine(StreamCopyDir, Guid.NewGuid().ToString("N") + ".txt");
            return WriteMerged(copy) ? ("cookies", copy) : null;
        }
        catch (Exception ex)
        {
            Log.Warn("cookie copy for streaming failed: " + ex.Message);
            _streamStamp = null; // try again at the next online open
            return null;         // never a saved file
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
    private static string StreamCopyDir => Path.Combine(AppPaths.CookiesDir, "stream");

    private static string? _streamStamp;

    private static string Stamp() => string.Join(";", LoginSite.All.Select(s => Stamp(s.CookiesFile)));

    private static string Stamp(string file)
    {
        try
        {
            var fi = new FileInfo(file);
            return fi.Exists ? $"{fi.Length}|{fi.LastWriteTimeUtc.Ticks}" : "none";
        }
        catch
        {
            return "unknown";
        }
    }
}

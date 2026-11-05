using System.IO;
using Microsoft.Web.WebView2.Core;

namespace SluPlayer.Services.Downloads;

/// <summary>Turns WebView2 cookies into Netscape cookies.txt records (in-app YouTube sign-in).</summary>
public static class WebViewCookieExport
{
    /// <summary>
    /// URIs whose cookies make up a YouTube session: the hosts yt-dlp talks to. The sign-in mirrors the account cookies
    /// (SID, SAPISID, __Secure-*PSID…) onto .youtube.com, and yt-dlp only sends cookies of the host it requests, so the
    /// accounts.google.com / .google.com cookies are never needed (and would open the whole Google account if leaked).
    /// </summary>
    public static readonly string[] YouTubeUris =
    [
        "https://www.youtube.com/",
        "https://youtube.com/",
        "https://m.youtube.com/",
        "https://music.youtube.com/",
    ];

    /// <summary>Cookie names that only exist for a signed-in YouTube session.</summary>
    private static readonly string[] AuthCookies = ["SAPISID", "__Secure-3PAPISID", "__Secure-1PAPISID"];
    private static readonly string[] SessionCookies = ["__Secure-3PSID", "__Secure-1PSID", "SID", "LOGIN_INFO"];

    public static async Task<List<CookieRecord>> CollectAsync(CoreWebView2CookieManager manager, IEnumerable<string> uris)
    {
        var map = new Dictionary<(string, string, string), CookieRecord>();
        foreach (var uri in uris)
        {
            List<CoreWebView2Cookie> cookies;
            try { cookies = await manager.GetCookiesAsync(uri); }
            catch (Exception ex)
            {
                Log.Warn($"GetCookiesAsync({uri}) failed: {ex.Message}");
                continue;
            }
            foreach (var c in cookies)
            {
                var rec = ToRecord(c);
                map[(rec.Domain.ToLowerInvariant(), rec.Path, rec.Name)] = rec;
            }
        }
        return map.Values.OrderBy(c => c.Domain, StringComparer.Ordinal).ThenBy(c => c.Name, StringComparer.Ordinal).ToList();
    }

    public static CookieRecord ToRecord(CoreWebView2Cookie c)
    {
        long expires = 0;
        if (!c.IsSession && c.Expires > DateTime.MinValue.AddDays(1))
        {
            // The wrapper converts "seconds since the Unix epoch" to a DateTime; normalise its Kind before converting back.
            var utc = c.Expires.Kind == DateTimeKind.Local ? c.Expires.ToUniversalTime()
                : DateTime.SpecifyKind(c.Expires, DateTimeKind.Utc);
            expires = Math.Max(0, new DateTimeOffset(utc).ToUnixTimeSeconds());
        }
        return new CookieRecord(c.Domain, string.IsNullOrEmpty(c.Path) ? "/" : c.Path, c.Name, c.Value ?? "",
            c.IsSecure, c.IsHttpOnly, expires);
    }

    /// <summary>True when the cookies contain a signed-in YouTube/Google session.</summary>
    public static bool HasYouTubeSession(IEnumerable<CookieRecord> cookies)
    {
        var yt = cookies.Where(IsYouTubeCookie).ToList();
        return yt.Any(c => AuthCookies.Contains(c.Name)) && yt.Any(c => SessionCookies.Contains(c.Name));
    }

    /// <summary>A cookie of youtube.com or one of its subdomains (the only ones written to the cookies file).</summary>
    public static bool IsYouTubeCookie(CookieRecord c)
    {
        var domain = c.Domain.Trim().TrimStart('.');
        return domain.Equals("youtube.com", StringComparison.OrdinalIgnoreCase)
               || domain.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Earlier builds also saved the accounts.google.com / .google.com cookies: drops everything but the youtube.com
    /// cookies from the saved file, so existing sign-ins keep working without them. Off the UI thread.
    /// </summary>
    public static void TrimSavedFile()
    {
        try
        {
            var path = AppPaths.YouTubeCookiesFile;
            if (!File.Exists(path)) return;
            var stamp = File.GetLastWriteTimeUtc(path);
            var cookies = NetscapeCookies.Parse(File.ReadAllText(path));
            var youtube = cookies.Where(IsYouTubeCookie).ToList();
            if (youtube.Count == cookies.Count) return;
            // A sign-in or sign-out in the meantime wins.
            if (File.GetLastWriteTimeUtc(path) != stamp) return;
            NetscapeCookies.WriteFile(path, youtube);
            Log.Info($"removed {cookies.Count - youtube.Count} non-YouTube cookies from {path}");
        }
        catch (Exception ex)
        {
            Log.Warn("could not trim the YouTube cookies file: " + ex.Message);
        }
    }
}

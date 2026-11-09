using System.IO;
using Microsoft.Web.WebView2.Core;

namespace SluPlayer.Services.Downloads;

/// <summary>Turns WebView2 cookies into Netscape cookies.txt records (in-app sign-ins, see <see cref="LoginSite"/>).</summary>
public static class WebViewCookieExport
{
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
            var youtube = cookies.Where(LoginSite.YouTube.IsSiteCookie).ToList();
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

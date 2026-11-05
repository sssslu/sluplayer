using System.Globalization;
using System.IO;
using System.Text;

namespace SluPlayer.Services.Downloads;

/// <summary>One cookie as stored in a Netscape cookies.txt. <c>Expires</c> is Unix seconds (0 = session cookie).</summary>
public sealed record CookieRecord(
    string Domain,
    string Path,
    string Name,
    string Value,
    bool Secure,
    bool HttpOnly,
    long Expires);

/// <summary>
/// Reads/writes the Netscape cookie file format used by yt-dlp's --cookies:
/// <c>domain \t includeSubdomains \t path \t secure \t expires \t name \t value</c>.
/// yt-dlp (Python's MozillaCookieJar) asserts includeSubdomains == domain.StartsWith("."), treats expires 0 as a
/// session cookie (kept, since it loads with ignore_discard) and understands the "#HttpOnly_" prefix.
/// </summary>
public static class NetscapeCookies
{
    public const string Header = "# Netscape HTTP Cookie File";

    public static string Serialize(IEnumerable<CookieRecord> cookies)
    {
        var sb = new StringBuilder();
        sb.Append(Header).Append('\n');
        sb.Append("# Exported by SluPlayer (YouTube sign-in). Keep this file private: it signs in to your account.\n\n");
        foreach (var c in cookies)
        {
            if (!IsValid(c)) continue;
            var domain = c.Domain.Trim();
            if (c.HttpOnly) sb.Append("#HttpOnly_");
            sb.Append(domain).Append('\t')
              .Append(domain.StartsWith('.') ? "TRUE" : "FALSE").Append('\t')
              .Append(string.IsNullOrEmpty(c.Path) ? "/" : c.Path).Append('\t')
              .Append(c.Secure ? "TRUE" : "FALSE").Append('\t')
              .Append(Math.Max(0, c.Expires).ToString(CultureInfo.InvariantCulture)).Append('\t')
              .Append(c.Name).Append('\t')
              .Append(c.Value).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>Tabs/newlines would break the line format; such cookies are skipped.</summary>
    public static bool IsValid(CookieRecord c)
        => !string.IsNullOrWhiteSpace(c.Domain) && !string.IsNullOrEmpty(c.Name)
           && c.Domain.IndexOfAny(['\t', '\n', '\r', ' ']) < 0
           && c.Name.IndexOfAny(['\t', '\n', '\r']) < 0
           && c.Value.IndexOfAny(['\t', '\n', '\r']) < 0
           && (c.Path ?? "").IndexOfAny(['\t', '\n', '\r']) < 0;

    public static List<CookieRecord> Parse(string text)
    {
        var list = new List<CookieRecord>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            bool httpOnly = false;
            if (line.StartsWith("#HttpOnly_", StringComparison.Ordinal))
            {
                httpOnly = true;
                line = line["#HttpOnly_".Length..];
            }
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var f = line.Split('\t');
            if (f.Length != 7) continue;
            long.TryParse(f[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var exp);
            list.Add(new CookieRecord(f[0], f[2], f[5], f[6], f[3] == "TRUE", httpOnly, exp));
        }
        return list;
    }

    /// <summary>Writes the file atomically (temp + replace) and creates its folder.</summary>
    public static void WriteFile(string path, IEnumerable<CookieRecord> cookies)
        => Settings.SettingsService.WriteAtomic(path, Serialize(cookies));
}

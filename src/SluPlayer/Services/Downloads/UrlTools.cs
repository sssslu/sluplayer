using System.Text.RegularExpressions;

namespace SluPlayer.Services.Downloads;

/// <summary>URL helpers for the downloader: http(s) detection, YouTube ids, duplicate keys.</summary>
public static partial class UrlTools
{
    [GeneratedRegex(@"https?://[^\s<>""']+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlInTextRegex();

    [GeneratedRegex(@"^[A-Za-z0-9_-]{11}$")]
    private static partial Regex YouTubeIdRegex();

    public static bool IsHttpUrl(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        return Uri.TryCreate(s.Trim(), UriKind.Absolute, out var u)
               && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps)
               && !string.IsNullOrEmpty(u.Host);
    }

    /// <summary>
    /// The first http(s) URL in free text (share texts like "제목 https://youtu.be/xyz"), or null.
    /// Trailing punctuation that is almost never part of a link is trimmed.
    /// </summary>
    public static string? ExtractUrl(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim();
        if (IsHttpUrl(t) && !t.Any(char.IsWhiteSpace)) return t;
        var m = UrlInTextRegex().Match(t);
        if (!m.Success) return null;
        var url = m.Value.TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '}', '>', '」', '』', '。', '、');
        return IsHttpUrl(url) ? url : null;
    }

    public static bool IsYouTube(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var u) && IsYouTubeHost(u.Host);

    public static bool IsYouTubeHost(string host)
    {
        host = host.ToLowerInvariant();
        return host is "youtube.com" or "youtu.be" or "youtube-nocookie.com"
               || host.EndsWith(".youtube.com", StringComparison.Ordinal)
               || host.EndsWith(".youtube-nocookie.com", StringComparison.Ordinal);
    }

    /// <summary>An Instagram or X direct-message link: a chat, not a post (yt-dlp only reaches the login page).</summary>
    public static bool IsDirectMessage(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        var host = u.Host.ToLowerInvariant();
        var path = u.AbsolutePath.ToLowerInvariant();
        if (host is "instagram.com" || host.EndsWith(".instagram.com", StringComparison.Ordinal))
            return path.StartsWith("/direct/", StringComparison.Ordinal);
        if (host is "x.com" or "twitter.com" || host.EndsWith(".x.com", StringComparison.Ordinal)
            || host.EndsWith(".twitter.com", StringComparison.Ordinal))
            return path.StartsWith("/messages", StringComparison.Ordinal) || path.StartsWith("/i/chat", StringComparison.Ordinal);
        return false;
    }

    /// <summary>11-character video id from watch/shorts/live/embed/youtu.be links, or null.</summary>
    public static string? YouTubeVideoId(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || !IsYouTubeHost(u.Host)) return null;
        string? id = null;
        var segments = u.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (u.Host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase))
            id = segments.FirstOrDefault();
        else if (segments.Length >= 2 && segments[0] is "shorts" or "live" or "embed" or "v" or "e")
            id = segments[1];
        else
            id = QueryValue(u, "v");
        return id != null && YouTubeIdRegex().IsMatch(id) ? id : null;
    }

    public static string? YouTubeListId(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || !IsYouTubeHost(u.Host)) return null;
        var list = QueryValue(u, "list");
        return string.IsNullOrWhiteSpace(list) ? null : list;
    }

    /// <summary>A "watch?v=…&amp;list=…" link: the user decides between the single video and the whole playlist.</summary>
    public static bool IsYouTubeVideoInList(string? url) => YouTubeVideoId(url) != null && YouTubeListId(url) != null;

    public static string YouTubeWatchUrl(string id) => "https://www.youtube.com/watch?v=" + id;

    public static string YouTubePlaylistUrl(string listId) => "https://www.youtube.com/playlist?list=" + Uri.EscapeDataString(listId);

    /// <summary>16:9 thumbnail that exists for every YouTube video (maxres 404s for older uploads).</summary>
    public static string YouTubeThumbnail(string id) => $"https://i.ytimg.com/vi/{id}/mqdefault.jpg";

    /// <summary>
    /// Identity used for duplicate detection: "youtube:&lt;id&gt;" for YouTube links, "&lt;extractor&gt;:&lt;id&gt;" when known,
    /// otherwise the URL without fragment / trailing slash.
    /// </summary>
    public static string Key(string url, string? extractorKey = null, string? id = null)
    {
        var yt = YouTubeVideoId(url);
        if (yt != null) return "youtube:" + yt;
        if (!string.IsNullOrWhiteSpace(extractorKey) && !string.IsNullOrWhiteSpace(id)
            && !extractorKey.Equals("Generic", StringComparison.OrdinalIgnoreCase))
            return extractorKey.ToLowerInvariant() + ":" + id;
        if (Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u))
        {
            var s = u.GetLeftPart(UriPartial.Query);
            return s.TrimEnd('/').ToLowerInvariant();
        }
        return url.Trim().ToLowerInvariant();
    }

    private static string? QueryValue(Uri u, string name)
    {
        var q = u.Query;
        if (q.Length <= 1) return null;
        foreach (var part in q[1..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv[0].Equals(name, StringComparison.Ordinal))
                return kv.Length == 2 ? Uri.UnescapeDataString(kv[1].Replace('+', ' ')) : "";
        }
        return null;
    }
}

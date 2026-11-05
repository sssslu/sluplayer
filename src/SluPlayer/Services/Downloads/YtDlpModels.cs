using System.Text.Json;

namespace SluPlayer.Services.Downloads;

/// <summary>One entry of yt-dlp's "formats" list.</summary>
public sealed class MediaFormat
{
    public string Id { get; init; } = "";
    public string Ext { get; init; } = "";
    public string? VCodec { get; init; }
    public string? ACodec { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public double? Fps { get; init; }
    public string? DynamicRange { get; init; }
    public string? Note { get; init; }
    public bool HasDrm { get; init; }

    // Unknown codecs (null) usually mean a progressive/muxed file: assume audio, and video unless it is an audio container.
    public bool HasVideo => VCodec is { } v ? v != "none" : (Width ?? Height ?? 0) > 0 || !IsAudioExt(Ext);

    public bool HasAudio => ACodec is not { } a || a != "none";

    private static bool IsAudioExt(string ext)
        => ext is "m4a" or "mp3" or "opus" or "ogg" or "oga" or "aac" or "flac" or "wav" or "weba" or "mka";

    /// <summary>Storyboards/images (mhtml) and DRM streams are useless for downloading.</summary>
    public bool IsUsable => !HasDrm && (HasVideo || HasAudio) && Ext is not ("mhtml" or "jpg" or "png" or "webp");

    /// <summary>The value yt-dlp's "res" sort field compares: the smaller side (so vertical videos work).</summary>
    public int ShortSide => Width is int w && Height is int h ? Math.Min(w, h) : Height ?? Width ?? 0;
    public int LongSide => Width is int w && Height is int h ? Math.Max(w, h) : Width ?? Height ?? 0;

    public bool IsHdr => DynamicRange is { Length: > 0 } d && !d.Equals("SDR", StringComparison.OrdinalIgnoreCase);
}

public sealed class MediaInfo
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string? Uploader { get; init; }
    public double? Duration { get; init; }
    public string? Thumbnail { get; init; }
    public string WebpageUrl { get; init; } = "";
    public string? ExtractorKey { get; init; }
    public IReadOnlyList<MediaFormat> Formats { get; init; } = [];
}

public sealed class PlaylistEntryInfo
{
    public string Id { get; init; } = "";
    public string Url { get; init; } = "";
    public string Title { get; init; } = "";
    public double? Duration { get; init; }
    public string? Channel { get; init; }
    public string? Thumbnail { get; init; }
    public string? IeKey { get; init; }

    /// <summary>A tab/sub-playlist (channel home pages list their tabs), not a single video.</summary>
    public bool IsPlaylist { get; init; }

    /// <summary>
    /// 1-based position for media without a page of its own (the videos of one Instagram or X post): <see cref="Url"/>
    /// is then the post, and the download picks this item.
    /// </summary>
    public int? Item { get; init; }
}

public sealed class PlaylistInfo
{
    public string Title { get; init; } = "";
    public string? Uploader { get; init; }
    public IReadOnlyList<PlaylistEntryInfo> Entries { get; init; } = [];
}

public sealed class AnalysisResult
{
    public MediaInfo? Video { get; init; }
    public PlaylistInfo? Playlist { get; init; }
}

/// <summary>Parses yt-dlp -J output (single video or flat playlist).</summary>
public static class YtDlpJson
{
    public static AnalysisResult ParseAnalysis(string json, string requestedUrl)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 256 });
        var root = doc.RootElement;
        var type = Str(root, "_type");
        if (type is "playlist" or "multi_video")
            return new AnalysisResult { Playlist = ParsePlaylist(root, requestedUrl) };
        return new AnalysisResult { Video = ParseVideo(root, requestedUrl) };
    }

    private static MediaInfo ParseVideo(JsonElement e, string requestedUrl)
    {
        var id = Str(e, "id") ?? "";
        var extractor = Str(e, "extractor_key");
        var formats = new List<MediaFormat>();
        if (e.TryGetProperty("formats", out var fl) && fl.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in fl.EnumerateArray())
            {
                formats.Add(new MediaFormat
                {
                    Id = Str(f, "format_id") ?? "",
                    Ext = Str(f, "ext") ?? "",
                    VCodec = Str(f, "vcodec"),
                    ACodec = Str(f, "acodec"),
                    Width = Int(f, "width"),
                    Height = Int(f, "height"),
                    Fps = Num(f, "fps"),
                    DynamicRange = Str(f, "dynamic_range"),
                    Note = Str(f, "format_note"),
                    HasDrm = f.TryGetProperty("has_drm", out var drm) && drm.ValueKind == JsonValueKind.True,
                });
            }
        }
        else if (Str(e, "format_id") is { } single)
        {
            // Some extractors return one direct format without a list.
            formats.Add(new MediaFormat
            {
                Id = single, Ext = Str(e, "ext") ?? "", VCodec = Str(e, "vcodec"), ACodec = Str(e, "acodec"),
                Width = Int(e, "width"), Height = Int(e, "height"), Fps = Num(e, "fps"),
            });
        }

        var thumb = string.Equals(extractor, "Youtube", StringComparison.OrdinalIgnoreCase) && id.Length == 11
            ? UrlTools.YouTubeThumbnail(id)
            : BestThumbnail(e, preferWidth: 480);

        return new MediaInfo
        {
            Id = id,
            Title = Str(e, "title") ?? Str(e, "fulltitle") ?? id,
            Uploader = FirstLine(Str(e, "channel") ?? Str(e, "uploader") ?? Str(e, "creator")),
            Duration = Num(e, "duration"),
            Thumbnail = thumb,
            WebpageUrl = Str(e, "webpage_url") ?? Str(e, "original_url") ?? requestedUrl,
            ExtractorKey = extractor,
            Formats = formats,
        };
    }

    private static PlaylistInfo ParsePlaylist(JsonElement e, string requestedUrl)
    {
        var entries = new List<PlaylistEntryInfo>();
        var pageUrl = Str(e, "webpage_url") ?? Str(e, "original_url") ?? requestedUrl;
        if (e.TryGetProperty("entries", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            int position = 0;
            foreach (var x in list.EnumerateArray())
            {
                position++;
                if (x.ValueKind != JsonValueKind.Object) continue;
                var id = Str(x, "id") ?? "";
                var ie = Str(x, "ie_key") ?? Str(x, "extractor_key");
                // Resolved in place (with formats) instead of a link: the post's own videos. Their "url" is a short-lived
                // CDN link or missing, and the post URL alone downloads all of them.
                bool inline = Str(x, "_type") is null or "video"
                              && (x.TryGetProperty("formats", out _) || Str(x, "format_id") != null);
                var url = inline ? pageUrl : Str(x, "url") ?? Str(x, "webpage_url") ?? Str(x, "original_url") ?? "";
                if (inline) ie ??= Str(e, "extractor_key");
                bool ytVideo = ie is "Youtube" && id.Length == 11;
                if (url.Length == 0 && ytVideo) url = UrlTools.YouTubeWatchUrl(id);
                if (url.Length == 0) continue;
                bool nested = !inline && (Str(x, "_type") is "playlist"
                              || ie is "YoutubeTab" or "YoutubePlaylist"
                              || (UrlTools.IsYouTube(url) && UrlTools.YouTubeVideoId(url) == null));
                entries.Add(new PlaylistEntryInfo
                {
                    Id = id,
                    Url = url,
                    Title = Str(x, "title") ?? (nested ? url : id),
                    Duration = Num(x, "duration"),
                    Channel = FirstLine(Str(x, "channel") ?? Str(x, "uploader")),
                    Thumbnail = ytVideo ? UrlTools.YouTubeThumbnail(id) : BestThumbnail(x, preferWidth: 320),
                    IeKey = ie,
                    IsPlaylist = nested,
                    Item = inline ? position : null,
                });
            }
        }
        return new PlaylistInfo
        {
            Title = Str(e, "title") ?? Str(e, "id") ?? "재생목록",
            Uploader = FirstLine(Str(e, "channel") ?? Str(e, "uploader")),
            Entries = entries,
        };
    }

    /// <summary>The thumbnail closest to (and at least) the preferred width; falls back to "thumbnail".</summary>
    private static string? BestThumbnail(JsonElement e, int preferWidth)
    {
        if (e.TryGetProperty("thumbnails", out var arr) && arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() > 0)
        {
            string? best = null;
            int bestScore = int.MaxValue;
            foreach (var t in arr.EnumerateArray())
            {
                var url = Str(t, "url");
                if (string.IsNullOrEmpty(url) || url.EndsWith(".webp", StringComparison.OrdinalIgnoreCase) && best != null) continue;
                int w = Int(t, "width") ?? (Int(t, "height") is int h ? h * 16 / 9 : 0);
                int score = w == 0 ? 5000 : w >= preferWidth ? w - preferWidth : (preferWidth - w) * 3;
                if (score < bestScore) { bestScore = score; best = url; }
            }
            if (best != null) return best;
        }
        return Str(e, "thumbnail");
    }

    private static string? FirstLine(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var i = s.IndexOfAny(['\n', '\r']);
        return (i >= 0 ? s[..i] : s).Trim();
    }

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        } : null;

    private static double? Num(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : null;

    private static int? Int(JsonElement e, string name) => Num(e, name) is double d ? (int)d : null;
}

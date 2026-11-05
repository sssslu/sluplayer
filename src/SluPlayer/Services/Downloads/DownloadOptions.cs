using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SluPlayer.Services.Settings;

namespace SluPlayer.Services.Downloads;

/// <summary>What to download: captured when an item is queued (later settings changes don't affect it).</summary>
public sealed record DownloadOptions
{
    /// <summary>video | audio</summary>
    public string Kind { get; init; } = "video";
    /// <summary>best | 4320 | 2160 | 1440 | 1080 | 720 | 480 | 360 | 240 | 144 (quality tier)</summary>
    public string Quality { get; init; } = "best";
    /// <summary>Exact "res" cap (short side) for the chosen tier, from analysis; overrides <see cref="Quality"/>.</summary>
    public int? ResCap { get; init; }
    /// <summary>mp4 | mkv</summary>
    public string Container { get; init; } = "mp4";
    /// <summary>mp3 | m4a</summary>
    public string AudioFormat { get; init; } = "mp3";

    [JsonIgnore] public bool IsAudio => Kind == "audio";

    public static DownloadOptions FromSettings(AppSettings s) => new DownloadOptions
    {
        Kind = s.DownloadKind,
        Quality = s.DownloadQuality,
        Container = s.DownloadVideoFormat,
        AudioFormat = s.DownloadAudioFormat,
    }.Normalized();

    public DownloadOptions Normalized() => this with
    {
        Kind = Kind is "audio" ? "audio" : "video",
        Quality = Quality == "best" || QualityTiers.TryParse(Quality, out _) ? Quality : "best",
        Container = Container is "mkv" ? "mkv" : "mp4",
        AudioFormat = AudioFormat is "m4a" ? "m4a" : "mp3",
    };

    /// <summary>Badge text for the list ("1080p · MP4", "MP3").</summary>
    [JsonIgnore]
    public string Label => IsAudio
        ? AudioFormat.ToUpperInvariant()
        : (Quality == "best" ? "최고 화질" : Quality + "p") + " · " + Container.ToUpperInvariant();
}

/// <summary>Builds yt-dlp argument lists for downloads (verified -f/-S strings, see the research report).</summary>
public static class DownloadArgs
{
    public const string OutputTemplate = "%(title).150B [%(id)s].%(ext)s";

    // ASCII-only: progress lines ignore --encoding.
    public const string ProgressTemplate =
        "download:[dl]%(progress.status)s|%(progress.downloaded_bytes)s|%(progress.total_bytes)s|" +
        "%(progress.total_bytes_estimate)s|%(progress.speed)s|%(progress.eta)s|" +
        "%(progress.fragment_index)s|%(progress.fragment_count)s|%(info.format_id)s";

    public const string PostprocessTemplate = "postprocess:[pp]%(progress.status)s|%(progress.postprocessor)s";


    /// <summary>The output template with an optional " (2)" style suffix (used when the plain name is taken).</summary>
    public static string Template(string? suffix)
        => string.IsNullOrEmpty(suffix) ? OutputTemplate : "%(title).150B [%(id)s]" + suffix.Replace("%", "%%") + ".%(ext)s";

    /// <param name="batch">Playlist/batch items: polite pauses between requests (YouTube may block fast batches).</param>
    /// <param name="nameSuffix">Appended to the file name when a different download already uses the plain name.</param>
    /// <param name="playlistItem">One video of a multi-video post (Instagram, X): the post URL alone gets all of them.</param>
    public static List<string> Build(DownloadOptions o, string outputDir, string url, bool batch,
        ToolPaths t, CookieLease cookies, string? nameSuffix = null, int? playlistItem = null)
    {
        var a = YtDlpService.CommonArgs(t, cookies);
        a.AddRange(playlistItem is int n
            ? ["--yes-playlist", "--playlist-items", n.ToString(CultureInfo.InvariantCulture)]
            : ["--no-playlist"]);
        a.AddRange([
            "--no-mtime",
            "-P", outputDir, "-o", Template(nameSuffix),
            // Parallel HLS/DASH fragments are much faster.
            "--concurrent-fragments", "4",
            "--newline", "--progress", "--progress-delta", "0.5",
            "--progress-template", ProgressTemplate, "--progress-template", PostprocessTemplate,
            "--print", "video:[sel]%(format_id)s|%(ext)s|%(filesize,filesize_approx)s",
            "--print", "video:[name]%(filename)j",
            "--print", "after_move:[file]%(filepath)j",
        ]);
        a.AddRange(FormatArgs(o));
        // Fixed extras: tags for everything, cover art for music, chapters for videos (one metadata pass, no
        // extra re-mux of large video files for a cover).
        a.Add("--embed-metadata");
        a.Add(o.IsAudio ? "--embed-thumbnail" : "--embed-chapters");
        if (batch) a.AddRange(["--sleep-requests", "0.75", "--sleep-interval", "5"]);
        a.Add("--");
        a.Add(url);
        return a;
    }

    /// <summary>
    /// Format selection. Notes from the fixture tests: "res" must come before any acodec preference (otherwise a 360p
    /// combined file wins), "lang" must be listed in custom sorts, and res:N compares the smaller side so vertical
    /// videos keep their resolution.
    /// </summary>
    public static IEnumerable<string> FormatArgs(DownloadOptions o)
    {
        if (o.IsAudio)
        {
            return o.AudioFormat == "m4a"
                ? ["-f", "ba[acodec^=mp4a]/ba/b", "-x", "--audio-format", "m4a"]
                : ["-f", "ba[acodec^=mp3]/ba/b", "-x", "--audio-format", "mp3", "--audio-quality", "192K"];
        }
        int? cap = o.ResCap ?? (QualityTiers.TryParse(o.Quality, out var tier) ? tier : null);
        var res = cap is int r ? "res:" + r.ToString(CultureInfo.InvariantCulture) : "res";
        string sort = o.Container == "mp4"
            ? $"lang,{res},fps,hdr:12,vcodec,acodec:aac"   // best picture; AAC audio fits MP4 best
            : $"lang,{res},fps,hdr:12";                    // default codec order (AV1/VP9 + Opus) into MKV
        return ["-f", "bv*+ba/b", "-S", sort, "--merge-output-format", o.Container];
    }
}

/// <summary>Quality tiers ("1080p") shown to the user, independent of odd sizes like 1920×800 or 1080×1920.</summary>
public static class QualityTiers
{
    public static readonly int[] Standard = [4320, 2160, 1440, 1080, 720, 480, 360, 240, 144];

    /// <summary>The tiers offered before any analysis (playlists).</summary>
    public static readonly int[] Common = [2160, 1440, 1080, 720, 480, 360];

    public static bool TryParse(string? s, out int tier)
        => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out tier) && tier is >= 100 and <= 8000;

    private static readonly Regex NoteRe = new(@"^(\d{3,4})p", RegexOptions.Compiled);

    public static int TierOf(MediaFormat f)
    {
        if (f.Note is { } note && NoteRe.Match(note) is { Success: true } m)
            return int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        int shortSide = f.ShortSide, longSide = f.LongSide;
        if (shortSide <= 0) return 0;
        if (longSide > 0 && (double)longSide / shortSide >= 1.9)
        {
            // Cinemascope etc.: name it after the width like YouTube does (1920×800 → 1080p).
            return longSide switch
            {
                >= 6000 => 4320, >= 3200 => 2160, >= 2200 => 1440, >= 1600 => 1080,
                >= 1100 => 720, >= 750 => 480, >= 560 => 360, >= 380 => 240, _ => 144,
            };
        }
        foreach (var t in Standard)
            if (Math.Abs(shortSide - t) <= t * 0.03) return t;
        return shortSide;
    }

    public static string Label(int tier, double? fps, bool hdr)
    {
        var s = tier + "p";
        if (fps is double f && f > 31) s += Math.Round(f).ToString(CultureInfo.InvariantCulture);
        if (hdr) s += " HDR";
        return s;
    }

    public static string TierName(int tier) => tier switch
    {
        4320 => "4320p (8K)",
        2160 => "2160p (4K)",
        _ => tier + "p",
    };
}

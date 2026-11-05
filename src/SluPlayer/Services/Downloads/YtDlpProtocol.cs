using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SluPlayer.Services.Downloads;

// Machine-readable lines we ask yt-dlp to print (see DownloadArgs), verified against yt-dlp 2026.10.26:
//   [sel]399+251|mkv|4285705                       --print video:…      (chosen formats, ext, summed size)
//   [name]"C:\\dl\\Title [id].mkv"                  --print video:…      (prepared file name, JSON-escaped)
//   [dl]downloading|1047552|3824121|NA|2092413.09|1|NA|NA|399            (progress template, ASCII only)
//   [pp]started|Merger                             (post-processor template; arrives on stderr)
//   [file]"C:\\dl\\Title [id].mkv"                 --print after_move:… (final path, JSON-escaped)
// Progress lines ignore --encoding, so they only carry numbers and format ids; text fields use the 'j' conversion.

public abstract record YtEvent;

public sealed record ProgressEvent(string Status, long? Downloaded, long? Total, long? TotalEstimate, double? Speed,
    double? Eta, int? FragmentIndex, int? FragmentCount, string FormatId) : YtEvent
{
    public long? BestTotal => Total ?? TotalEstimate;
}

public sealed record PostprocessEvent(string Status, string Postprocessor) : YtEvent;

public sealed record SelectionEvent(string FormatId, string Ext, long? Size) : YtEvent
{
    public string[] Parts => FormatId.Split('+', StringSplitOptions.RemoveEmptyEntries);
}

public sealed record FileEvent(string Path) : YtEvent;

public sealed record NameEvent(string Path) : YtEvent;

public sealed record MessageEvent(bool IsError, string Text) : YtEvent;

public enum YtErrorKind
{
    None, BotCheck, AgeRestricted, LoginRequired, Private, Unavailable, MembersOnly, GeoBlocked, Drm, LiveNotStarted,
    UnsupportedUrl, InvalidUrl, Network, Http403, Http429, FormatNotAvailable, FfmpegMissing, DiskFull, Permission,
    BadOption, Cookies, EngineMissing, Timeout, Other,
}

public static class YtDlpParser
{
    private static string? N(string s) => s is "NA" or "" or "None" or "null" ? null : s;

    private static long? L(string s)
        => N(s) is { } v && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && !double.IsNaN(d)
            ? (long)d : null;

    private static double? D(string s)
        => N(s) is { } v && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && !double.IsNaN(d)
            ? d : null;

    public static YtEvent? Parse(string line, bool fromStderr)
    {
        if (line.Length == 0) return null;
        if (line.StartsWith("[dl]", StringComparison.Ordinal))
        {
            var f = line[4..].Split('|');
            if (f.Length < 9) return null;
            return new ProgressEvent(f[0], L(f[1]), L(f[2]), L(f[3]), D(f[4]), D(f[5]), (int?)L(f[6]), (int?)L(f[7]), f[8]);
        }
        if (line.StartsWith("[pp]", StringComparison.Ordinal))
        {
            var f = line[4..].Split('|');
            return f.Length >= 2 ? new PostprocessEvent(f[0], f[1].Trim()) : null;
        }
        if (line.StartsWith("[sel]", StringComparison.Ordinal))
        {
            var f = line[5..].Split('|');
            return f.Length >= 3 ? new SelectionEvent(f[0], f[1], L(f[2])) : null;
        }
        if (line.StartsWith("[file]", StringComparison.Ordinal))
            return DecodeJsonString(line[6..]) is { } p ? new FileEvent(p) : null;
        if (line.StartsWith("[name]", StringComparison.Ordinal))
            return DecodeJsonString(line[6..]) is { } n ? new NameEvent(n) : null;
        if (line.StartsWith("ERROR: ", StringComparison.Ordinal))
            return new MessageEvent(true, line[7..]);
        if (line.StartsWith("WARNING: ", StringComparison.Ordinal))
            return new MessageEvent(false, line[9..]);
        if (fromStderr && line.StartsWith("yt-dlp", StringComparison.OrdinalIgnoreCase) && line.Contains("error: ", StringComparison.Ordinal))
            return new MessageEvent(true, line);
        return null;
    }

    private static string? DecodeJsonString(string json)
    {
        try { return JsonSerializer.Deserialize<string>(json.Trim()); }
        catch { return null; }
    }

    // ---- error classification -------------------------------------------------------

    private static readonly (Regex Re, YtErrorKind Kind)[] Rules =
    [
        // "Sign in to confirm you’re not a bot" — the apostrophe is U+2019, so match on "not a bot".
        (new(@"not a bot", RegexOptions.IgnoreCase), YtErrorKind.BotCheck),
        (new(@"confirm your age|age.restricted|inappropriate for some users", RegexOptions.IgnoreCase), YtErrorKind.AgeRestricted),
        (new(@"members.only|Join this channel", RegexOptions.IgnoreCase), YtErrorKind.MembersOnly),
        (new(@"Private video", RegexOptions.IgnoreCase), YtErrorKind.Private),
        (new(@"only works when logged.in|requires? (authentication|login|logging in|an account)|login required|account credentials|This video is only available for registered users|Sign in to|Restricted Video", RegexOptions.IgnoreCase), YtErrorKind.LoginRequired),
        (new(@"not (made )?available in your (country|region)|geo.?restrict", RegexOptions.IgnoreCase), YtErrorKind.GeoBlocked),
        (new(@"\bDRM\b", RegexOptions.None), YtErrorKind.Drm),
        (new(@"live event will begin|Premieres in|This live stream|is not live|will begin in", RegexOptions.IgnoreCase), YtErrorKind.LiveNotStarted),
        // Local/technical problems before the generic "unavailable" texts ("ffmpeg-location … does not exist").
        (new(@"ffmpeg (is )?not (installed|found)|ffprobe.* not found|ffmpeg-location .* does not exist", RegexOptions.IgnoreCase), YtErrorKind.FfmpegMissing),
        (new(@"No space left|disk is full|not enough space|Errno 28", RegexOptions.IgnoreCase), YtErrorKind.DiskFull),
        (new(@"Permission denied|Access is denied|WinError 5\b|Errno 13", RegexOptions.IgnoreCase), YtErrorKind.Permission),
        (new(@"no such option|error: unrecognized|ambiguous option", RegexOptions.IgnoreCase), YtErrorKind.BadOption),
        (new(@"cookies database|failed to decrypt|cookie file|cookies file|could not find .* cookies", RegexOptions.IgnoreCase), YtErrorKind.Cookies),
        // "empty media response": Instagram's answer for deleted posts and private accounts.
        (new(@"Video unavailable|This video is unavailable|has been removed|account .* terminated|playlist does not exist|HTTP Error 404|does not exist|empty media response", RegexOptions.IgnoreCase), YtErrorKind.Unavailable),
        (new(@"Unsupported URL", RegexOptions.None), YtErrorKind.UnsupportedUrl),
        (new(@"is not a valid URL|Incomplete YouTube ID|looks truncated", RegexOptions.None), YtErrorKind.InvalidUrl),
        (new(@"HTTP Error 403", RegexOptions.None), YtErrorKind.Http403),
        (new(@"HTTP Error 429|Too Many Requests|rate.limit", RegexOptions.IgnoreCase), YtErrorKind.Http429),
        (new(@"Requested format is not available", RegexOptions.None), YtErrorKind.FormatNotAvailable),
        (new(@"Failed to resolve|getaddrinfo|timed out|Connection (refused|reset|aborted)|Unable to download (webpage|API page|JSON)|Network is unreachable|No route to host|SSL: |CERTIFICATE_VERIFY|RemoteDisconnected|IncompleteRead", RegexOptions.IgnoreCase), YtErrorKind.Network),
    ];

    public static YtErrorKind Classify(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return YtErrorKind.Other;
        foreach (var (re, kind) in Rules)
            if (re.IsMatch(text)) return kind;
        return YtErrorKind.Other;
    }
}

//// <summary>A classified yt-dlp failure with a short Korean message for the user.</summary>
public sealed record YtDlpError(YtErrorKind Kind, string Message, bool NeedsLogin, string? Detail)
{
    /// <param name="stderr">yt-dlp's stderr (only the ERROR lines matter).</param>
    /// <param name="url">The URL that failed (decides whether the in-app YouTube sign-in can help).</param>
    /// <param name="downloading">true for downloads, false for analysis (changes the fallback text).</param>
    public static YtDlpError From(string? stderr, string? url, bool downloading)
    {
        var errorText = ErrorText(stderr);
        var kind = YtDlpParser.Classify(errorText ?? stderr);
        bool youtube = UrlTools.IsYouTube(url);
        // The message depends on whether yt-dlp got the in-app sign-in cookies.
        bool loggedIn = YtDlpCookies.HasYouTubeLogin;
        string? detail = YtDlpErrors.LastErrorLine(stderr) ?? errorText;

        string msg;
        bool needsLogin = false;
        switch (kind)
        {
            case YtErrorKind.BotCheck:
                needsLogin = true;
                msg = loggedIn ? "YouTube 로그인 만료. 다시 로그인 필요" : "YouTube 로그인 필요";
                break;
            case YtErrorKind.AgeRestricted:
                needsLogin = youtube && !loggedIn;
                msg = needsLogin ? "연령 제한 영상. YouTube 로그인 필요" : "연령 제한 영상";
                break;
            case YtErrorKind.LoginRequired:
                needsLogin = youtube;
                // The in-app sign-in is YouTube only.
                msg = youtube ? "YouTube 로그인 필요" : "로그인 필요";
                break;
            case YtErrorKind.Cookies:
                needsLogin = youtube;
                msg = "로그인 정보 읽기 실패. 다시 로그인 필요";
                break;
            case YtErrorKind.MembersOnly: msg = "멤버십 전용 영상"; break;
            case YtErrorKind.Private: msg = "비공개 영상"; break;
            case YtErrorKind.GeoBlocked: msg = "이 국가에서 볼 수 없는 영상"; break;
            case YtErrorKind.Drm: msg = "DRM 보호 영상"; break;
            case YtErrorKind.LiveNotStarted: msg = "시작 전 라이브"; break;
            case YtErrorKind.Unavailable: msg = "볼 수 없는 영상 (삭제 또는 비공개)"; break;
            case YtErrorKind.UnsupportedUrl:
                // Pages that need an account redirect to the login page, which yt-dlp then reports as unsupported.
                msg = (errorText ?? "").Contains("login", StringComparison.OrdinalIgnoreCase) ? "로그인 필요한 링크" : "지원하지 않는 링크";
                break;
            case YtErrorKind.InvalidUrl: msg = "잘못된 주소"; break;
            case YtErrorKind.Http403: msg = "접근 거부 (403). 엔진 업데이트 필요"; break;
            case YtErrorKind.Http429: msg = "요청 과다 (429). 잠시 후 다시 시도"; break;
            case YtErrorKind.FormatNotAvailable: msg = "선택한 화질 없음"; break;
            case YtErrorKind.FfmpegMissing: msg = "ffmpeg 없음. 재설치 필요"; break;
            case YtErrorKind.DiskFull: msg = "저장 공간 부족"; break;
            case YtErrorKind.Permission: msg = "저장 폴더에 쓸 수 없음"; break;
            case YtErrorKind.Network: msg = "네트워크 오류"; break;
            case YtErrorKind.BadOption: msg = "엔진 오류. 엔진 업데이트 필요"; break;
            default:
                msg = YtDlpErrors.Translate(stderr) is { } t && !t.StartsWith(YtDlpErrors.EnginePrefix, StringComparison.Ordinal)
                    ? t
                    : downloading
                        ? "다운로드 실패" + (detail != null ? ": " + Shorten(detail, 140) : "")
                        : "분석 실패";
                break;
        }
        return new YtDlpError(kind, msg, needsLogin, detail);
    }

    public static YtDlpError EngineMissing() => new(YtErrorKind.EngineMissing, "yt-dlp 없음. 재설치 필요", false, null);

    public static YtDlpError TimedOut() => new(YtErrorKind.Timeout, "응답 없음. 네트워크 확인", false, null);

    /// <summary>All ERROR lines joined (Python tracebacks without an ERROR line fall back to the last line).</summary>
    private static string? ErrorText(string? stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr)) return null;
        var lines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var errors = lines.Where(l => l.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase)
                                      || l.Contains("error: ", StringComparison.Ordinal)).ToList();
        if (errors.Count > 0) return string.Join("\n", errors);
        return lines.LastOrDefault();
    }

    private static string Shorten(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}

/// <summary>Thrown by analysis calls; carries the classified error.</summary>
public sealed class YtDlpException(YtDlpError error) : Exception(error.Message)
{
    public YtDlpError Error { get; } = error;
}

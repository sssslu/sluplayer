namespace SluPlayer.Services;

/// <summary>Turns yt-dlp stderr into a short Korean message for the user (shared by streaming and downloads).</summary>
public static class YtDlpErrors
{
    /// <summary>Prefix of the fallback message that only repeats yt-dlp's own error line.</summary>
    public const string EnginePrefix = "엔진 오류: ";

    private static readonly (string Needle, string Message)[] Known =
    [
        ("confirm your age", "연령 제한 영상. YouTube 로그인 필요"),
        ("not a bot", "YouTube 로그인 필요"),
        ("Sign in to", "YouTube 로그인 필요"),
        ("members-only", "멤버십 전용 영상"),
        ("Private video", "비공개 영상"),
        ("This live event will begin", "시작 전 라이브"),
        ("Premieres in", "공개 전 영상"),
        ("Video unavailable", "볼 수 없는 영상"),
        ("This video has been removed", "삭제된 영상"),
        ("not available in your country", "이 국가에서 볼 수 없는 영상"),
        ("geo restrict", "지역 제한 영상"),
        ("Unsupported URL", "지원하지 않는 주소"),
        ("is not a valid URL", "잘못된 주소"),
        ("HTTP Error 403", "접근 거부 (403). 엔진 업데이트 필요"),
        ("HTTP Error 404", "영상 없음 (404)"),
        ("HTTP Error 429", "요청 과다 (429). 잠시 후 다시 시도"),
        ("Unable to download webpage", "네트워크 오류"),
        ("getaddrinfo failed", "네트워크 오류"),
        ("timed out", "서버 응답 없음"),
        ("Requested format is not available", "선택한 화질 없음"),
        ("ffmpeg not found", "ffmpeg 없음. 재설치 필요"),
        ("No space left", "저장 공간 부족"),
        ("JavaScript runtime", "deno 없음. 재설치 필요"),
    ];

    /// <summary>True when the failure would be fixed by signing in to YouTube (bot check, age gate, private...).</summary>
    public static bool NeedsLogin(string? text)
        => text != null && (text.Contains("not a bot", StringComparison.OrdinalIgnoreCase)
                            || text.Contains("confirm your age", StringComparison.OrdinalIgnoreCase)
                            || text.Contains("Sign in to", StringComparison.OrdinalIgnoreCase)
                            || text.Contains("로그인", StringComparison.Ordinal));

    /// <summary>Best user-facing message for a yt-dlp failure, or null when nothing useful is found.</summary>
    public static string? Translate(string? stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr)) return null;
        foreach (var (needle, msg) in Known)
            if (stderr.Contains(needle, StringComparison.OrdinalIgnoreCase)) return msg;
        var line = LastErrorLine(stderr);
        return line == null ? null : EnginePrefix + line;
    }

    /// <summary>The last "ERROR:" line of yt-dlp output, without the prefix.</summary>
    public static string? LastErrorLine(string? stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr)) return null;
        var lines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var err = lines.LastOrDefault(l => l.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase));
        if (err == null) return null;
        err = err["ERROR:".Length..].Trim();
        // Drop the "[youtube] id:" extractor prefix.
        if (err.StartsWith('[') && err.IndexOf("] ", StringComparison.Ordinal) is var i and > 0)
        {
            err = err[(i + 2)..];
            var colon = err.IndexOf(": ", StringComparison.Ordinal);
            if (colon is > 0 and < 20) err = err[(colon + 2)..];
        }
        return err.Length > 240 ? err[..240] + "…" : err;
    }
}

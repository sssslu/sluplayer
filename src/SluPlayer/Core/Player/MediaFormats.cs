using System.IO;

namespace SluPlayer.Core.Player;

public enum MediaKind { Unknown, Video, Audio, Subtitle, Playlist }

/// <summary>Known media extensions (used for open dialogs, folder scans, drag &amp; drop routing).</summary>
public static class MediaFormats
{
    public static readonly HashSet<string> Video = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mkv", ".webm", ".avi", ".mov", ".qt", ".wmv", ".asf", ".flv", ".f4v",
        ".ts", ".m2ts", ".mts", ".m2t", ".tp", ".trp", ".mpg", ".mpeg", ".mpe", ".m1v", ".m2v",
        ".vob", ".evo", ".3gp", ".3g2", ".ogv", ".divx", ".xvid", ".rm", ".rmvb", ".dv", ".mxf",
        ".y4m", ".h264", ".264", ".h265", ".265", ".hevc", ".avc", ".ivf", ".obu", ".nut", ".bik",
    };

    public static readonly HashSet<string> Audio = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".m4a", ".aac", ".ogg", ".oga", ".opus", ".wav", ".wma", ".ape", ".alac",
        ".aiff", ".aif", ".wv", ".mka", ".ac3", ".eac3", ".dts", ".dtshd", ".thd", ".mp2", ".mpc",
        ".tta", ".amr", ".au", ".caf", ".dsf", ".dff", ".mid", ".midi", ".spx", ".weba",
    };

    public static readonly HashSet<string> Subtitle = new(StringComparer.OrdinalIgnoreCase)
    {
        ".srt", ".ass", ".ssa", ".smi", ".sami", ".vtt", ".sub", ".idx", ".sup", ".txt", ".lrc", ".ttml", ".dfxp",
    };

    public static readonly HashSet<string> Playlist = new(StringComparer.OrdinalIgnoreCase)
    {
        ".m3u", ".m3u8", ".pls", ".cue", ".xspf",
    };

    public static MediaKind Classify(string pathOrUrl)
    {
        if (IsUrl(pathOrUrl)) return MediaKind.Video;
        string ext;
        try { ext = Path.GetExtension(pathOrUrl); }
        catch { return MediaKind.Unknown; }
        if (Video.Contains(ext)) return MediaKind.Video;
        if (Audio.Contains(ext)) return MediaKind.Audio;
        // .txt is too generic to treat as a subtitle unless explicitly dropped next to a video.
        if (Subtitle.Contains(ext) && !ext.Equals(".txt", StringComparison.OrdinalIgnoreCase)) return MediaKind.Subtitle;
        if (Playlist.Contains(ext)) return MediaKind.Playlist;
        return MediaKind.Unknown;
    }

    public static bool IsUrl(string s)
        => s.Contains("://", StringComparison.Ordinal) &&
           (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            s.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            s.StartsWith("rtmp://", StringComparison.OrdinalIgnoreCase) ||
            s.StartsWith("rtmps://", StringComparison.OrdinalIgnoreCase) ||
            s.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase) ||
            s.StartsWith("rtp://", StringComparison.OrdinalIgnoreCase) ||
            s.StartsWith("udp://", StringComparison.OrdinalIgnoreCase) ||
            s.StartsWith("srt://", StringComparison.OrdinalIgnoreCase) ||
            s.StartsWith("mms://", StringComparison.OrdinalIgnoreCase) ||
            s.StartsWith("ytdl://", StringComparison.OrdinalIgnoreCase));

    /// <summary>Recursively (optionally) lists playable media files in a folder, sorted naturally.</summary>
    /// <exception cref="OperationCanceledException">The scan was cancelled.</exception>
    public static List<string> EnumerateFolder(string folder, bool recursive, CancellationToken cancel = default)
    {
        var result = new List<string>();
        try
        {
            var opts = new EnumerationOptions
            {
                RecurseSubdirectories = recursive,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
            };
            foreach (var f in Directory.EnumerateFiles(folder, "*", opts))
            {
                cancel.ThrowIfCancellationRequested();
                var k = Classify(f);
                if (k is MediaKind.Video or MediaKind.Audio) result.Add(f);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { }
        result.Sort(NaturalStringComparer.Instance);
        return result;
    }

    public static string OpenFileFilter =>
        "미디어 파일|" + Join(Video) + ";" + Join(Audio) + ";" + Join(Playlist) +
        "|동영상|" + Join(Video) +
        "|오디오|" + Join(Audio) +
        "|재생목록|" + Join(Playlist) +
        "|모든 파일|*.*";

    public static string SubtitleFileFilter => "자막 파일|" + Join(Subtitle) + "|모든 파일|*.*";

    private static string Join(IEnumerable<string> exts) => string.Join(";", exts.Select(e => "*" + e));
}

/// <summary>Windows-Explorer-like natural sort ("ep2" &lt; "ep10").</summary>
public sealed class NaturalStringComparer : IComparer<string>
{
    public static readonly NaturalStringComparer Instance = new();

    [System.Runtime.InteropServices.DllImport("shlwapi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int StrCmpLogicalW(string x, string y);

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x == null) return -1;
        if (y == null) return 1;
        return StrCmpLogicalW(x, y);
    }
}

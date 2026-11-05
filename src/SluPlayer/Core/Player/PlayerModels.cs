using SluPlayer.Core.Mpv;

namespace SluPlayer.Core.Player;

public enum TrackType { Video, Audio, Subtitle }

public enum LoopMode { None, File, Playlist }

/// <summary>One entry of mpv's track-list.</summary>
public sealed record MediaTrack
{
    public long Id { get; init; }
    public TrackType Type { get; init; }
    public string? Title { get; init; }
    public string? Lang { get; init; }
    public string? Codec { get; init; }
    public bool IsForced { get; init; }
    public bool IsExternal { get; init; }
    public bool IsSelected { get; init; }
    public bool IsAlbumArt { get; init; }
    public bool IsImage { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public double Fps { get; init; }
    public int Channels { get; init; }
    public long SampleRate { get; init; }

    public string DisplayName
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Title)) parts.Add(Title!);
            var lang = LanguageNames.ToKorean(Lang);
            if (!string.IsNullOrEmpty(lang)) parts.Add(lang);
            var detail = Type switch
            {
                TrackType.Video when Width > 0 => $"{Codec} {Width}x{Height}" + (Fps > 0 ? $" {Fps:0.###}fps" : ""),
                TrackType.Audio => Codec + (Channels > 0 ? $" {ChannelText(Channels)}" : "") + (SampleRate > 0 ? $" {SampleRate / 1000.0:0.#}kHz" : ""),
                _ => Codec,
            };
            if (!string.IsNullOrWhiteSpace(detail)) parts.Add(detail!.Trim());
            if (IsExternal) parts.Add("외부");
            if (IsForced) parts.Add("강제");
            var text = string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
            return $"#{Id}  " + (text.Length > 0 ? text : "트랙");
        }
    }

    private static string ChannelText(int ch) => ch switch
    {
        1 => "모노",
        2 => "스테레오",
        6 => "5.1ch",
        8 => "7.1ch",
        _ => $"{ch}ch",
    };

    internal static MediaTrack? FromNode(object? node)
    {
        if (node is not Dictionary<string, object?> m) return null;
        var type = m.GetString("type") switch
        {
            "video" => TrackType.Video,
            "audio" => TrackType.Audio,
            "sub" => TrackType.Subtitle,
            _ => (TrackType?)null,
        };
        if (type == null) return null;
        return new MediaTrack
        {
            Id = m.GetLong("id") ?? 0,
            Type = type.Value,
            Title = m.GetString("title"),
            Lang = m.GetString("lang"),
            Codec = m.GetString("codec"),
            IsForced = m.GetBool("forced"),
            IsExternal = m.GetBool("external"),
            IsSelected = m.GetBool("selected"),
            IsAlbumArt = m.GetBool("albumart"),
            IsImage = m.GetBool("image"),
            Width = (int)(m.GetLong("demux-w") ?? 0),
            Height = (int)(m.GetLong("demux-h") ?? 0),
            Fps = m.GetDouble("demux-fps") ?? 0,
            Channels = (int)(m.GetLong("demux-channel-count") ?? 0),
            SampleRate = m.GetLong("demux-samplerate") ?? 0,
        };
    }
}

public sealed record ChapterInfo(int Index, double Time, string Title)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Title) ? $"챕터 {Index + 1}" : Title;
}

public sealed record PlaylistEntry(int Index, long Id, string Filename, string? Title, bool IsCurrent)
{
    public string DisplayName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Title)) return Title!;
            if (MediaFormats.IsUrl(Filename)) return Filename;
            try { return System.IO.Path.GetFileName(Filename); } catch { return Filename; }
        }
    }
}

public readonly record struct TimeRange(double Start, double End);

/// <summary>ISO-639 codes to Korean language names for track menus.</summary>
public static class LanguageNames
{
    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ko"] = "한국어", ["kor"] = "한국어", ["ko-kr"] = "한국어",
        ["en"] = "영어", ["eng"] = "영어", ["en-us"] = "영어", ["en-gb"] = "영어",
        ["ja"] = "일본어", ["jpn"] = "일본어", ["jp"] = "일본어",
        ["zh"] = "중국어", ["chi"] = "중국어", ["zho"] = "중국어", ["zh-hans"] = "중국어(간체)", ["zh-hant"] = "중국어(번체)",
        ["fr"] = "프랑스어", ["fre"] = "프랑스어", ["fra"] = "프랑스어",
        ["de"] = "독일어", ["ger"] = "독일어", ["deu"] = "독일어",
        ["es"] = "스페인어", ["spa"] = "스페인어",
        ["it"] = "이탈리아어", ["ita"] = "이탈리아어",
        ["ru"] = "러시아어", ["rus"] = "러시아어",
        ["pt"] = "포르투갈어", ["por"] = "포르투갈어",
        ["th"] = "태국어", ["tha"] = "태국어",
        ["vi"] = "베트남어", ["vie"] = "베트남어",
        ["ar"] = "아랍어", ["ara"] = "아랍어",
        ["hi"] = "힌디어", ["hin"] = "힌디어",
        ["id"] = "인도네시아어", ["ind"] = "인도네시아어",
        ["und"] = "",
    };

    public static string ToKorean(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return "";
        return Map.TryGetValue(code.Trim(), out var name) ? name : code.Trim();
    }
}

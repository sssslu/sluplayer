using System.Globalization;
using System.IO;
using SluPlayer.Services;
using SluPlayer.Services.Settings;

namespace SluPlayer.Core.Player;

/// <summary>Translates <see cref="AppSettings"/> into the mpv option set applied before mpv_initialize.</summary>
/// <remarks>Option names verified against mpv master (v0.41.0-dev); see docs/ARCHITECTURE.md.</remarks>
public static class MpvOptionBuilder
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static IEnumerable<(string Name, string Value)> Build(AppSettings s, nint wid)
    {
        // ---- isolation from any user-installed mpv config/scripts ----------
        var mpvConfigDir = Path.Combine(AppPaths.DataDir, "mpv");
        try { Directory.CreateDirectory(mpvConfigDir); } catch { }
        yield return ("config-dir", mpvConfigDir);
        yield return ("config", "no");
        yield return ("load-scripts", "no");
        yield return ("terminal", "no");

        // ---- embedding: mpv draws video only, WPF owns all input ------------
        yield return ("wid", ((long)wid).ToString(Inv));
        yield return ("input-default-bindings", "no");
        yield return ("input-builtin-bindings", "no");
        yield return ("input-vo-keyboard", "no");
        yield return ("input-cursor", "no");
        yield return ("cursor-autohide", "no");           // required with input-cursor=no
        yield return ("window-dragging", "no");
        yield return ("input-builtin-dragging", "no");
        yield return ("input-builtin-drag-and-drop", "no"); // mpv master; ignored if unknown
        yield return ("drag-and-drop", "no");               // older name
        yield return ("osc", "no");
        yield return ("osd-bar", "no");
        yield return ("osd-on-seek", "no");
        yield return ("idle", "yes");
        yield return ("keep-open", "yes");
        yield return ("force-window", "yes");
        yield return ("taskbar-progress", "no");           // WPF TaskbarItemInfo does this
        yield return ("load-console", "no");
        yield return ("load-commands", "no");
        yield return ("load-select", "no");
        yield return ("load-positioning", "no");
        yield return ("load-auto-profiles", "no");
        yield return ("load-stats-overlay", "no");
        yield return ("media-controls", "yes");            // Windows SMTC (media keys, volume flyout)

        // ---- GPU decode + render --------------------------------------------
        yield return ("vo", "gpu-next");
        yield return ("gpu-api", "d3d11");
        yield return ("gpu-context", "d3d11");
        yield return ("hwdec", EffectiveHwdec(s.Hwdec));
        var adapter = GpuInfo.PreferredAdapterName; // hybrid GPUs: the high-performance one
        if (!string.IsNullOrWhiteSpace(adapter))
            yield return ("d3d11-adapter", adapter!);
        // config=no makes ~~cache paths resolve to nothing, so these must be explicit.
        yield return ("gpu-shader-cache-dir", AppPaths.ShaderCacheDir);
        yield return ("icc-cache-dir", Path.Combine(AppPaths.LocalDir, "icc-cache"));
        // auto: HDR output on an HDR desktop, tone-mapping on SDR.
        yield return ("target-colorspace-hint", "auto");
        // Only frames flagged as interlaced (on the GPU with hardware decoding).
        yield return ("deinterlace", "auto");

        // ---- playback behaviour ---------------------------------------------
        yield return ("save-position-on-quit", "no");
        yield return ("resume-playback", "no"); // SluPlayer keeps its own resume positions (history.json)
        yield return ("hr-seek", "default");
        yield return ("hr-seek-framedrop", "yes");
        yield return ("demuxer-max-bytes", "512MiB");
        yield return ("demuxer-max-back-bytes", "256MiB");
        yield return ("cache-pause-wait", "2");
        yield return ("network-timeout", "20");
        yield return ("reset-on-next-file", "video-rotate,video-aspect-override,sub-delay,audio-delay");
        yield return ("loop-file", s.LoopMode == "file" ? "inf" : "no");
        yield return ("loop-playlist", s.LoopMode == "playlist" ? "inf" : "no");
        // Shuffle mode: mpv shuffles every list it adds (loadlist, playlist files) before it picks the entry to play.
        yield return ("shuffle", s.Shuffle ? "yes" : "no");
        yield return ("screenshot-dir", s.ScreenshotDir);
        yield return ("screenshot-format", "png");
        // %n: mpv counts up until the name is free (a second capture of the same paused frame would fail otherwise).
        yield return ("screenshot-template", "%F_%wH-%wM-%wS-%wT_%n");
        yield return ("screenshot-high-bit-depth", "no");

        // ---- audio -----------------------------------------------------------
        yield return ("volume-max", AppSettings.MaxVolume.ToString(Inv));
        yield return ("volume", s.Volume.ToString("0", Inv));
        yield return ("mute", s.Muted ? "yes" : "no");
        yield return ("audio-channels", "auto-safe");
        yield return ("alang", "ko,kor,en,eng,ja,jpn");
        // Only "<same name>.<audio ext>" (separate audio track releases); fuzzy would grab unrelated songs.
        yield return ("audio-file-auto", "exact");

        // ---- subtitles -------------------------------------------------------
        yield return ("sub-auto", "fuzzy");
        yield return ("sub-file-paths", "sub;subs;subtitle;subtitles;Sub;Subs;Subtitles");
        yield return ("slang", "ko,kor,en,eng");
        // mpv tries UTF-8 first, then this codepage: cp949 covers Korean SMI/SRT (incl. EUC-KR).
        yield return ("sub-codepage", "cp949");
        yield return ("sub-font", "Malgun Gothic");
        yield return ("sub-font-size", s.SubtitleFontSize.ToString(Inv));
        yield return ("sub-bold", "yes");
        yield return ("sub-color", "#FFFFFFFF");
        yield return ("sub-outline-color", "#FF000000");
        yield return ("sub-outline-size", "2.5");
        // outline-and-shadow: sub-back-color is the shadow colour.
        yield return ("sub-border-style", "outline-and-shadow");
        yield return ("sub-back-color", "#80000000");
        yield return ("sub-shadow-offset", "1.5");
        yield return ("sub-pos", "100");
        yield return ("sub-ass-override", "scale");

        // ---- OSD look (used for volume/seek/status feedback) -----------------
        yield return ("osd-font", "Segoe UI"); // Hangul falls back to Malgun Gothic; avoids \ → ₩
        yield return ("osd-font-size", "24");
        yield return ("osd-bold", "yes");
        yield return ("osd-color", "#FFFFFFFF");
        yield return ("osd-outline-color", "#CC000000");
        yield return ("osd-outline-size", "1.4");
        yield return ("osd-shadow-offset", "0.8");
        yield return ("osd-margin-x", "26");
        yield return ("osd-margin-y", "20");
        yield return ("osd-align-x", "left");
        yield return ("osd-align-y", "top");
        yield return ("osd-duration", "1500");

        // ---- online streams through yt-dlp (paths go through BuildMaps) ----------
        yield return ("ytdl", ToolLocator.YtDlp != null ? "yes" : "no");
        // Streams up to 1080p (the best formats above that are large and rarely needed in a window).
        yield return ("ytdl-format", "bestvideo*[height<=?1080]+bestaudio/best[height<=?1080]/bestvideo*+bestaudio/best");
    }

    /// <summary>Key/value-list options, applied as node maps so paths need no escaping.</summary>
    public static IEnumerable<(string Name, Dictionary<string, string> Map)> BuildMaps()
    {
        var ytdlp = ToolLocator.YtDlp;
        if (ytdlp != null) yield return ("script-opts", new Dictionary<string, string> { ["ytdl_hook-ytdl_path"] = ytdlp });

        // Each pair becomes "--key value" on yt-dlp's command line ("" = bare flag).
        var raw = new Dictionary<string, string>
        {
            ["ignore-config"] = "",
            ["cache-dir"] = AppPaths.YtDlpCacheDir,
        };
        var deno = ToolLocator.Deno;
        if (deno != null) raw["js-runtimes"] = "deno:" + deno;
        if (YtDlpCookies.RawOption() is { } cookie) raw[cookie.Key] = cookie.Value;
        yield return ("ytdl-raw-options", raw);
    }

    /// <summary>
    /// The hwdec value mpv gets for a setting (settings keep the plain choice). mpv renders with Direct3D 11, which has
    /// no CUDA interop: NVDEC always uses its copy variant, D3D11VA falls back to its copy variant.
    /// </summary>
    public static string EffectiveHwdec(string hwdec) => hwdec switch
    {
        _ when string.IsNullOrWhiteSpace(hwdec) => "auto-safe",
        "nvdec" => "nvdec-copy",
        "d3d11va" => "d3d11va,d3d11va-copy",
        _ => hwdec,
    };
}

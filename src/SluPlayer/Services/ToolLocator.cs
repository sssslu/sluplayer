using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using SluPlayer.Services.Downloads;

namespace SluPlayer.Services;

/// <summary>
/// Locates the helper executables (yt-dlp, deno, ffmpeg). Copies in %LOCALAPPDATA%\SluPlayer\tools win over the
/// bundled ones in &lt;app&gt;\tools; for yt-dlp only while the self-updated copy is newer than the bundled one.
/// </summary>
public static partial class ToolLocator
{
    /// <summary>
    /// yt-dlp: the unpacked "onedir" build lives in a sub-folder (tools\yt-dlp\yt-dlp.exe + _internal\);
    /// a single-file yt-dlp.exe directly in tools\ is also accepted. After an app update the bundled engine may be
    /// newer than an earlier self-update: the newer one wins (decided from the version stamps, no process start).
    /// </summary>
    public static string? YtDlp
    {
        get
        {
            var updated = Path.Combine(UpdatedYtDlpDir, "yt-dlp.exe");
            var bundled = Path.Combine(AppPaths.BundledToolsDir, "yt-dlp", "yt-dlp.exe");
            if (File.Exists(updated))
                return !PreferUpdatedYtDlp && File.Exists(bundled) && IsNotOlder(bundled, updated) ? bundled : updated;
            return File.Exists(bundled) ? bundled : Find("yt-dlp.exe");
        }
    }

    /// <summary>Set when the bundled yt-dlp failed to start: the self-updated copy is used even if it is older.</summary>
    public static bool PreferUpdatedYtDlp { get; set; }

    public static string? Deno => Find("deno.exe");
    public static string? Ffmpeg => Find("ffmpeg.exe");

    /// <summary>Directory holding ffmpeg.exe (for yt-dlp --ffmpeg-location).</summary>
    public static string? FfmpegDir => Ffmpeg is { } f ? Path.GetDirectoryName(f) : null;

    /// <summary>Folder where the self-updater installs a newer onedir yt-dlp (tools\yt-dlp\ under LocalAppData).</summary>
    public static string UpdatedYtDlpDir => Path.Combine(AppPaths.UpdatedToolsDir, "yt-dlp");

    /// <summary>
    /// yt-dlp's version without starting it: the updater's sluplayer-version.txt next to the exe, else the version
    /// official builds stamp into the exe ("2026.10.26", the text --version prints). Null for other builds.
    /// </summary>
    public static string? YtDlpVersionOf(string exe)
    {
        try
        {
            var file = Path.Combine(Path.GetDirectoryName(exe) ?? "", "sluplayer-version.txt");
            if (File.Exists(file) && File.ReadAllText(file).Trim() is { Length: > 0 } v) return v;
            var stamp = FileVersionInfo.GetVersionInfo(exe).FileVersion?.Trim();
            return stamp != null && VersionStampRegex().IsMatch(stamp) ? stamp : null;
        }
        catch (Exception ex)
        {
            Log.Debug($"no version stamp in {exe}: {ex.Message}");
            return null;
        }
    }

    /// <summary>True when both versions are known and <paramref name="a"/> is the same or newer.</summary>
    private static bool IsNotOlder(string a, string b)
        => YtDlpVersionOf(a) is { } va && YtDlpVersionOf(b) is { } vb && YtDlpUpdater.CompareVersions(va, vb) >= 0;

    [GeneratedRegex(@"^\d{4}(\.\d+){2,3}$")]
    private static partial Regex VersionStampRegex();

    public static string? Find(string exe)
    {
        foreach (var dir in new[] { AppPaths.UpdatedToolsDir, AppPaths.BundledToolsDir, AppPaths.AppDir })
        {
            var p = Path.Combine(dir, exe);
            if (File.Exists(p)) return p;
        }
        // Fall back to PATH (developer machines).
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var p = Path.Combine(dir.Trim('"'), exe);
                if (File.Exists(p)) return p;
            }
            catch { }
        }
        return null;
    }
}

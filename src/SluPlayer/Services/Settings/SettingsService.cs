using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SluPlayer.Services.Settings;

/// <summary>Loads/saves <see cref="AppSettings"/> atomically. Corrupt files are backed up and replaced by defaults.</summary>
public static class SettingsService
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly object SaveLock = new();

    public static AppSettings Current { get; private set; } = new();

    public static AppSettings Load()
    {
        var path = AppPaths.SettingsFile;
        try
        {
            // Unknown properties (settings removed in later versions) are ignored.
            Current = File.Exists(path)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions) ?? new AppSettings()
                : new AppSettings();
        }
        catch (Exception ex)
        {
            Log.Error("settings.json is unreadable; using defaults", ex);
            try { File.Copy(path, path + ".bad", overwrite: true); } catch { }
            Current = new AppSettings();
        }
        Normalize(Current);
        return Current;
    }

    public static void Save()
    {
        lock (SaveLock)
        {
            try
            {
                WriteAtomic(AppPaths.SettingsFile, JsonSerializer.Serialize(Current, JsonOptions));
            }
            catch (Exception ex)
            {
                Log.Error("failed to save settings", ex);
            }
        }
    }

    /// <summary>Writes via temp file + replace so a crash never leaves a half-written file.</summary>
    /// <remarks>
    /// The temp file is flushed to disk before the replace: NTFS journals the rename but not the data, so after a
    /// power cut an unflushed file can come back full of NULs while the old one is already gone.
    /// </remarks>
    public static void WriteAtomic(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            using (var writer = new StreamWriter(fs, leaveOpen: true)) writer.Write(contents); // UTF-8 without BOM, like File.WriteAllText
            fs.Flush(flushToDisk: true); // FlushFileBuffers: a few ms for these small files
        }
        if (File.Exists(path))
            File.Replace(tmp, path, null, ignoreMetadataErrors: true);
        else
            File.Move(tmp, path);
    }

    private static void Normalize(AppSettings s)
    {
        s.Volume = Math.Clamp(s.Volume, 0, AppSettings.MaxVolume);
        s.SubtitleFontSize = Math.Clamp(s.SubtitleFontSize, 10, 120);
        if (string.IsNullOrWhiteSpace(s.DownloadDir)) s.DownloadDir = AppPaths.DefaultDownloadDir;
        if (string.IsNullOrWhiteSpace(s.AudioDownloadDir)) s.AudioDownloadDir = AppPaths.DefaultMusicDownloadDir;
        if (string.IsNullOrWhiteSpace(s.ScreenshotDir)) s.ScreenshotDir = AppPaths.DefaultScreenshotDir;
        if (string.IsNullOrWhiteSpace(s.Hwdec)) s.Hwdec = "auto-safe";
        if (s.LoopMode is not ("none" or "file" or "playlist")) s.LoopMode = "none";
    }
}

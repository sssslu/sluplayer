using System.IO;
using System.Text.Json;
using SluPlayer.Services.Settings;

namespace SluPlayer.Services;

public sealed class HistoryEntry
{
    public string Path { get; set; } = "";
    public string Title { get; set; } = "";
    public double Duration { get; set; }
    public double Position { get; set; }
    public DateTime LastPlayed { get; set; }
}

/// <summary>
/// Played items with their resume position (history.json). Thread-safe.
/// </summary>
public static class HistoryService
{
    private const int MaxEntries = 300;
    private static readonly object Gate = new();
    private static readonly object SaveGate = new();
    private static List<HistoryEntry> _entries = new();
    private static Timer? _saveTimer;

    public static void Load()
    {
        lock (Gate)
        {
            try
            {
                if (File.Exists(AppPaths.HistoryFile))
                    _entries = JsonSerializer.Deserialize<List<HistoryEntry>>(
                        File.ReadAllText(AppPaths.HistoryFile), SettingsService.JsonOptions) ?? new();
            }
            catch (Exception ex)
            {
                Log.Error("history.json unreadable", ex);
                // Keep it (like settings.json.bad): the next save replaces the file with the new, nearly empty list.
                try { File.Copy(AppPaths.HistoryFile, AppPaths.HistoryFile + ".bad", overwrite: true); } catch { }
                _entries = new();
            }
        }
    }

    public static IReadOnlyList<HistoryEntry> Snapshot()
    {
        lock (Gate) return _entries.OrderByDescending(e => e.LastPlayed).ToList();
    }

    public static HistoryEntry? Find(string path)
    {
        lock (Gate) return _entries.FirstOrDefault(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Adds or refreshes an entry (called when a file starts and periodically while playing).</summary>
    public static void Touch(string path, string? title, double duration, double position)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        lock (Gate)
        {
            var e = _entries.FirstOrDefault(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase));
            if (e == null)
            {
                e = new HistoryEntry { Path = path };
                _entries.Add(e);
            }
            if (!string.IsNullOrWhiteSpace(title)) e.Title = title!;
            if (string.IsNullOrWhiteSpace(e.Title)) e.Title = SafeFileName(path);
            if (duration > 0) e.Duration = duration;
            if (position >= 0) e.Position = position;
            e.LastPlayed = DateTime.Now;
            if (_entries.Count > MaxEntries)
                _entries = _entries.OrderByDescending(x => x.LastPlayed).Take(MaxEntries).ToList();
        }
        ScheduleSave();
    }

    public static void SaveNow()
    {
        // One save at a time (timer thread vs. app exit), so an older snapshot is never written over a newer one.
        lock (SaveGate)
        {
            List<HistoryEntry> copy;
            lock (Gate) copy = _entries.ToList();
            try { SettingsService.WriteAtomic(AppPaths.HistoryFile, JsonSerializer.Serialize(copy, SettingsService.JsonOptions)); }
            catch (Exception ex) { Log.Error("failed to save history", ex); }
        }
    }

    private static void ScheduleSave()
    {
        _saveTimer ??= new Timer(_ => SaveNow());
        _saveTimer.Change(1500, Timeout.Infinite);
    }

    private static string SafeFileName(string path)
    {
        try { return System.IO.Path.GetFileNameWithoutExtension(path); } catch { return path; }
    }
}

namespace SluPlayer.Core;

public static class Formatters
{
    /// <summary>1:02:03 or 2:03 (always at least m:ss).</summary>
    public static string Time(double seconds, bool forceHours = false)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) seconds = 0;
        var t = TimeSpan.FromSeconds(Math.Floor(seconds));
        return t.TotalHours >= 1 || forceHours
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes}:{t.Seconds:00}";
    }

    public static string Bytes(double bytes)
    {
        if (bytes <= 0 || double.IsNaN(bytes)) return "0 B";
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        int i = 0;
        while (bytes >= 1024 && i < units.Length - 1) { bytes /= 1024; i++; }
        return i == 0 ? $"{bytes:0} {units[i]}" : $"{bytes:0.0} {units[i]}";
    }

    public static string Speed(double bytesPerSecond) => bytesPerSecond > 0 ? Bytes(bytesPerSecond) + "/s" : "";
}

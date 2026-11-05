using System.Collections.Concurrent;
using System.IO;
using System.Text;

namespace SluPlayer.Services;

/// <summary>Tiny async file logger (%LOCALAPPDATA%\SluPlayer\logs\sluplayer.log, rotated at 4 MB).</summary>
public static class Log
{
    private static readonly BlockingCollection<string> Queue = new(new ConcurrentQueue<string>(), 10_000);
    private static readonly Thread Writer;
    private const long MaxBytes = 4 * 1024 * 1024;
    private const long ReopenRetryMs = 10_000;

    public static string FilePath => Path.Combine(AppPaths.LogDir, "sluplayer.log");

    static Log()
    {
        Writer = new Thread(WriteLoop) { IsBackground = true, Name = "log-writer", Priority = ThreadPriority.BelowNormal };
        Writer.Start();
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null)
        => Write("ERROR", ex == null ? message : $"{message}: {ex}");
    public static void Debug(string message) => Write("DEBUG", message);

    public static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
        Queue.TryAdd(line);
    }

    /// <summary>Blocks briefly until queued lines are written (used at shutdown / crash).</summary>
    public static void Flush(int timeoutMs = 1000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Queue.Count > 0 && Environment.TickCount64 < deadline)
            Thread.Sleep(10);
    }

    private static void WriteLoop()
    {
        StreamWriter? writer = null;
        long size = 0, reopenAt = long.MaxValue;
        try
        {
            Directory.CreateDirectory(AppPaths.LogDir);
            writer = Open(out size);
        }
        catch
        {
            // Logging is best-effort; drain the queue so producers never block.
        }

        foreach (var line in Queue.GetConsumingEnumerable())
        {
            try
            {
                if (writer == null)
                {
                    // The reopen after a rotation failed: retry now and then instead of going silent for the session
                    // (a failed first open stays off, as before).
                    if (Environment.TickCount64 < reopenAt) continue;
                    reopenAt = Environment.TickCount64 + ReopenRetryMs;
                    writer = Open(out size);
                }
                writer.WriteLine(line);
                size += Encoding.UTF8.GetByteCount(line) + 2;
                if (size > MaxBytes)
                {
                    // Also during long sessions (days of playback, or a damaged file that warns on every frame).
                    // Detach first: a Dispose that throws (disk full while flushing) has still closed the file, and
                    // must neither leave the dead writer in place nor skip the reopen.
                    var old = writer;
                    writer = null;
                    try { old.Dispose(); } catch { }
                    reopenAt = 0;
                    writer = Open(out size);
                    // Still too big: the move failed (another process has the file open). Try again after
                    // another MaxBytes instead of on every line.
                    if (size > MaxBytes) size = 0;
                }
                else if (Queue.Count == 0) writer.Flush();
            }
            catch { /* disk full etc. */ }
        }
    }

    /// <summary>Opens the log for appending (rotated first when it is over <see cref="MaxBytes"/>); returns its size.</summary>
    private static StreamWriter Open(out long size)
    {
        // Exclusive writer: a second process (e.g. a portable copy sharing the folder) gets its own file
        // instead of interleaving bytes into ours.
        FileStream fs;
        try
        {
            Rotate(FilePath);
            fs = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.Read);
        }
        catch (IOException)
        {
            var own = Path.Combine(AppPaths.LogDir, $"sluplayer-{Environment.ProcessId}.log");
            Rotate(own);
            fs = new FileStream(own, FileMode.Append, FileAccess.Write, FileShare.Read);
        }
        size = fs.Length;
        return new StreamWriter(fs, new UTF8Encoding(false)) { AutoFlush = false };
    }

    private static void Rotate(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (fi.Exists && fi.Length > MaxBytes) File.Move(path, path + ".1", overwrite: true);
        }
        catch { }
    }
}

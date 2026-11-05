namespace SluPlayer.Services;

/// <summary>
/// Hooks for app exit. Components with background work (e.g. downloads) register here; the main window asks
/// the user before closing and then runs the shutdown handlers. Register/unregister on the UI thread.
/// </summary>
public static class AppLifetime
{
    /// <summary>Each probe returns a short Korean description of unfinished work, or null when idle.</summary>
    public static readonly List<Func<string?>> PendingWorkProbes = new();

    /// <summary>Run once when the app is closing (stop child processes, flush state). Must be quick.</summary>
    public static readonly List<Action> ShutdownHandlers = new();

    public static IReadOnlyList<string> GetPendingWork()
    {
        var list = new List<string>();
        foreach (var probe in PendingWorkProbes.ToList())
        {
            try
            {
                var msg = probe();
                if (!string.IsNullOrWhiteSpace(msg)) list.Add(msg!);
            }
            catch (Exception ex) { Log.Warn("pending-work probe failed: " + ex.Message); }
        }
        return list;
    }

    public static void RunShutdownHandlers()
    {
        foreach (var handler in ShutdownHandlers.ToList())
        {
            try { handler(); }
            catch (Exception ex) { Log.Warn("shutdown handler failed: " + ex.Message); }
        }
    }
}

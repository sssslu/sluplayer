using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using SluPlayer.Services;
using SluPlayer.Services.Settings;
using SluPlayer.UI;

namespace SluPlayer;

public partial class App : Application
{
    private SingleInstance? _single;
    private int _errorDialogs;

    protected override void OnStartup(StartupEventArgs e)
    {
        AppPaths.EnsureCreated();
        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
        {
            Log.Error("fatal unhandled exception", ex.ExceptionObject as Exception);
            Log.Flush();
        };
        TaskScheduler.UnobservedTaskException += (_, ex) =>
        {
            Log.Error("unobserved task exception", ex.Exception);
            ex.SetObserved();
        };

        // Setup runs "SluPlayer.exe --register-file-types" after installing: write the associations and exit (no window).
        if (e.Args.Contains("--register-file-types", StringComparer.OrdinalIgnoreCase))
        {
            int code = 0;
            try { FileAssociations.ForCurrentUser().Register(); }
            catch (Exception ex) { Log.Error("file type registration failed", ex); code = 1; }
            Shutdown(code);
            return;
        }

        // Restart: wait for the previous process to release the single-instance mutex.
        var restartArg = e.Args.FirstOrDefault(a => a.StartsWith("--restart-wait=", StringComparison.OrdinalIgnoreCase));
        if (restartArg != null && int.TryParse(restartArg["--restart-wait=".Length..], out var oldPid))
        {
            try { System.Diagnostics.Process.GetProcessById(oldPid).WaitForExit(15000); } catch { }
        }

        var settings = SettingsService.Load();

        _single = new SingleInstance();
        if (!_single.TryAcquire())
        {
            // The running instance has its own working directory: send absolute paths.
            if (_single.Forward(e.Args.Select(AbsoluteArg).ToArray()))
            {
                // Forwarded to the running instance; exit without touching the log/settings.
                _forwarded = true;
                Shutdown();
                return;
            }
            _single.Dispose();
            _single = null;
        }
        else
        {
            _single.StartServer();
        }
        Log.Info($"SluPlayer {typeof(App).Assembly.GetName().Version} starting; args: {string.Join(" | ", e.Args)}");

        HistoryService.Load();
        base.OnStartup(e);

        var window = new MainWindow(settings, e.Args);
        MainWindow = window;
        // Delivers anything forwarded while the window was being built, then every later forward.
        _single?.SetHandler(args => Dispatcher.BeginInvoke(() => window.HandleExternalArgs(args)));
        window.Closed += (_, _) => Shutdown();
        // --noactivate (scripts/tests): appear without stealing keyboard focus from the user's current app.
        // (A remembered maximized state is not applied then: MainWindow keeps it for the next normal start.)
        bool noActivate = e.Args.Contains("--noactivate", StringComparer.OrdinalIgnoreCase);
        if (noActivate) window.ShowActivated = false;
        try
        {
            window.Show();
        }
        catch (Exception ex)
        {
            // A process without a window must not keep the single-instance mutex: every later launch would be
            // forwarded to it and disappear.
            Log.Error("main window could not be shown", ex);
            if (!noActivate)
                MessageBox.Show("시작 실패\n\n" + ex.Message + "\n\n로그: " + Log.FilePath,
                    "SluPlayer", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _single?.Dispose();
        if (!_forwarded)
        {
            Log.Info("exit");
            Log.Flush();
        }
        base.OnExit(e);
    }

    private bool _forwarded;

    private static string AbsoluteArg(string arg)
    {
        if (string.IsNullOrWhiteSpace(arg) || arg.StartsWith("--", StringComparison.Ordinal))
            return arg;
        try
        {
            var trimmed = arg.Trim().Trim('"');
            // Any URI (http, file:///, ftp, dvd, …) is sent as is: the running instance and mpv resolve it, while
            // Path.GetFullPath would turn it into "<cwd>\file:\…". A path never has a scheme before "://".
            if (UriScheme().IsMatch(trimmed)) return trimmed;
            return Path.IsPathFullyQualified(trimmed) ? trimmed : Path.GetFullPath(trimmed);
        }
        catch { return arg; }
    }

    /// <summary>"scheme://" as mpv recognises URLs (RFC 3986 scheme; one letter would be a drive).</summary>
    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9+.\-]+://")]
    private static partial Regex UriScheme();

    /// <summary>
    /// Windows is logging off or shutting down: the process may be killed before the window's normal close
    /// sequence runs, so persist settings, window bounds and the playback position right away.
    /// </summary>
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        try { (MainWindow as MainWindow)?.SaveStateNow(); }
        catch (Exception ex) { Log.Error("session-end save failed", ex); }
        Log.Info("session ending: " + e.ReasonSessionEnding);
        Log.Flush();
        base.OnSessionEnding(e);
    }

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("UI exception", e.Exception);
        e.Handled = true;
        // Avoid stacking dialogs if something fails repeatedly (e.g. in a render loop).
        if (Interlocked.Increment(ref _errorDialogs) > 3) return;
        MessageBox.Show(
            "오류\n\n" + e.Exception.Message + "\n\n로그: " + Log.FilePath,
            "SluPlayer", MessageBoxButton.OK, MessageBoxImage.Warning);
        Interlocked.Decrement(ref _errorDialogs);
    }
}

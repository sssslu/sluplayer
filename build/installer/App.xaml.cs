using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;

namespace SluPlayer.Setup
{
    /// <summary>
    /// Entry point. The same program is the installer (with the release zip appended) and the uninstaller
    /// (the copy without payload that is installed as Uninstall.exe).
    /// </summary>
    public partial class App : Application
    {
        private const int StayOpen = int.MinValue;
        private Mutex _mutex;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            int code;
            try { code = Run(e.Args); }
            catch (Exception ex)
            {
                Log.Write("fatal: " + ex);
                code = 1;
            }
            if (code != StayOpen) Shutdown(code);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try { _mutex?.ReleaseMutex(); } catch { }
            _mutex?.Dispose();
            base.OnExit(e);
        }

        private int Run(string[] args)
        {
            SetupOptions o;
            try { o = SetupOptions.Parse(args); }
            catch (ArgumentException ex)
            {
                Log.Write("bad arguments: " + string.Join(" ", args) + " - " + ex.Message);
                if (!args.Any(a => a.Trim().TrimStart('/', '-').Equals("S", StringComparison.OrdinalIgnoreCase)))
                    Message(ex.Message + "\n\n사용법: SluPlayer-Setup.exe [/S] [/D=설치 폴더] [/Desktop | /NoDesktop] [/NoLaunch]", MessageBoxImage.Warning);
                return 2;
            }
            Log.Write($"start {Installer.Version}: {string.Join(" ", args)}");

            // The uninstaller's temp copy first waits for Uninstall.exe to exit (it holds the mutex and its own file).
            if (o.ParentPid > 0)
            {
                try { using (var parent = Process.GetProcessById(o.ParentPid)) parent.WaitForExit(15000); } catch { }
            }

            if (!Environment.Is64BitOperatingSystem) return Fail(o, "64비트 Windows 필요", 5);

            _mutex = new Mutex(false, @"Local\SluPlayer.Setup");
            bool owned;
            try { owned = _mutex.WaitOne(0); } catch (AbandonedMutexException) { owned = true; }
            if (!owned)
            {
                _mutex.Dispose();
                _mutex = null;
                return Fail(o, "설치 프로그램이 이미 실행 중", 1);
            }

            var exe = Assembly.GetEntryAssembly().Location;
            Payload payload;
            try { payload = Payload.TryOpen(exe); }
            catch (Exception ex) when (ex is InvalidDataException || ex is IOException)
            {
                return Fail(o, ex is InvalidDataException ? ex.Message : "설치 파일 읽기 실패: " + ex.Message, 1);
            }

            if (payload == null || o.Uninstall)
            {
                payload?.Dispose();
                return RunUninstall(o, exe);
            }
            return RunInstall(o, payload);
        }

        // ---------------------------------------------------------------------------------------------

        private int RunInstall(SetupOptions o, Payload payload)
        {
            var existing = Installer.FindExisting(o.RegKey);
            if (o.Silent)
            {
                try
                {
                    var dir = Installer.ResolveInstallDir(o.InstallDir ?? existing?.Location ?? Installer.DefaultInstallDir);
                    bool desktop = o.Desktop ?? HadDesktopShortcut(dir, o);
                    Installer.EnsureAppClosed(dir, force: true);
                    Installer.Install(o, dir, desktop, payload, null);
                    if (o.LaunchExplicit && o.Launch) Launch(Path.Combine(dir, Installer.AppExe));
                    return 0;
                }
                catch (SetupException ex) { Log.Write("silent install failed: " + ex); return ex.ExitCode; }
                catch (Exception ex) { Log.Write("silent install failed: " + ex); return 1; }
                finally { payload.Dispose(); }
            }

            var window = new SetupWindow(o, payload, existing);
            window.Closed += (s, e) =>
            {
                payload.Dispose();
                if (window.LaunchAfterClose != null) Launch(window.LaunchAfterClose);
                Shutdown(window.ExitCode);
            };
            ShowWindow(window, o);
            return StayOpen;
        }

        private static void ShowWindow(Window window, SetupOptions o)
        {
            if (o.NoActivate) window.ShowActivated = false;
            if (o.WindowPos != null)
            {
                var xy = o.WindowPos.Split(',');
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = double.Parse(xy[0], System.Globalization.CultureInfo.InvariantCulture);
                window.Top = double.Parse(xy[1], System.Globalization.CultureInfo.InvariantCulture);
            }
            window.Show();
        }

        private static bool HadDesktopShortcut(string dir, SetupOptions o)
        {
            try
            {
                var m = Manifest.Load(dir);
                return m != null && m.Shortcuts.Any(s => s.StartsWith(o.DesktopDir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));
            }
            catch { return false; }
        }

        private int RunUninstall(SetupOptions o, string exe)
        {
            if (o.Target == null)
            {
                // Started as <install folder>\Uninstall.exe (or "setup /uninstall"): find the install folder ...
                var dir = Path.GetDirectoryName(exe);
                if (Manifest.Load(dir) == null)
                {
                    var existing = Installer.FindExisting(o.RegKey);
                    if (existing == null || Manifest.Load(existing.Location) == null)
                        return Fail(o, "설치된 SluPlayer 없음", 1);
                    dir = existing.Location;
                }
                // ... and continue from a temp copy, so that this folder (including this exe) can be deleted.
                var tmp = Path.Combine(Path.GetTempPath(), "SluPlayer-Uninstall-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".exe");
                File.Copy(exe, tmp);
                var childArgs = $"/uninstall \"/target={dir}\" /parent={Process.GetCurrentProcess().Id}"
                                + (o.Silent ? " /S" : "") + (o.RemoveData ? " /RemoveData" : "")
                                + (o.NoActivate ? " /NoActivate" : "") + (o.WindowPos != null ? " /WindowPos=" + o.WindowPos : "");
                _mutex.ReleaseMutex();
                _mutex.Dispose();
                _mutex = null;
                Process.Start(new ProcessStartInfo(tmp, childArgs) { UseShellExecute = false, WorkingDirectory = Path.GetTempPath() })?.Dispose();
                Log.Write("continuing uninstall from " + tmp);
                return 0;
            }

            var manifest = Manifest.Load(o.Target);
            if (manifest == null)
            {
                ScheduleSelfDelete(exe);
                return Fail(o, "설치 정보 없음: " + o.Target, 1);
            }
            if (o.Silent)
            {
                try
                {
                    Installer.EnsureAppClosed(o.Target, force: true);
                    var left = Installer.Uninstall(o.Target, manifest, o.RemoveData, null);
                    return left.Count == 0 ? 0 : 6;
                }
                catch (SetupException ex) { Log.Write("silent uninstall failed: " + ex); return ex.ExitCode; }
                catch (Exception ex) { Log.Write("silent uninstall failed: " + ex); return 1; }
                finally { ScheduleSelfDelete(exe); }
            }

            var window = new SetupWindow(o, o.Target, manifest);
            window.Closed += (s, e) =>
            {
                ScheduleSelfDelete(exe);
                Shutdown(window.ExitCode);
            };
            ShowWindow(window, o);
            return StayOpen;
        }

        // ---------------------------------------------------------------------------------------------

        private static void Launch(string appExe)
        {
            try
            {
                Process.Start(new ProcessStartInfo(appExe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(appExe) })?.Dispose();
            }
            catch (Exception ex) { Log.Write("launch failed: " + ex.Message); }
        }

        /// <summary>Deletes the temp copy of the uninstaller once this process has exited.</summary>
        private static void ScheduleSelfDelete(string exe)
        {
            var name = Path.GetFileName(exe);
            if (!name.StartsWith("SluPlayer-Uninstall-", StringComparison.OrdinalIgnoreCase)) return;
            if (!exe.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase)) return;
            try
            {
                Process.Start(new ProcessStartInfo("cmd.exe", $"/d /c ping -n 3 127.0.0.1 >nul & del /f /q \"{exe}\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = Path.GetTempPath(),
                })?.Dispose();
            }
            catch (Exception ex) { Log.Write("self-delete failed: " + ex.Message); }
        }

        private static int Fail(SetupOptions o, string message, int code)
        {
            Log.Write("error: " + message);
            if (!o.Silent) Message(message, MessageBoxImage.Error);
            return code;
        }

        private static void Message(string text, MessageBoxImage icon)
            => MessageBox.Show(text, "SluPlayer 설치", MessageBoxButton.OK, icon);
    }
}

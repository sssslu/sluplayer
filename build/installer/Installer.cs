using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace SluPlayer.Setup
{
    /// <summary>A failure with a message that can be shown to the user as is (Korean).</summary>
    internal class SetupException : Exception
    {
        public int ExitCode { get; }
        public SetupException(string message, int exitCode = 1, Exception inner = null) : base(message, inner) { ExitCode = exitCode; }
    }

    internal sealed class AppRunningException : SetupException
    {
        public AppRunningException() : base("SluPlayer 실행 중. 닫은 뒤 다시 시도", 3) { }
    }

    internal struct SetupProgress
    {
        public double Fraction;
        public string Text;
        public SetupProgress(double fraction, string text) { Fraction = fraction; Text = text; }
    }

    /// <summary>What a previous install left in the registry.</summary>
    internal sealed class ExistingInstall
    {
        public string Location;
        public string Version;
    }

    /// <summary>
    /// install.manifest in the install folder: everything the installer created, so updates can remove stale
    /// files and the uninstaller removes exactly these (never anything else in the folder).
    /// </summary>
    internal sealed class Manifest
    {
        public const string FileName = "install.manifest";
        public string Version = "";
        public string RegKey = SetupOptions.DefaultRegKey;
        public List<string> Shortcuts = new List<string>();
        public List<string> DataDirs = new List<string>();
        public List<string> Files = new List<string>();

        public static Manifest Load(string dir)
        {
            var path = Path.Combine(dir, FileName);
            if (!File.Exists(path)) return null;
            var m = new Manifest();
            foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
            {
                int eq = line.IndexOf('=');
                if (line.StartsWith("#") || eq <= 0) continue;
                var key = line.Substring(0, eq);
                var value = line.Substring(eq + 1);
                switch (key)
                {
                    case "version": m.Version = value; break;
                    case "regkey": m.RegKey = value; break;
                    case "shortcut": m.Shortcuts.Add(value); break;
                    case "datadir": m.DataDirs.Add(value); break;
                    case "file": m.Files.Add(value); break;
                }
            }
            return m;
        }

        public void Save(string dir)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# SluPlayer install manifest - written by the setup program, used by Uninstall.exe");
            sb.AppendLine("version=" + Version);
            sb.AppendLine("regkey=" + RegKey);
            foreach (var s in Shortcuts) sb.AppendLine("shortcut=" + s);
            foreach (var d in DataDirs) sb.AppendLine("datadir=" + d);
            foreach (var f in Files) sb.AppendLine("file=" + f);
            var path = Path.Combine(dir, FileName);
            File.WriteAllText(path + ".new", sb.ToString(), new UTF8Encoding(false));
            ReplaceFile(path + ".new", path);
        }

        internal static void ReplaceFile(string source, string target)
        {
            if (File.Exists(target)) File.Replace(source, target, null, ignoreMetadataErrors: true);
            else File.Move(source, target);
        }
    }

    internal static class Installer
    {
        public const string AppExe = "SluPlayer.exe";
        public const string UninstallExe = "Uninstall.exe";
        public const string ShortcutName = "SluPlayer.lnk";

        public static string Version
        {
            get
            {
                var v = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                        ?? Assembly.GetExecutingAssembly().GetName().Version.ToString(3);
                int plus = v.IndexOf('+');
                return plus > 0 ? v.Substring(0, plus) : v;
            }
        }

        public static string DefaultInstallDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "SluPlayer");

        public static ExistingInstall FindExisting(string regKey)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(regKey))
                {
                    var loc = key?.GetValue("InstallLocation") as string;
                    if (string.IsNullOrWhiteSpace(loc)) return null;
                    return new ExistingInstall { Location = loc.TrimEnd('\\'), Version = key.GetValue("DisplayVersion") as string ?? "" };
                }
            }
            catch (Exception ex)
            {
                Log.Write("registry read failed: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// The folder to install into. A non-empty folder is only accepted when it already holds SluPlayer;
        /// otherwise "SluPlayer" is appended (the user picked a parent folder such as D:\Apps).
        /// </summary>
        public static string ResolveInstallDir(string requested)
        {
            string dir;
            try { dir = Path.GetFullPath(Environment.ExpandEnvironmentVariables(requested.Trim().Trim('"'))).TrimEnd('\\'); }
            catch (Exception ex) { throw new SetupException("잘못된 설치 위치", 2, ex); }
            if (dir.Length <= 3 || !Path.IsPathRooted(dir)) throw new SetupException("드라이브 루트에는 설치 불가. 폴더 선택 필요", 2);
            if (IsUsable(dir)) return dir;
            var sub = Path.Combine(dir, "SluPlayer");
            if (IsUsable(sub)) return sub;
            throw new SetupException("이 폴더에는 설치 불가. 빈 폴더 선택 필요", 2);
        }

        private static bool IsUsable(string dir)
        {
            if (File.Exists(dir)) return false;
            if (!Directory.Exists(dir)) return true;
            return File.Exists(Path.Combine(dir, Manifest.FileName)) || File.Exists(Path.Combine(dir, AppExe))
                   || !Directory.EnumerateFileSystemEntries(dir).Any();
        }

        // ---------------------------------------------------------------------------------------------
        // Running app
        // ---------------------------------------------------------------------------------------------

        private static List<Process> FindAppProcesses(string installDir)
        {
            var prefix = installDir.TrimEnd('\\') + "\\";
            var list = new List<Process>();
            foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(AppExe)))
            {
                var path = NativeMethods.GetProcessPath(p.Id);
                if (path != null && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) list.Add(p);
                else p.Dispose();
            }
            return list;
        }

        /// <summary>
        /// Closes SluPlayer instances started from the install folder (WM_CLOSE: the app saves its state).
        /// With <paramref name="force"/> (silent mode) a process that does not close in time is terminated.
        /// </summary>
        public static void EnsureAppClosed(string installDir, bool force)
        {
            var procs = FindAppProcesses(installDir);
            if (procs.Count == 0) return;
            Log.Write($"closing {procs.Count} running SluPlayer process(es)");
            foreach (var p in procs) { try { p.CloseMainWindow(); } catch { } }
            var deadline = DateTime.UtcNow.AddSeconds(10);
            foreach (var p in procs)
            {
                var left = (int)Math.Max(0, (deadline - DateTime.UtcNow).TotalMilliseconds);
                try { p.WaitForExit(left); } catch { }
            }
            var still = procs.Where(p => { try { return !p.HasExited; } catch { return false; } }).ToList();
            if (still.Count > 0 && force)
            {
                foreach (var p in still) { try { p.Kill(); p.WaitForExit(5000); } catch { } }
                still = still.Where(p => { try { return !p.HasExited; } catch { return false; } }).ToList();
            }
            foreach (var p in procs) p.Dispose();
            if (still.Count > 0) throw new AppRunningException();
        }

        // ---------------------------------------------------------------------------------------------
        // Install
        // ---------------------------------------------------------------------------------------------

        private sealed class PayloadFile
        {
            public ZipArchiveEntry Entry;
            public string Relative;   // e.g. tools\ffmpeg.exe
        }

        private static List<PayloadFile> ListFiles(ZipArchive zip, string installDir)
        {
            var prefix = installDir.TrimEnd('\\') + "\\";
            var files = new List<PayloadFile>();
            foreach (var e in zip.Entries)
            {
                if (string.IsNullOrEmpty(e.Name)) continue;                    // directory entry
                var name = e.FullName.Replace('\\', '/');
                int slash = name.IndexOf('/');
                if (slash <= 0) throw new SetupException("설치 파일 손상. 다시 다운로드 필요");
                var rel = name.Substring(slash + 1).Replace('/', '\\');           // drop the top "SluPlayer/" folder
                var full = Path.GetFullPath(Path.Combine(installDir, rel));
                if (rel.Length == 0 || Path.IsPathRooted(rel) || !full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    throw new SetupException("설치 파일 손상. 다시 다운로드 필요");
                files.Add(new PayloadFile { Entry = e, Relative = rel });
            }
            if (!files.Any(f => f.Relative.Equals(AppExe, StringComparison.OrdinalIgnoreCase)))
                throw new SetupException("설치 파일 손상. 다시 다운로드 필요");
            return files;
        }

        public static long PayloadSize(Payload payload)
        {
            using (var zip = payload.OpenZip())
                return zip.Entries.Sum(e => e.Length);
        }

        public static void Install(SetupOptions o, string installDir, bool desktopShortcut, Payload payload, IProgress<SetupProgress> progress)
        {
            Log.Write($"install {Version} into {installDir} (desktop={desktopShortcut}, regkey={o.RegKey})");
            progress?.Report(new SetupProgress(0, "준비 중…"));
            try { Directory.CreateDirectory(installDir); }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException)
            {
                throw new SetupException("이 폴더에 쓰기 권한 없음. 다른 위치 선택 필요", 1, ex);
            }

            var old = Manifest.Load(installDir);
            using (var zip = payload.OpenZip())
            {
                var files = ListFiles(zip, installDir);
                long total = files.Sum(f => f.Entry.Length);

                // Free space: what we write minus what we overwrite, plus some room.
                long existing = 0;
                foreach (var f in files)
                {
                    var t = Path.Combine(installDir, f.Relative);
                    if (File.Exists(t)) existing += new FileInfo(t).Length;
                }
                long need = Math.Max(0, total - existing) + 64L * 1024 * 1024;
                try
                {
                    var free = new DriveInfo(Path.GetPathRoot(installDir)).AvailableFreeSpace;
                    if (free < need)
                        throw new SetupException($"저장 공간 부족: {(need - free) / (1024 * 1024) + 1:N0} MB 더 필요", 4);
                }
                catch (ArgumentException) { /* network path etc.: let the copy fail if it must */ }

                long done = 0;
                var buffer = new byte[1 << 20];
                int index = 0;
                foreach (var f in files)
                {
                    index++;
                    var target = Path.Combine(installDir, f.Relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    var tmp = target + ".new";
                    using (var src = f.Entry.Open())
                    using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                    {
                        int n;
                        while ((n = src.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            dst.Write(buffer, 0, n);
                            done += n;
                            progress?.Report(new SetupProgress(0.95 * done / Math.Max(1, total), $"파일 복사 중… ({index}/{files.Count})"));
                        }
                    }
                    File.SetLastWriteTime(tmp, f.Entry.LastWriteTime.LocalDateTime);
                    try { Manifest.ReplaceFile(tmp, target); }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        TryDelete(tmp);
                        throw new SetupException($"파일 교체 실패: {f.Relative}\nSluPlayer 실행 중이면 닫고 다시 시도", 1, ex);
                    }
                }

                progress?.Report(new SetupProgress(0.96, "마무리 중…"));
                payload.WriteProgramOnly(Path.Combine(installDir, UninstallExe + ".new"));
                Manifest.ReplaceFile(Path.Combine(installDir, UninstallExe + ".new"), Path.Combine(installDir, UninstallExe));

                var manifest = new Manifest
                {
                    Version = Version,
                    RegKey = o.RegKey,
                    DataDirs = o.DataDirs.ToList(),
                    Files = files.Select(f => f.Relative).Concat(new[] { UninstallExe, Manifest.FileName }).ToList(),
                };

                // Shortcuts
                var startMenu = Path.Combine(o.StartMenuDir, ShortcutName);
                var exe = Path.Combine(installDir, AppExe);
                Directory.CreateDirectory(o.StartMenuDir);
                NativeMethods.CreateShortcut(startMenu, exe, installDir, "동영상 플레이어 · 다운로더");
                manifest.Shortcuts.Add(startMenu);
                if (desktopShortcut)
                {
                    var desktop = Path.Combine(o.DesktopDir, ShortcutName);
                    Directory.CreateDirectory(o.DesktopDir);
                    NativeMethods.CreateShortcut(desktop, exe, installDir, "동영상 플레이어 · 다운로더");
                    manifest.Shortcuts.Add(desktop);
                }

                // Update: remove what the previous version installed but this one doesn't.
                if (old != null)
                {
                    var keep = new HashSet<string>(manifest.Files, StringComparer.OrdinalIgnoreCase);
                    foreach (var rel in old.Files.Where(r => !keep.Contains(r)))
                    {
                        var path = SafeCombine(installDir, rel);
                        if (path != null) TryDelete(path);
                    }
                    var keepLinks = new HashSet<string>(manifest.Shortcuts, StringComparer.OrdinalIgnoreCase);
                    foreach (var lnk in old.Shortcuts.Where(s => !keepLinks.Contains(s))) TryDeleteShortcut(lnk);
                    RemoveEmptyDirectories(installDir, removeRoot: false);
                }

                manifest.Save(installDir);
                WriteUninstallEntry(o.RegKey, installDir, total);
                // Test runs (/RegKey=…) never touch the real file associations.
                if (string.Equals(o.RegKey, SetupOptions.DefaultRegKey, StringComparison.OrdinalIgnoreCase))
                {
                    progress?.Report(new SetupProgress(0.98, "파일 연결 등록 중…"));
                    RegisterFileTypes(exe);
                }
            }
            progress?.Report(new SetupProgress(1, "설치 완료"));
            Log.Write("install finished");
        }

        private static void WriteUninstallEntry(string regKey, string installDir, long totalBytes)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(regKey))
            {
                var uninstall = Path.Combine(installDir, UninstallExe);
                key.SetValue("DisplayName", "SluPlayer");
                key.SetValue("DisplayVersion", Version);
                key.SetValue("Publisher", "slu");
                key.SetValue("DisplayIcon", Path.Combine(installDir, AppExe) + ",0");
                key.SetValue("InstallLocation", installDir);
                key.SetValue("UninstallString", "\"" + uninstall + "\"");
                key.SetValue("QuietUninstallString", "\"" + uninstall + "\" /S");
                key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
                key.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, totalBytes / 1024), RegistryValueKind.DWord);
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }
        }

        /// <summary>
        /// "SluPlayer.exe --register-file-types": the app writes its HKCU associations (ProgIDs, "Open with",
        /// Default apps entry) for the installed exe. Windows lets only the user pick the default app.
        /// </summary>
        private static void RegisterFileTypes(string exe)
        {
            try
            {
                using (var p = Process.Start(new ProcessStartInfo(exe, "--register-file-types")
                       { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(exe) }))
                {
                    if (p == null) return;
                    if (!p.WaitForExit(30000)) { Log.Write("file type registration timed out"); return; }
                    Log.Write("file type registration exit code " + p.ExitCode);
                }
            }
            catch (Exception ex) { Log.Write("file type registration failed: " + ex.Message); }
        }

        // ---------------------------------------------------------------------------------------------
        // Uninstall
        // ---------------------------------------------------------------------------------------------

        /// <summary>Removes exactly what the manifest lists. Returns paths that could not be removed.</summary>
        public static List<string> Uninstall(string installDir, Manifest manifest, bool removeData, IProgress<SetupProgress> progress)
        {
            Log.Write($"uninstall from {installDir} (removeData={removeData})");
            var leftovers = new List<string>();
            int total = manifest.Files.Count, index = 0;
            foreach (var rel in manifest.Files)
            {
                index++;
                progress?.Report(new SetupProgress(0.9 * index / Math.Max(1, total), "파일 삭제 중…"));
                var path = SafeCombine(installDir, rel);
                if (path == null || !File.Exists(path)) continue;
                if (!TryDelete(path)) leftovers.Add(path);
            }
            foreach (var lnk in manifest.Shortcuts) TryDeleteShortcut(lnk);

            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(manifest.RegKey))
                {
                    var loc = key?.GetValue("InstallLocation") as string;
                    if (loc != null && !loc.TrimEnd('\\').Equals(installDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                        Log.Write("registry entry belongs to another location; kept: " + loc);
                    else if (key != null)
                    {
                        key.Dispose();
                        Registry.CurrentUser.DeleteSubKeyTree(manifest.RegKey, throwOnMissingSubKey: false);
                    }
                }
            }
            catch (Exception ex) { Log.Write("registry cleanup failed: " + ex.Message); }
            // Test runs (/RegKey=…) never touch the real file associations.
            if (string.Equals(manifest.RegKey, SetupOptions.DefaultRegKey, StringComparison.OrdinalIgnoreCase))
                RemoveFileAssociations(installDir);

            RemoveEmptyDirectories(installDir, removeRoot: true);
            if (Directory.Exists(installDir) && Directory.EnumerateFileSystemEntries(installDir).Any())
                Log.Write("install folder kept (not empty): " + installDir);

            if (removeData)
            {
                progress?.Report(new SetupProgress(0.95, "설정과 기록 삭제 중…"));
                foreach (var d in manifest.DataDirs)
                {
                    // Only ever "...\SluPlayer" folders: a damaged manifest must not be able to delete anything else.
                    if (string.IsNullOrWhiteSpace(d) || !Path.IsPathRooted(d) ||
                        !string.Equals(Path.GetFileName(d.TrimEnd('\\')), "SluPlayer", StringComparison.OrdinalIgnoreCase))
                    {
                        Log.Write("skipped suspicious data folder: " + d);
                        continue;
                    }
                    if (!Directory.Exists(d)) continue;
                    try { Directory.Delete(d, recursive: true); }
                    catch (Exception ex) { Log.Write($"could not remove {d}: {ex.Message}"); leftovers.Add(d); }
                }
            }
            progress?.Report(new SetupProgress(1, "제거 완료"));
            Log.Write("uninstall finished; leftovers: " + leftovers.Count);
            return leftovers;
        }

        // ---------------------------------------------------------------------------------------------
        // Helpers
        // ---------------------------------------------------------------------------------------------

        /// <summary>installDir + relative path, or null if the result would leave installDir.</summary>
        private static string SafeCombine(string installDir, string rel)
        {
            if (string.IsNullOrWhiteSpace(rel) || Path.IsPathRooted(rel)) return null;
            var full = Path.GetFullPath(Path.Combine(installDir, rel));
            return full.StartsWith(installDir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) ? full : null;
        }

        /// <summary>
        /// Removes the file associations (written at install or by the app's "파일 연결" button; HKCU only), if they point into this install folder:
        /// the ProgIDs, the Applications entry, the OpenWithProgids values, Capabilities/RegisteredApplications and the
        /// folder menu entry of earlier versions.
        /// </summary>
        private static void RemoveFileAssociations(string installDir)
        {
            const string classesPath = @"Software\Classes";
            string[] progIds = { "SluPlayer.Video", "SluPlayer.Audio" };
            try
            {
                using (var cmd = Registry.CurrentUser.OpenSubKey(classesPath + @"\SluPlayer.Video\shell\open\command"))
                {
                    var command = cmd?.GetValue("") as string;
                    if (command != null && command.IndexOf(installDir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        Log.Write("file associations belong to another location; kept: " + command);
                        return;
                    }
                }
                using (var classes = Registry.CurrentUser.OpenSubKey(classesPath, writable: true))
                {
                    if (classes == null) return;
                    foreach (var id in progIds) classes.DeleteSubKeyTree(id, throwOnMissingSubKey: false);
                    // Windows also creates this key itself ("Open with" › browse), maybe for another copy: only ours.
                    bool ownApp;
                    using (var appCmd = classes.OpenSubKey(@"Applications\SluPlayer.exe\shell\open\command"))
                        ownApp = appCmd?.GetValue("") is string c && c.IndexOf(installDir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (ownApp) classes.DeleteSubKeyTree(@"Applications\SluPlayer.exe", throwOnMissingSubKey: false);
                    classes.DeleteSubKeyTree(@"Directory\shell\SluPlayer", throwOnMissingSubKey: false);
                    foreach (var name in classes.GetSubKeyNames())
                    {
                        if (!name.StartsWith(".", StringComparison.Ordinal)) continue;
                        using (var openWith = classes.OpenSubKey(name + @"\OpenWithProgids", writable: true))
                        {
                            if (openWith == null) continue;
                            foreach (var id in progIds) openWith.DeleteValue(id, throwOnMissingValue: false);
                        }
                    }
                }
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\SluPlayer\Capabilities", throwOnMissingSubKey: false);
                using (var app = Registry.CurrentUser.OpenSubKey(@"Software\SluPlayer"))
                {
                    if (app != null && app.SubKeyCount == 0 && app.ValueCount == 0)
                    {
                        app.Dispose();
                        Registry.CurrentUser.DeleteSubKey(@"Software\SluPlayer", throwOnMissingSubKey: false);
                    }
                }
                using (var reg = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications", writable: true))
                    reg?.DeleteValue("SluPlayer", throwOnMissingValue: false);
                NativeMethods.NotifyAssociationsChanged(); // Explorer drops stale "Open with" entries and icons
                Log.Write("file associations removed");
            }
            catch (Exception ex) { Log.Write("file association cleanup failed: " + ex.Message); }
        }

        private static bool TryDelete(string path)
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    if (!File.Exists(path)) return true;
                    File.SetAttributes(path, FileAttributes.Normal);
                    File.Delete(path);
                    return true;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    Thread.Sleep(200);
                }
            }
            Log.Write("could not delete " + path);
            return false;
        }

        private static void TryDeleteShortcut(string lnk)
        {
            if (string.IsNullOrWhiteSpace(lnk) || !lnk.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return;
            TryDelete(lnk);
        }

        private static void RemoveEmptyDirectories(string root, bool removeRoot)
        {
            if (!Directory.Exists(root)) return;
            foreach (var dir in Directory.GetDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
            {
                try { if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); } catch { }
            }
            if (removeRoot)
            {
                try { if (!Directory.EnumerateFileSystemEntries(root).Any()) Directory.Delete(root); } catch { }
            }
        }
    }

    internal static class Log
    {
        private static readonly object Gate = new object();
        public static readonly string FilePath = Path.Combine(Path.GetTempPath(), "SluPlayer-Setup.log");

        public static void Write(string message)
        {
            try
            {
                lock (Gate)
                    File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{Process.GetCurrentProcess().Id}] {message}\r\n", Encoding.UTF8);
            }
            catch { /* logging is best effort */ }
        }
    }
}

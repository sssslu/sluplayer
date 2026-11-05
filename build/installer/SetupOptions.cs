using System;
using System.Collections.Generic;
using System.IO;

namespace SluPlayer.Setup
{
    /// <summary>
    /// Command line. Install: SluPlayer-Setup.exe [/S] [/D=dir] [/Desktop|/NoDesktop] [/NoLaunch] [/Launch]
    /// Uninstall: Uninstall.exe [/S] [/RemoveData]
    /// Test hooks (keep tests away from the real Start menu, desktop, registry and user data):
    ///   /RegKey=Software\...  /StartMenuDir=dir  /DesktopDir=dir  /DataDirs=dir1|dir2
    ///   /NoActivate /WindowPos=x,y   (show the window without taking focus, at a fixed place)
    /// </summary>
    internal sealed class SetupOptions
    {
        public const string DefaultRegKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\SluPlayer";

        public bool Silent;
        public bool Uninstall;
        public string InstallDir;          // null = default / previous install
        public bool? Desktop;              // null = default (UI: on, silent: off)
        public bool Launch = true;         // UI only; silent installs launch only with /Launch
        public bool LaunchExplicit;
        public bool RemoveData;
        public string RegKey = DefaultRegKey;
        public string StartMenuDir = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        public string DesktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        public List<string> DataDirs = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SluPlayer"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SluPlayer"),
        };
        public bool DataDirsExplicit;
        public bool RegKeyExplicit;
        public bool StartMenuExplicit;
        public bool DesktopDirExplicit;
        /// <summary>Uninstaller running from its temp copy: the install folder to remove.</summary>
        public string Target;
        /// <summary>Uninstaller running from its temp copy: the Uninstall.exe process to wait for.</summary>
        public int ParentPid;
        /// <summary>Test hook: show the window without taking keyboard focus, optionally at a fixed position.</summary>
        public bool NoActivate;
        public string WindowPos;

        public static SetupOptions Parse(string[] args)
        {
            var o = new SetupOptions();
            foreach (var raw in args)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var a = raw.Trim();
                if (!(a.StartsWith("/") || a.StartsWith("-"))) throw new ArgumentException("알 수 없는 인수: " + raw);
                a = a.TrimStart('/', '-');
                string name = a, value = null;
                int eq = a.IndexOf('=');
                if (eq > 0) { name = a.Substring(0, eq); value = a.Substring(eq + 1).Trim().Trim('"'); }
                switch (name.ToLowerInvariant())
                {
                    case "s": case "silent": case "quiet": o.Silent = true; break;
                    case "uninstall": o.Uninstall = true; break;
                    case "d": case "dir": o.InstallDir = RequireDir(name, value); break;
                    case "desktop": o.Desktop = true; break;
                    case "nodesktop": o.Desktop = false; break;
                    case "launch": o.Launch = true; o.LaunchExplicit = true; break;
                    case "nolaunch": o.Launch = false; o.LaunchExplicit = true; break;
                    case "removedata": o.RemoveData = true; break;
                    case "target": o.Target = RequireDir(name, value); break;
                    case "parent":
                        if (!int.TryParse(value, out o.ParentPid)) throw new ArgumentException("/parent= needs a process id");
                        break;
                    case "noactivate": o.NoActivate = true; break;
                    case "windowpos":
                        if (value == null || value.Split(',').Length != 2) throw new ArgumentException("/WindowPos=x,y");
                        o.WindowPos = value;
                        break;
                    case "regkey":
                        if (string.IsNullOrEmpty(value) || !value.StartsWith(@"Software\", StringComparison.OrdinalIgnoreCase))
                            throw new ArgumentException("/RegKey must be a key under HKCU\\Software");
                        o.RegKey = value; o.RegKeyExplicit = true; break;
                    case "startmenudir": o.StartMenuDir = RequireDir(name, value); o.StartMenuExplicit = true; break;
                    case "desktopdir": o.DesktopDir = RequireDir(name, value); o.DesktopDirExplicit = true; break;
                    case "datadirs":
                        o.DataDirs = new List<string>();
                        foreach (var d in (value ?? "").Split('|'))
                            if (d.Trim().Length > 0) o.DataDirs.Add(Path.GetFullPath(d.Trim()));
                        o.DataDirsExplicit = true;
                        break;
                    default: throw new ArgumentException("알 수 없는 옵션: " + raw);
                }
            }
            return o;
        }

        private static string RequireDir(string name, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("/" + name + "= needs a folder");
            var full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(value));
            if (full.Length <= 3) throw new ArgumentException("/" + name + " cannot be a drive root");
            return full.TrimEnd('\\');
        }
    }
}

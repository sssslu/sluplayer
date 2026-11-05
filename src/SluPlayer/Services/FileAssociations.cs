using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using SluPlayer.Core.Player;

namespace SluPlayer.Services;

public enum AssociationState
{
    /// <summary>Nothing registered.</summary>
    NotRegistered,
    /// <summary>Registered for this exe.</summary>
    Registered,
    /// <summary>Registered, but for an exe in another location (moved or another copy): register again.</summary>
    OtherLocation,
}

/// <summary>
/// Per-user file associations (HKCU, no admin rights), like mpv.net / VLC "register file types":
/// <list type="bullet">
/// <item>ProgIDs <c>SluPlayer.Video</c> / <c>SluPlayer.Audio</c> with icon and open command (<c>"exe" "%1"</c>).</item>
/// <item><c>.ext\OpenWithProgids</c> entries for every extension in <see cref="MediaFormats.Video"/> / <see cref="MediaFormats.Audio"/>,
///       so SluPlayer appears under "Open with" without taking over anything.</item>
/// <item><c>Applications\SluPlayer.exe</c> (+ SupportedTypes) and Capabilities + RegisteredApplications,
///       so SluPlayer is listed in Windows Settings › Default apps.</item>
/// </list>
/// Windows 10/11 do not allow programs to change the default app themselves; the user confirms in Settings
/// (<see cref="OpenDefaultAppsSettings"/>).
/// The registry root is configurable so tests can run against a scratch key instead of the real HKCU\Software\Classes.
/// </summary>
public sealed class FileAssociations
{
    public const string AppName = "SluPlayer";
    public const string VideoProgId = "SluPlayer.Video";
    public const string AudioProgId = "SluPlayer.Audio";

    private readonly RegistryKey _hive;
    private readonly string _classesPath;
    private readonly string _softwarePath;
    private readonly string _exePath;
    private readonly bool _notifyShell;

    /// <param name="hive">Usually <see cref="Registry.CurrentUser"/>.</param>
    /// <param name="classesPath">Classes root under the hive (real: <c>Software\Classes</c>).</param>
    /// <param name="softwarePath">Where Capabilities and RegisteredApplications go (real: <c>Software</c>).</param>
    /// <param name="exePath">Full path of SluPlayer.exe written into the commands.</param>
    /// <param name="notifyShell">Send SHCNE_ASSOCCHANGED after changes (off for tests).</param>
    public FileAssociations(RegistryKey hive, string classesPath, string softwarePath, string exePath, bool notifyShell)
    {
        _hive = hive;
        _classesPath = classesPath.Trim('\\');
        _softwarePath = softwarePath.Trim('\\');
        _exePath = exePath;
        _notifyShell = notifyShell;
    }

    /// <summary>The real thing: HKCU\Software\Classes for the running exe.</summary>
    public static FileAssociations ForCurrentUser()
        => new(Registry.CurrentUser, @"Software\Classes", "Software",
            Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "SluPlayer.exe"), notifyShell: true);

    public static IReadOnlyList<string> VideoExtensions { get; } = MediaFormats.Video.OrderBy(e => e, StringComparer.OrdinalIgnoreCase).ToList();
    public static IReadOnlyList<string> AudioExtensions { get; } = MediaFormats.Audio.OrderBy(e => e, StringComparer.OrdinalIgnoreCase).ToList();

    private string ExeFileName => Path.GetFileName(_exePath);
    private string OpenCommand => $"\"{_exePath}\" \"%1\"";
    private string IconRef => $"\"{_exePath}\",0";
    private string CapabilitiesPath => $@"{_softwarePath}\{AppName}\Capabilities";
    private string RegisteredAppsPath => $@"{_softwarePath}\RegisteredApplications";

    // =====================================================================
    // State
    // =====================================================================

    public AssociationState GetState()
    {
        using var cmd = _hive.OpenSubKey($@"{_classesPath}\{VideoProgId}\shell\open\command");
        if (cmd?.GetValue("") is not string command || string.IsNullOrWhiteSpace(command)) return AssociationState.NotRegistered;
        return PointsToThisExe(command) ? AssociationState.Registered : AssociationState.OtherLocation;
    }

    private bool PointsToThisExe(string command)
    {
        // The command is "<exe>" "%1"; compare the exe part only.
        var exe = command.TrimStart();
        if (exe.StartsWith('"')) exe = exe[1..(exe.IndexOf('"', 1) is var end and > 0 ? end : exe.Length)];
        else exe = exe.Split(' ')[0];
        try { return string.Equals(Path.GetFullPath(exe), Path.GetFullPath(_exePath), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    // =====================================================================
    // Register
    // =====================================================================

    public void Register()
    {
        using (var classes = _hive.CreateSubKey(_classesPath, writable: true))
        {
            WriteProgId(classes, VideoProgId, "SluPlayer 동영상");
            WriteProgId(classes, AudioProgId, "SluPlayer 오디오");
            foreach (var ext in VideoExtensions) AddOpenWith(classes, ext, VideoProgId);
            foreach (var ext in AudioExtensions) AddOpenWith(classes, ext, AudioProgId);

            // "Open with" entry for the exe itself, limited to the media types it understands.
            using var app = classes.CreateSubKey($@"Applications\{ExeFileName}", writable: true);
            app.SetValue("FriendlyAppName", AppName);
            using (var icon = app.CreateSubKey("DefaultIcon", writable: true)) icon.SetValue("", IconRef);
            using (var cmd = app.CreateSubKey(@"shell\open\command", writable: true)) cmd.SetValue("", OpenCommand);
            using (var types = app.CreateSubKey("SupportedTypes", writable: true))
            {
                foreach (var ext in VideoExtensions.Concat(AudioExtensions)) types.SetValue(ext, "");
            }
        }

        // Capabilities: makes SluPlayer selectable in Settings › Default apps.
        using (var cap = _hive.CreateSubKey(CapabilitiesPath, writable: true))
        {
            cap.SetValue("ApplicationName", AppName);
            cap.SetValue("ApplicationDescription", "동영상 플레이어 · 다운로더");
            cap.SetValue("ApplicationIcon", IconRef);
            using var fa = cap.CreateSubKey("FileAssociations", writable: true);
            foreach (var ext in VideoExtensions) fa.SetValue(ext, VideoProgId);
            foreach (var ext in AudioExtensions) fa.SetValue(ext, AudioProgId);
        }
        using (var reg = _hive.CreateSubKey(RegisteredAppsPath, writable: true))
            reg.SetValue(AppName, CapabilitiesPath);

        // Earlier versions could add "SluPlayer로 재생" to every folder's menu; that option is gone, so remove the entry.
        try { _hive.DeleteSubKeyTree($@"{_classesPath}\Directory\shell\{AppName}", throwOnMissingSubKey: false); }
        catch (Exception ex) { Log.Warn("could not remove the old folder menu entry: " + ex.Message); }

        NotifyShell();
        Log.Info($"file associations registered ({VideoExtensions.Count + AudioExtensions.Count} types) for {_exePath} under {_hive.Name}\\{_classesPath}");
    }

    // =====================================================================
    // Helpers
    // =====================================================================

    private void WriteProgId(RegistryKey classes, string progId, string friendlyName)
    {
        using var key = classes.CreateSubKey(progId, writable: true);
        key.SetValue("", friendlyName);
        key.SetValue("FriendlyTypeName", friendlyName);
        using (var icon = key.CreateSubKey("DefaultIcon", writable: true)) icon.SetValue("", IconRef);
        using (var shell = key.CreateSubKey("shell", writable: true)) shell.SetValue("", "open");
        using (var open = key.CreateSubKey(@"shell\open", writable: true)) open.SetValue("FriendlyAppName", AppName);
        using (var cmd = key.CreateSubKey(@"shell\open\command", writable: true)) cmd.SetValue("", OpenCommand);
    }

    private static void AddOpenWith(RegistryKey classes, string ext, string progId)
    {
        using var key = classes.CreateSubKey($@"{ext}\OpenWithProgids", writable: true);
        key.SetValue(progId, Array.Empty<byte>(), RegistryValueKind.None);
    }

    private void NotifyShell()
    {
        if (!_notifyShell) return;
        try { SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, 0, 0); }
        catch (Exception ex) { Log.Warn("SHChangeNotify failed: " + ex.Message); }
    }

    /// <summary>
    /// Opens Settings › Default apps on SluPlayer's own page (Windows 11), falling back to the general page.
    /// Programs can't set defaults themselves on Windows 10/11; the user picks SluPlayer there.
    /// </summary>
    public static void OpenDefaultAppsSettings()
    {
        foreach (var uri in new[] { $"ms-settings:defaultapps?registeredAppUser={AppName}", "ms-settings:defaultapps" })
        {
            try
            {
                Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
                return;
            }
            catch (Exception ex)
            {
                Log.Warn($"could not open {uri}: {ex.Message}");
            }
        }
    }

    private const int SHCNE_ASSOCCHANGED = 0x08000000;
    private const uint SHCNF_IDLIST = 0x0000;

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, nint item1, nint item2);
}

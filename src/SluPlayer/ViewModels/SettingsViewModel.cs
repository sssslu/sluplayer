using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using SluPlayer.Core.Player;
using SluPlayer.Services;
using SluPlayer.Services.Settings;
using SluPlayer.UI;

namespace SluPlayer.ViewModels;

/// <summary>An option of a combo box: the stored value and its Korean label.</summary>
public sealed record Choice(object Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// Settings view-model. Wraps <see cref="AppSettings"/> (a plain POCO): every setter stores the value, saves (debounced
/// by the shell) and applies it to the running player where it matters. Must be used on the UI thread.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject, IDisposable
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly IShell _shell;
    private readonly AppSettings _s;
    private readonly PlayerEngine _player;
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    public SettingsViewModel(IShell shell)
    {
        _shell = shell;
        _s = shell.Settings;
        _player = shell.Player;

        // A value set elsewhere (settings.json, an older version) gets its own entry so the combo box doesn't go blank.
        var hwdec = new List<Choice>
        {
            // The engine falls back to the copy method where zero-copy can't work (MpvOptionBuilder.EffectiveHwdec).
            new("auto-safe", "자동"), new("d3d11va", "D3D11VA"), new("nvdec", "NVDEC"), new("d3d11va-copy", "D3D11VA 복사"), new("no", "끄기"),
        };
        if (!hwdec.Any(c => (string)c.Value == Hwdec)) hwdec.Add(new Choice(Hwdec, Hwdec));
        HwdecChoices = hwdec;

        EnsureDownloadQualityChoice();
        _loggedIn = ReadYouTubeLogin();
        UI.Dialogs.YouTubeLoginDialog.LoginChanged += OnLoginChanged;
        _ = RefreshAssociationAsync();
    }

    /// <summary>Stores a new value, saves, applies it live and logs it. Returns false when nothing changed.</summary>
    /// <remarks>null is ignored: combo boxes push null while their items are being replaced.</remarks>
    private bool Set<T>(T current, T value, Action<T> assign, Action<T>? live = null, [CallerMemberName] string name = "")
    {
        if (_disposed || value is null || EqualityComparer<T>.Default.Equals(current, value)) return false;
        assign(value);
        OnPropertyChanged(name);
        Log.Info($"settings: {name} = {value}");
        if (live != null)
        {
            try { live(value); }
            catch (Exception ex) { Log.Warn($"settings: applying {name} failed: {ex.Message}"); }
        }
        _shell.SaveSettings();
        return true;
    }

    /// <summary>Sets an mpv property on the running player.</summary>
    private void Mpv(string property, string value)
    {
        if (_player.IsInitialized) _player.SetRaw(property, value);
    }

    /// <summary>Re-reads everything (menus, shortcuts or the sign-in window may have changed it).</summary>
    public void Refresh()
    {
        if (_disposed) return;
        EnsureDownloadQualityChoice();
        _loggedIn = ReadYouTubeLogin();
        OnPropertyChanged(string.Empty);
        _ = RefreshAssociationAsync();
    }

    // =====================================================================
    // 일반
    // =====================================================================

    public IReadOnlyList<Choice> OnTopChoices { get; } =
        [new(OnTopMode.Never, "안 함"), new(OnTopMode.Always, "항상"), new(OnTopMode.WhilePlaying, "재생 중")];

    public OnTopMode OnTop
    {
        get => _s.OnTop;
        set => Set(_s.OnTop, value, v => _s.OnTop = v, v => (_shell.Window as MainWindow)?.SetOnTopMode(v));
    }

    public bool ResumePlayback
    {
        get => _s.ResumePlayback;
        set => Set(_s.ResumePlayback, value, v => _s.ResumePlayback = v);
    }

    public bool AutoFitWindow
    {
        get => _s.AutoFitWindow;
        set => Set(_s.AutoFitWindow, value, v => _s.AutoFitWindow = v);
    }

    // ---- file associations ------------------------------------------------

    private AssociationState _association = AssociationState.NotRegistered;

    public string AssociationStatus => _association switch
    {
        AssociationState.Registered => "등록됨",
        AssociationState.OtherLocation => "다른 위치에 등록됨",
        _ => "등록 안 됨",
    };

    private async Task RefreshAssociationAsync()
    {
        try
        {
            var state = await Task.Run(() => FileAssociations.ForCurrentUser().GetState(), _cts.Token);
            if (_disposed || state == _association) return;
            _association = state;
            OnPropertyChanged(nameof(AssociationStatus));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Warn("settings: association state failed: " + ex.Message); }
    }

    /// <summary>Registers the file types, then opens Windows' default apps page (apps can't set defaults themselves).</summary>
    [RelayCommand]
    private async Task RegisterAssociations()
    {
        try
        {
            await Task.Run(() => FileAssociations.ForCurrentUser().Register());
            await RefreshAssociationAsync();
            _shell.ShowToast("파일 연결 등록됨", ToastKind.Success);
            FileAssociations.OpenDefaultAppsSettings();
        }
        catch (Exception ex)
        {
            Log.Error("file association registration failed", ex);
            _shell.ShowToast("파일 연결 실패: " + ex.Message, ToastKind.Error);
        }
    }

    // =====================================================================
    // 재생
    // =====================================================================

    public IReadOnlyList<Choice> HwdecChoices { get; }

    public string Hwdec
    {
        get => string.IsNullOrWhiteSpace(_s.Hwdec) ? "auto-safe" : _s.Hwdec;
        set => Set(Hwdec, value, v => _s.Hwdec = v, v => _player.SetHwdec(v));
    }

    public int SubtitleFontSize
    {
        get => _s.SubtitleFontSize;
        set => Set(_s.SubtitleFontSize, Math.Clamp(value, 10, 120), v => _s.SubtitleFontSize = v, v => Mpv("sub-font-size", v.ToString(Inv)));
    }

    public string ScreenshotDir
    {
        get => _s.ScreenshotDir;
        set => Set(_s.ScreenshotDir, value, v => _s.ScreenshotDir = v, v => Mpv("screenshot-dir", v));
    }

    [RelayCommand]
    private async Task BrowseScreenshotDir()
    {
        if (await PickFolderAsync("스크린샷 폴더", ScreenshotDir) is { } dir) ScreenshotDir = dir;
    }

    // =====================================================================
    // 다운로드
    // =====================================================================

    public string DownloadDir
    {
        get => _s.DownloadDir;
        set => Set(_s.DownloadDir, value, v => _s.DownloadDir = v);
    }

    public string AudioDownloadDir
    {
        get => _s.AudioDownloadDir;
        set => Set(_s.AudioDownloadDir, value, v => _s.AudioDownloadDir = v);
    }

    [RelayCommand]
    private async Task BrowseDownloadDir()
    {
        if (await PickFolderAsync("동영상 폴더", DownloadDir) is { } dir) DownloadDir = dir;
    }

    [RelayCommand]
    private async Task BrowseAudioDownloadDir()
    {
        if (await PickFolderAsync("오디오 폴더", AudioDownloadDir) is { } dir) AudioDownloadDir = dir;
    }

    public ObservableCollection<Choice> DownloadQualityChoices { get; } =
    [
        new("best", "최고 화질"), new("2160", "2160p (4K)"), new("1440", "1440p"), new("1080", "1080p"),
        new("720", "720p"), new("480", "480p"), new("360", "360p"),
    ];

    public string DownloadQuality
    {
        get => _s.DownloadQuality;
        set => Set(_s.DownloadQuality, value, v => _s.DownloadQuality = v);
    }

    /// <summary>
    /// The download page remembers the tier last picked (240p, 8K, or an odd one like 1012p): give it its own entry, in
    /// order, so the combo box shows it instead of going blank.
    /// </summary>
    private void EnsureDownloadQualityChoice()
    {
        var q = _s.DownloadQuality;
        if (!int.TryParse(q, NumberStyles.Integer, Inv, out int tier) || tier is < 100 or > 8000
            || DownloadQualityChoices.Any(c => Equals(c.Value, q))) return;
        int at = 1; // after "최고 화질"
        while (at < DownloadQualityChoices.Count && int.Parse((string)DownloadQualityChoices[at].Value, Inv) > tier) at++;
        DownloadQualityChoices.Insert(at, new Choice(q, $"{tier}p"));
    }

    // ---- YouTube sign-in ----------------------------------------------------

    private bool _loggedIn;

    private static bool ReadYouTubeLogin()
    {
        try { return YtDlpCookies.HasYouTubeLogin; } catch { return false; }
    }

    public string YouTubeStatus => _loggedIn ? "로그인됨" : "로그인 안 됨";

    public string YouTubeAction => _loggedIn ? "로그아웃" : "로그인";

    [RelayCommand]
    private async Task YouTubeAccount()
    {
        if (_loggedIn)
        {
            await UI.Dialogs.YouTubeLoginDialog.SignOutAsync(_shell);
            _shell.ShowToast("YouTube 로그아웃됨", ToastKind.Success);
        }
        else
        {
            await UI.Dialogs.YouTubeLoginDialog.ShowAsync(_shell);
        }
    }

    /// <summary>Sign-in or sign-out from any window.</summary>
    private void OnLoginChanged(bool signedIn)
    {
        if (_disposed) return;
        _loggedIn = ReadYouTubeLogin();
        OnPropertyChanged(nameof(YouTubeStatus));
        OnPropertyChanged(nameof(YouTubeAction));
    }

    // =====================================================================
    // 정보
    // =====================================================================

    public string AppVersion { get; } = GetAppVersion();

    private static string GetAppVersion()
    {
        var asm = typeof(App).Assembly;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            int plus = info.IndexOf('+');
            return plus > 0 ? info[..plus] : info;
        }
        return asm.GetName().Version?.ToString(3) ?? "1.0.0";
    }

    // =====================================================================
    // Helpers
    // =====================================================================

    // Folders may be on a network share: when it is offline, every file-system call blocks for half a minute or more,
    // so none of them runs on the UI thread.

    private async Task<string?> PickFolderAsync(string title, string current)
    {
        var dlg = new OpenFolderDialog { Title = title, Multiselect = false };
        // The dialog resolves InitialDirectory on the UI thread too: only pass a folder that answered in time.
        if (await DirectoryExistsAsync(current)) dlg.InitialDirectory = current;
        if (_disposed) return null;
        // Owned by the window the button was clicked in (the settings window).
        var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? _shell.Window;
        return dlg.ShowDialog(owner) == true ? dlg.FolderName : null;
    }

    /// <summary>Directory.Exists on a worker thread; false when it takes longer than 1.5 s.</summary>
    private static async Task<bool> DirectoryExistsAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var probe = Task.Run(() => Directory.Exists(path));
        return await Task.WhenAny(probe, Task.Delay(1500)) == probe && await probe;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        UI.Dialogs.YouTubeLoginDialog.LoginChanged -= OnLoginChanged;
        _cts.Cancel();
    }
}

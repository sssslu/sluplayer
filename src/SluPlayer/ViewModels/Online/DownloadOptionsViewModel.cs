using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SluPlayer.Core;
using SluPlayer.Services.Downloads;
using SluPlayer.Services.Settings;
using SluPlayer.UI;

namespace SluPlayer.ViewModels.Online;

/// <summary>Display helpers (Korean UI).</summary>
public static class OnlineFormat
{
    public static string Duration(double? seconds) => seconds is > 0 ? Formatters.Time(seconds.Value) : "";

    public static string Join(params string?[] parts) => string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));

    /// <summary>"C:\Users\me\Videos\SluPlayer" → "~\Videos\SluPlayer".</summary>
    public static string ShortPath(string path)
    {
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(home) && path.StartsWith(home, StringComparison.OrdinalIgnoreCase))
                return "~" + path[home.Length..];
        }
        catch { }
        return path;
    }

    /// <summary>Owner for dialogs opened from the download window (the active window, else the player).</summary>
    public static Window ActiveWindow(IShell shell)
        => Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? shell.Window;
}

/// <summary>One entry of the quality list ("최고 화질", "1080p60").</summary>
public sealed record QualityChoice(string Quality, int? ResCap, string Label)
{
    public int Tier => QualityTiers.TryParse(Quality, out var t) ? t : int.MaxValue;
    public override string ToString() => Label;
}

/// <summary>
/// The choices shared by the video and playlist cards: kind (영상/음악), quality, container or audio format, save
/// folder. Every change is remembered in the settings as the default for the next download.
/// </summary>
public abstract class DownloadOptionsViewModel : ObservableObject
{
    protected readonly IShell Shell;

    protected DownloadOptionsViewModel(IShell shell)
    {
        Shell = shell;
        var o = DownloadOptions.FromSettings(shell.Settings);
        _kind = o.Kind;
        _container = o.Container;
        _audioFormat = o.AudioFormat;
        ChangeFolderCommand = new RelayCommand(ChangeFolder);
    }

    protected AppSettings S => Shell.Settings;

    // ---- kind -------------------------------------------------------------------------

    private string _kind;
    public bool IsVideo { get => _kind == "video"; set { if (value) SetKind("video"); } }
    public bool IsAudio { get => _kind == "audio"; set { if (value) SetKind("audio"); } }

    private void SetKind(string kind)
    {
        if (_kind == kind) return;
        _kind = kind;
        S.DownloadKind = kind;
        Shell.SaveSettings();
        OnPropertyChanged(nameof(IsVideo));
        OnPropertyChanged(nameof(IsAudio));
        OnPropertyChanged(nameof(ShowQuality));
        OnPropertyChanged(nameof(OutputDirText));
    }

    // ---- quality ------------------------------------------------------------------------

    public ObservableCollection<QualityChoice> Qualities { get; } = new();

    /// <summary>The quality list is shown for videos when there is more than one choice.</summary>
    public bool ShowQuality => IsVideo && Qualities.Count > 1;

    private QualityChoice? _selectedQuality;
    public QualityChoice? SelectedQuality
    {
        get => _selectedQuality;
        set
        {
            if (!SetProperty(ref _selectedQuality, value) || value == null || S.DownloadQuality == value.Quality) return;
            S.DownloadQuality = value.Quality;
            Shell.SaveSettings();
        }
    }

    /// <summary>Fills the list ("best" first) and preselects the remembered quality or the best tier below it.</summary>
    protected void SetQualities(IReadOnlyList<QualityChoice> list)
    {
        foreach (var q in list) Qualities.Add(q);
        var pick = list[0];
        if (QualityTiers.TryParse(S.DownloadQuality, out var want))
            pick = list.Where(q => q.Quality != "best" && q.Tier <= want).MaxBy(q => q.Tier) ?? pick;
        _selectedQuality = pick;
        OnPropertyChanged(nameof(SelectedQuality));
        OnPropertyChanged(nameof(ShowQuality));
    }

    // ---- format -----------------------------------------------------------------------------

    private string _container;
    public bool IsMp4 { get => _container == "mp4"; set { if (value) SetContainer("mp4"); } }
    public bool IsMkv { get => _container == "mkv"; set { if (value) SetContainer("mkv"); } }

    private void SetContainer(string c)
    {
        if (_container == c) return;
        _container = c;
        S.DownloadVideoFormat = c;
        Shell.SaveSettings();
        OnPropertyChanged(nameof(IsMp4));
        OnPropertyChanged(nameof(IsMkv));
    }

    private string _audioFormat;
    public bool IsMp3 { get => _audioFormat == "mp3"; set { if (value) SetAudioFormat("mp3"); } }
    public bool IsM4a { get => _audioFormat == "m4a"; set { if (value) SetAudioFormat("m4a"); } }

    private void SetAudioFormat(string f)
    {
        if (_audioFormat == f) return;
        _audioFormat = f;
        S.DownloadAudioFormat = f;
        Shell.SaveSettings();
        OnPropertyChanged(nameof(IsMp3));
        OnPropertyChanged(nameof(IsM4a));
    }

    // ---- save folder --------------------------------------------------------------------------

    private string OutputDir => IsAudio ? S.AudioDownloadDir : S.DownloadDir;
    public string OutputDirText => OnlineFormat.ShortPath(OutputDir);
    public IRelayCommand ChangeFolderCommand { get; }

    private void ChangeFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = IsAudio ? "음악 저장 폴더" : "영상 저장 폴더" };
        try
        {
            if (Directory.Exists(OutputDir)) dlg.InitialDirectory = OutputDir;
        }
        catch { }
        if (dlg.ShowDialog(OnlineFormat.ActiveWindow(Shell)) != true) return;
        if (IsAudio) S.AudioDownloadDir = dlg.FolderName;
        else S.DownloadDir = dlg.FolderName;
        Shell.SaveSettings();
        OnPropertyChanged(nameof(OutputDirText));
    }

    // ---- result ---------------------------------------------------------------------------------

    protected DownloadOptions BuildOptions() => new DownloadOptions
    {
        Kind = _kind,
        Quality = _selectedQuality?.Quality ?? "best",
        ResCap = _selectedQuality?.ResCap,
        Container = _container,
        AudioFormat = _audioFormat,
    }.Normalized();
}

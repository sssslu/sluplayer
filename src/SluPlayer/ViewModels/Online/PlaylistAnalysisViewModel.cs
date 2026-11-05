using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SluPlayer.Services.Downloads;
using SluPlayer.UI;

namespace SluPlayer.ViewModels.Online;

/// <summary>The analysis card for a playlist/channel: the shared choices, "전체 받기" and a checklist of entries.</summary>
public sealed class PlaylistAnalysisViewModel : DownloadOptionsViewModel
{
    private bool _bulk;

    /// <param name="download">Queues the requests.</param>
    /// <param name="open">Analyses a nested playlist (channel tabs).</param>
    public PlaylistAnalysisViewModel(IShell shell, PlaylistInfo info, Action<IReadOnlyList<DownloadRequest>> download,
        Action<string> open) : base(shell)
    {
        Info = info;
        int i = 0;
        Entries = info.Entries.Select(e => new PlaylistEntryViewModel(++i, e, OnEntrySelectionChanged, () => open(e.Url))).ToList();
        VideoCount = Entries.Count(e => !e.IsPlaylist);

        var list = new List<QualityChoice> { new("best", null, "최고 화질") };
        list.AddRange(QualityTiers.Common.Select(t => new QualityChoice(t.ToString(CultureInfo.InvariantCulture), null, QualityTiers.TierName(t))));
        SetQualities(list);

        DownloadAllCommand = new RelayCommand(() => download(CreateRequests(Entries)), () => VideoCount > 0);
        DownloadSelectedCommand = new RelayCommand(() => download(CreateRequests(Entries.Where(e => e.IsSelected))), () => SelectedCount > 0);
    }

    public PlaylistInfo Info { get; }
    public string Title => Info.Title;
    public string MetaText => OnlineFormat.Join(Info.Uploader, HasVideos ? $"{VideoCount}개" : null);
    public int VideoCount { get; }
    public bool HasVideos => VideoCount > 0;
    public IReadOnlyList<PlaylistEntryViewModel> Entries { get; }
    public bool HasEntries => Entries.Count > 0;

    // ---- selection ------------------------------------------------------------------------

    private int _selectedCount;
    public int SelectedCount { get => _selectedCount; private set => SetProperty(ref _selectedCount, value); }

    public string DownloadAllText => $"전체 받기 ({VideoCount})";
    public string DownloadSelectedText => $"선택 받기 ({SelectedCount})";

    /// <summary>Tri-state "전체 선택" box.</summary>
    public bool? AllSelected
    {
        get => SelectedCount == 0 ? false : SelectedCount == VideoCount ? true : null;
        set
        {
            bool select = value == true; // clicking an indeterminate box clears it
            _bulk = true;
            try
            {
                foreach (var e in Entries) e.IsSelected = select;
            }
            finally { _bulk = false; }
            Recount();
        }
    }

    private void OnEntrySelectionChanged()
    {
        if (!_bulk) Recount();
    }

    private void Recount()
    {
        SelectedCount = Entries.Count(e => e.IsSelected);
        OnPropertyChanged(nameof(AllSelected));
        OnPropertyChanged(nameof(DownloadSelectedText));
        DownloadSelectedCommand.NotifyCanExecuteChanged();
    }

    // ---- commands ------------------------------------------------------------------------------

    public IRelayCommand DownloadAllCommand { get; }
    public IRelayCommand DownloadSelectedCommand { get; }

    private List<DownloadRequest> CreateRequests(IEnumerable<PlaylistEntryViewModel> entries)
    {
        var options = BuildOptions();
        return entries.Where(e => !e.IsPlaylist)
            .Select(e => new DownloadRequest(e.Url, options, e.Title, e.Entry.Channel ?? Info.Uploader, e.Entry.Thumbnail,
                e.Entry.Duration, Key: UrlTools.Key(e.Url, e.Entry.IeKey, e.Entry.Id), FromPlaylist: true,
                PlaylistItem: e.Entry.Item))
            .ToList();
    }
}

public sealed class PlaylistEntryViewModel : ObservableObject
{
    private readonly Action _onSelectionChanged;

    public PlaylistEntryViewModel(int index, PlaylistEntryInfo entry, Action onSelectionChanged, Action open)
    {
        Index = index;
        Entry = entry;
        _onSelectionChanged = onSelectionChanged;
        OpenCommand = new RelayCommand(open);
    }

    public PlaylistEntryInfo Entry { get; }
    public int Index { get; }
    public string Title => Entry.Title;
    public string Url => Entry.Url;
    public string DurationText => OnlineFormat.Duration(Entry.Duration);

    /// <summary>A nested playlist (channel tab): opened with [열기] instead of being downloaded.</summary>
    public bool IsPlaylist => Entry.IsPlaylist;
    public bool IsVideo => !Entry.IsPlaylist;

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (IsPlaylist) value = false;
            if (SetProperty(ref _isSelected, value)) _onSelectionChanged();
        }
    }

    public IRelayCommand OpenCommand { get; }
}

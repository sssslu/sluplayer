using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using SluPlayer.Core;
using SluPlayer.Core.Player;

namespace SluPlayer.UI.Views.Player;

/// <summary>One row of the player's playlist panel. Identity is mpv's stable playlist entry id.</summary>
public sealed partial class PlaylistItemViewModel : ObservableObject
{
    public PlaylistItemViewModel(long id)
    {
        Id = id;
        Title = "";
    }

    public long Id { get; }

    public string Filename { get; private set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NumberText))]
    public partial int Index { get; set; }

    [ObservableProperty] public partial string Title { get; set; }
    [ObservableProperty] public partial string? DurationText { get; set; }
    [ObservableProperty] public partial bool IsCurrent { get; set; }

    public string NumberText => (Index + 1).ToString(CultureInfo.InvariantCulture);
    public bool IsUrl => MediaFormats.IsUrl(Filename);

    private double _knownDuration;

    /// <param name="duration">Known length in seconds (resume history), or 0.</param>
    public void Update(PlaylistEntry entry, int index, bool isCurrent, double duration)
    {
        Index = index;
        IsCurrent = isCurrent;
        if (Filename != entry.Filename || Title != entry.DisplayName)
        {
            Filename = entry.Filename;
            Title = entry.DisplayName;
        }
        SetDuration(duration);
    }

    public void SetDuration(double seconds)
    {
        if (seconds <= 0 || Math.Abs(seconds - _knownDuration) < 0.5) return;
        _knownDuration = seconds;
        DurationText = Formatters.Time(seconds);
    }
}

/// <summary>ObservableCollection that can replace its whole content with a single Reset notification.</summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public void ResetTo(IEnumerable<T> items)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (var item in items) Items.Add(item);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}

using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SluPlayer.Core.Player;
using SluPlayer.Services;
using SluPlayer.UI.Views.Player;

namespace SluPlayer.UI.Views;

/// <summary>
/// Player-side panel docked right of the video: the current mpv playlist (numbered, current entry marked,
/// double-click to play, Delete to remove, drag &amp; drop to reorder, files or links dropped anywhere on the panel
/// are appended). The list is virtualized and kept in sync with <see cref="PlayerEngine"/> incrementally, so
/// selection and scroll position survive playlist updates.
/// </summary>
public partial class PlaylistPanel : UserControl
{
    private const string DragFormat = "SluPlayer.PlaylistEntryId";
    /// <summary>Above this many structural changes a playlist update is applied as one reset instead of item by item.</summary>
    private const int ResetThreshold = 48;

    private readonly IShell _shell;
    private readonly PlayerEngine _player;
    private readonly BulkObservableCollection<PlaylistItemViewModel> _items = new();
    private PlaylistItemViewModel? _current;

    // Selection to restore after our own playlist edits (mpv answers asynchronously).
    private long? _reselectId;
    private HashSet<long>? _removingIds;
    private int _selectAfterRemove = -1;

    // Drag & drop
    private Point _dragStart;
    private PlaylistItemViewModel? _dragCandidate;
    private bool _dragging;
    private int _dropIndex = -1;

    public PlaylistPanel(IShell shell)
    {
        _shell = shell;
        _player = shell.Player;
        InitializeComponent();
        PlaylistList.ItemsSource = _items;
        SyncPlaylist();
        _player.PropertyChanged += OnPlayerChanged;
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) Dispatcher.BeginInvoke(ScrollToCurrent, DispatcherPriority.Loaded);
        };
        // Drops anywhere else on the panel (header, empty area) append as well: unhandled they would reach
        // MainWindow.OnDrop, which replaces the playlist.
        DragOver += Panel_DragOver;
        Drop += Panel_Drop;
    }

    // =====================================================================
    // Engine → UI
    // =====================================================================

    private void OnPlayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PlayerEngine.Playlist):
                SyncPlaylist();
                break;
            case nameof(PlayerEngine.PlaylistPos):
                UpdateCurrent(scroll: true);
                break;
            case nameof(PlayerEngine.Duration):
                UpdateCurrentDuration();
                break;
        }
    }

    private void SyncPlaylist()
    {
        var entries = _player.Playlist;
        int pos = _player.PlaylistPos;
        var durations = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in HistoryService.Snapshot())
            if (h.Duration > 0) durations.TryAdd(h.Path, h.Duration);
        var byId = new Dictionary<long, PlaylistItemViewModel>(_items.Count);
        foreach (var vm in _items) byId.TryAdd(vm.Id, vm);

        var target = new List<PlaylistItemViewModel>(entries.Count);
        var seen = new HashSet<long>();
        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (!seen.Add(entry.Id)) continue;
            if (!byId.TryGetValue(entry.Id, out var vm)) vm = new PlaylistItemViewModel(entry.Id);
            durations.TryGetValue(entry.Filename, out var duration);
            bool current = pos >= 0 ? i == pos : entry.IsCurrent;
            vm.Update(entry, target.Count, current, duration);
            target.Add(vm);
        }

        ApplyOrder(target);
        UpdateCurrent(scroll: false);
        UpdateCurrentDuration();
        RestoreSelectionAfterEdit();
        UpdateCount();
    }

    /// <summary>Turns <see cref="_items"/> into <paramref name="target"/> with minimal collection changes.</summary>
    private void ApplyOrder(List<PlaylistItemViewModel> target)
    {
        var targetSet = new HashSet<PlaylistItemViewModel>(target);
        var existing = new HashSet<PlaylistItemViewModel>(_items);
        int removals = _items.Count(vm => !targetSet.Contains(vm));
        int inserts = target.Count(vm => !existing.Contains(vm));
        if (removals + inserts > ResetThreshold)
        {
            ResetPreserving(target);
            return;
        }

        for (int i = _items.Count - 1; i >= 0; i--)
            if (!targetSet.Contains(_items[i])) _items.RemoveAt(i);

        int moves = 0;
        for (int i = 0; i < target.Count; i++)
        {
            var vm = target[i];
            if (i < _items.Count && ReferenceEquals(_items[i], vm)) continue;
            int found = -1;
            for (int j = i + 1; j < _items.Count; j++)
            {
                if (ReferenceEquals(_items[j], vm)) { found = j; break; }
            }
            if (found >= 0)
            {
                if (++moves > ResetThreshold) { ResetPreserving(target); return; }
                _items.Move(found, i);
            }
            else
            {
                _items.Insert(i, vm);
            }
        }
        while (_items.Count > target.Count) _items.RemoveAt(_items.Count - 1);
    }

    /// <summary>Replaces the whole list (e.g. shuffle, new folder) while keeping selection and scroll offset.</summary>
    private void ResetPreserving(List<PlaylistItemViewModel> target)
    {
        var selectedIds = PlaylistList.SelectedItems.OfType<PlaylistItemViewModel>().Select(v => v.Id).ToHashSet();
        var scroller = FindDescendant<ScrollViewer>(PlaylistList);
        double offset = scroller?.VerticalOffset ?? 0;
        _items.ResetTo(target);
        if (selectedIds.Count > 0)
        {
            foreach (var vm in _items.Where(v => selectedIds.Contains(v.Id)))
                PlaylistList.SelectedItems.Add(vm);
        }
        if (scroller != null && offset > 0)
            Dispatcher.BeginInvoke(() => scroller.ScrollToVerticalOffset(offset), DispatcherPriority.Loaded);
    }

    private void UpdateCurrent(bool scroll)
    {
        int pos = _player.PlaylistPos;
        var cur = pos >= 0 && pos < _items.Count ? _items[pos] : _items.FirstOrDefault(v => v.IsCurrent && pos < 0);
        foreach (var vm in _items)
        {
            bool isCur = ReferenceEquals(vm, cur);
            if (vm.IsCurrent != isCur) vm.IsCurrent = isCur;
        }
        bool changed = !ReferenceEquals(_current, cur);
        _current = cur;
        // Follow the playing entry, but never yank the list away from under the user's pointer.
        if (scroll && changed && !_dragging && !PlaylistList.IsMouseOver)
            Dispatcher.BeginInvoke(ScrollToCurrent, DispatcherPriority.Loaded);
    }

    private void UpdateCurrentDuration()
    {
        if (_current == null || _player.Duration <= 0 || _player.CurrentPath == null) return;
        if (!string.Equals(_current.Filename, _player.CurrentPath, StringComparison.OrdinalIgnoreCase)) return;
        _current.SetDuration(_player.Duration);
    }

    private void UpdateCount()
    {
        int n = _items.Count;
        CountText.Text = n > 0 ? n.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
        EmptyText.Visibility = n == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearButton.IsEnabled = n > 1;
    }

    private void RestoreSelectionAfterEdit()
    {
        if (_reselectId is long id)
        {
            var vm = _items.FirstOrDefault(v => v.Id == id);
            if (vm != null)
            {
                _reselectId = null;
                Select(vm);
            }
        }
        if (_removingIds != null && !_items.Any(v => _removingIds.Contains(v.Id)))
        {
            _removingIds = null;
            if (_items.Count > 0 && _selectAfterRemove >= 0)
                Select(_items[Math.Min(_selectAfterRemove, _items.Count - 1)]);
            _selectAfterRemove = -1;
        }
    }

    private void Select(PlaylistItemViewModel vm)
    {
        bool hadFocus = PlaylistList.IsKeyboardFocusWithin;
        PlaylistList.SelectedItem = vm;
        PlaylistList.ScrollIntoView(vm);
        if (hadFocus)
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (PlaylistList.ItemContainerGenerator.ContainerFromItem(vm) is ListBoxItem item) item.Focus();
            }, DispatcherPriority.Loaded);
        }
    }

    /// <summary>Scrolls so the current entry sits in the middle of the viewport (pixel-based virtualized scrolling).</summary>
    private void ScrollToCurrent()
    {
        var item = _current;
        if (!IsVisible || item == null) return;
        PlaylistList.ScrollIntoView(item);
        Dispatcher.BeginInvoke(() =>
        {
            if (PlaylistList.ItemContainerGenerator.ContainerFromItem(item) is not FrameworkElement container) return;
            var scroller = FindDescendant<ScrollViewer>(PlaylistList);
            if (scroller == null || !container.IsVisible) return;
            double y = container.TranslatePoint(new Point(0, 0), scroller).Y;
            double target = scroller.VerticalOffset + y - (scroller.ViewportHeight - container.ActualHeight) / 2;
            scroller.ScrollToVerticalOffset(Math.Max(0, target));
        }, DispatcherPriority.Loaded);
    }

    // =====================================================================
    // UI → engine
    // =====================================================================

    private void Close_Click(object sender, RoutedEventArgs e) => (_shell.Window as MainWindow)?.TogglePlaylistPanel();

    private void Add_Click(object sender, RoutedEventArgs e) => _shell.OpenFilesDialog(append: true);

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (_items.Count <= 1) return;
        _player.ClearPlaylist();
        _player.ShowOsd("목록 비움");
    }

    private void PlaylistList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (ItemFromSource(e.OriginalSource as DependencyObject, PlaylistList) is not PlaylistItemViewModel vm) return;
        _player.PlayIndex(vm.Index);
        e.Handled = true;
    }

    private void PlaylistList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && Keyboard.Modifiers == ModifierKeys.None)
        {
            RemoveSelected();
            e.Handled = true;
        }
    }

    private void RemoveSelected()
    {
        var indices = PlaylistList.SelectedItems.OfType<PlaylistItemViewModel>()
            .Select(v => v.Index).Distinct().OrderByDescending(i => i).ToList();
        if (indices.Count == 0) return;
        _removingIds = PlaylistList.SelectedItems.OfType<PlaylistItemViewModel>().Select(v => v.Id).ToHashSet();
        _selectAfterRemove = indices[^1];
        // Highest index first, so the remaining indices stay valid while mpv processes the queue.
        foreach (var i in indices) _player.RemoveAt(i);
    }

    private void MoveBy(PlaylistItemViewModel vm, int delta)
    {
        int from = vm.Index;
        // mpv's playlist-move puts the entry *before* the target index.
        int to = delta < 0 ? from - 1 : from + 2;
        if (from < 0 || to < 0 || to > _items.Count) return;
        _reselectId = vm.Id;
        _player.MoveItem(from, to);
    }

    // ---- context menu -----------------------------------------------------------------------------

    private void PlaylistList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (ItemFromSource(e.OriginalSource as DependencyObject, PlaylistList) is not PlaylistItemViewModel vm)
        {
            e.Handled = true; // no menu over empty space
            return;
        }
        if (!PlaylistList.SelectedItems.Contains(vm)) PlaylistList.SelectedItem = vm;
        int selected = PlaylistList.SelectedItems.Count;
        var menu = PlaylistList.ContextMenu!;
        menu.Items.Clear();
        menu.Items.Add(MakeMenuItem("재생", () => _player.PlayIndex(vm.Index)));
        menu.Items.Add(new Separator());
        menu.Items.Add(MakeMenuItem("위로 이동", () => MoveBy(vm, -1), enabled: selected == 1 && vm.Index > 0));
        menu.Items.Add(MakeMenuItem("아래로 이동", () => MoveBy(vm, +1), enabled: selected == 1 && vm.Index < _items.Count - 1));
        if (!vm.IsUrl) menu.Items.Add(MakeMenuItem("파일 위치 열기", () => RevealInExplorer(vm.Filename)));
        menu.Items.Add(new Separator());
        menu.Items.Add(MakeMenuItem(selected > 1 ? $"제거 ({selected}개)" : "제거", RemoveSelected, "Delete"));
    }

    private static MenuItem MakeMenuItem(string header, Action click, string? gesture = null, bool enabled = true)
    {
        var mi = new MenuItem { Header = header, InputGestureText = gesture ?? "", IsEnabled = enabled };
        mi.Click += (_, _) => click();
        return mi;
    }

    private void RevealInExplorer(string path)
    {
        Task.Run(() =>
        {
            try
            {
                if (File.Exists(path))
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
                else if (Path.GetDirectoryName(path) is { } dir && Directory.Exists(dir))
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
                else
                    Dispatcher.BeginInvoke(() => _shell.ShowToast("파일 없음", ToastKind.Warning));
            }
            catch (Exception ex)
            {
                Log.Warn("reveal in explorer: " + ex.Message);
            }
        });
    }

    // ---- drag & drop ------------------------------------------------------------------------------

    private void PlaylistList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragCandidate = ItemFromSource(e.OriginalSource as DependencyObject, PlaylistList) as PlaylistItemViewModel;
        _dragStart = e.GetPosition(PlaylistList);
    }

    private void PlaylistList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => _dragCandidate = null;

    private void PlaylistList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragCandidate == null || _dragging || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(PlaylistList);
        if (Math.Abs(p.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(p.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        if (_items.Count < 2) { _dragCandidate = null; return; }

        var vm = _dragCandidate;
        _dragCandidate = null;
        _dragging = true;
        try
        {
            PlaylistList.SelectedItem = vm;
            DragDrop.DoDragDrop(PlaylistList, new DataObject(DragFormat, vm.Id), DragDropEffects.Move);
        }
        catch (Exception ex)
        {
            Log.Warn("playlist drag failed: " + ex.Message);
        }
        finally
        {
            _dragging = false;
            HideDropMarker();
        }
    }

    private void PlaylistList_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = HandleDragOver(e.Data, e.GetPosition(PlaylistList));
    }

    /// <summary>Drag feedback for a pointer at <paramref name="p"/> (list coordinates); returns the allowed effect.</summary>
    private DragDropEffects HandleDragOver(IDataObject data, Point p)
    {
        if (data.GetDataPresent(DragFormat))
        {
            UpdateDropMarker(p);
            AutoScroll(p);
            return DragDropEffects.Move;
        }
        if (data.GetDataPresent(DataFormats.FileDrop) || DroppedUrl(data) != null)
        {
            // Files from Explorer (and links) are appended to the end of the playlist.
            ShowDropMarkerAtEnd();
            return DragDropEffects.Copy;
        }
        HideDropMarker();
        return DragDropEffects.None;
    }

    private void PlaylistList_DragLeave(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var p = e.GetPosition(PlaylistList);
        if (p.X < 0 || p.Y < 0 || p.X >= PlaylistList.ActualWidth || p.Y >= PlaylistList.ActualHeight) HideDropMarker();
    }

    private void PlaylistList_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        int dropIndex = _dropIndex;
        HideDropMarker();
        if (e.Data.GetData(DragFormat) is long id)
        {
            var vm = _items.FirstOrDefault(v => v.Id == id);
            int to = ComputeDropIndex(e.GetPosition(PlaylistList), out _);
            if (to < 0) to = dropIndex;
            if (vm == null || to < 0) return;
            int from = vm.Index;
            if (to == from || to == from + 1) return;
            _reselectId = id;
            _player.MoveItem(from, to);
        }
        else if (DroppedItems(e.Data) is { } items)
        {
            AppendDropped(items);
        }
    }

    private void Panel_DragOver(object sender, DragEventArgs e)
    {
        if (e.Handled || DroppedItems(e.Data) == null) return; // the list handled it, or not files/a link
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void Panel_Drop(object sender, DragEventArgs e)
    {
        if (e.Handled || DroppedItems(e.Data) is not { } items) return;
        e.Handled = true;
        HideDropMarker();
        AppendDropped(items);
    }

    /// <summary>Appends dropped files, folders or a link and says what happened.</summary>
    private void AppendDropped(IReadOnlyList<string> items)
    {
        // Same rule as MainWindow.Play: subtitle files dropped during playback become subtitle tracks instead.
        bool subtitles = _player.HasMedia && items.All(i => MediaFormats.Classify(i) == MediaKind.Subtitle);
        // Folders (no media extension) and playlist files are expanded later, so their entries can't be counted here.
        bool counted = items.All(i => MediaFormats.Classify(i) is MediaKind.Video or MediaKind.Audio);
        _shell.Play(items, append: true);
        _shell.ShowToast(
            subtitles ? (items.Count > 1 ? $"자막 {items.Count}개 추가" : "자막 추가")
            : counted && items.Count > 1 ? $"재생목록에 {items.Count}개 추가"
            : "재생목록에 추가",
            ToastKind.Success);
    }

    /// <summary>Files dropped from Explorer, or a link (text) dropped from a browser.</summary>
    private static IReadOnlyList<string>? DroppedItems(IDataObject data)
    {
        try
        {
            if (data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files) return files;
        }
        catch
        {
            return null; // some drag sources throw on GetData
        }
        return DroppedUrl(data) is { } url ? [url] : null;
    }

    private static string? DroppedUrl(IDataObject data)
    {
        try
        {
            if (data.GetData(DataFormats.UnicodeText) is not string text) return null;
            var first = text.Trim().Split('\n')[0].Trim();
            return MediaFormats.IsUrl(first) ? first : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Gap index (0..Count) under the pointer: the dragged entry would be placed before this index.</summary>
    private int ComputeDropIndex(Point p, out double lineY)
    {
        lineY = 0;
        if (_items.Count == 0) return 0;
        var container = FindAncestor<ListBoxItem>(PlaylistList.InputHitTest(p) as DependencyObject);
        if (container?.DataContext is PlaylistItemViewModel vm)
        {
            double top = container.TranslatePoint(new Point(0, 0), PlaylistList).Y;
            bool after = p.Y > top + container.ActualHeight / 2;
            lineY = after ? top + container.ActualHeight + 1 : top - 1;
            return vm.Index + (after ? 1 : 0);
        }
        // Below the last entry (only possible when it is realized, i.e. scrolled into view).
        if (PlaylistList.ItemContainerGenerator.ContainerFromIndex(_items.Count - 1) is ListBoxItem last && last.IsVisible)
        {
            double top = last.TranslatePoint(new Point(0, 0), PlaylistList).Y;
            if (p.Y >= top + last.ActualHeight)
            {
                lineY = top + last.ActualHeight + 1;
                return _items.Count;
            }
        }
        return -1;
    }

    private void UpdateDropMarker(Point p)
    {
        int index = ComputeDropIndex(p, out double y);
        if (index < 0) return; // between rows: keep the previous marker (no flicker)
        _dropIndex = index;
        PlaceMarker(y);
    }

    private void ShowDropMarkerAtEnd()
    {
        if (_items.Count == 0) { HideDropMarker(); return; }
        if (PlaylistList.ItemContainerGenerator.ContainerFromIndex(_items.Count - 1) is ListBoxItem last && last.IsVisible)
        {
            double top = last.TranslatePoint(new Point(0, 0), PlaylistList).Y;
            _dropIndex = _items.Count;
            PlaceMarker(top + last.ActualHeight + 1);
        }
        else
        {
            HideDropMarker();
        }
    }

    private void PlaceMarker(double y)
    {
        y = Math.Clamp(y, 1, Math.Max(1, PlaylistList.ActualHeight - 1));
        DropMarker.Width = Math.Max(0, PlaylistList.ActualWidth - 30);
        Canvas.SetLeft(DropMarker, 12);
        Canvas.SetTop(DropMarker, y - DropMarker.Height / 2);
        DropMarker.Visibility = Visibility.Visible;
    }

    private void HideDropMarker()
    {
        _dropIndex = -1;
        DropMarker.Visibility = Visibility.Collapsed;
    }

    private void AutoScroll(Point p)
    {
        var scroller = FindDescendant<ScrollViewer>(PlaylistList);
        if (scroller == null) return;
        const double edge = 40;
        double h = PlaylistList.ActualHeight;
        if (p.Y < edge) scroller.ScrollToVerticalOffset(scroller.VerticalOffset - (edge - p.Y) * 0.5);
        else if (p.Y > h - edge) scroller.ScrollToVerticalOffset(scroller.VerticalOffset + (p.Y - (h - edge)) * 0.5);
    }

    // ---- tree helpers -------------------------------------------------------------------------------

    private static object? ItemFromSource(DependencyObject? source, ItemsControl list)
    {
        var container = FindAncestor<ListBoxItem>(source);
        if (container == null || ItemsControl.ItemsControlFromItemContainer(container) != list) return null;
        var item = list.ItemContainerGenerator.ItemFromContainer(container);
        return item == DependencyProperty.UnsetValue ? null : item;
    }

    private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null)
        {
            if (d is T t) return t;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) return t;
            if (FindDescendant<T>(child) is { } found) return found;
        }
        return null;
    }
}

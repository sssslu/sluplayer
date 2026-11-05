using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using SluPlayer.Core.Interop;
using SluPlayer.Core.Player;

namespace SluPlayer.UI;

// Keyboard shortcuts, mouse gestures over the video, drag & drop.
public partial class MainWindow
{
    private Dictionary<(Key, ModifierKeys), Action>? _shortcuts;
    private Point _downPos;
    private bool _leftDown;
    private bool _dragMoved;
    private long _lastClickPauseTick;
    private Point _lastMovePos = new(double.NaN, double.NaN);

    /// <summary>Default shortcut map (PotPlayer / YouTube / mpv conventions).</summary>
    private Dictionary<(Key, ModifierKeys), Action> BuildShortcuts()
    {
        var p = _player;
        var map = new Dictionary<(Key, ModifierKeys), Action>();
        void Add(Key k, ModifierKeys m, Action a) => map[(k, m)] = a;

        // Playback
        Add(Key.Space, ModifierKeys.None, p.PlayPause);
        Add(Key.K, ModifierKeys.None, p.PlayPause);
        Add(Key.S, ModifierKeys.None, p.Stop);
        Add(Key.MediaPlayPause, ModifierKeys.None, p.PlayPause);
        Add(Key.MediaStop, ModifierKeys.None, p.Stop);
        Add(Key.MediaNextTrack, ModifierKeys.None, p.Next);
        Add(Key.MediaPreviousTrack, ModifierKeys.None, p.Previous);

        // Window
        Add(Key.Enter, ModifierKeys.None, ToggleFullscreen);
        Add(Key.Enter, ModifierKeys.Alt, ToggleFullscreen);
        Add(Key.F, ModifierKeys.None, ToggleFullscreen);
        Add(Key.F11, ModifierKeys.None, ToggleFullscreen);
        Add(Key.Escape, ModifierKeys.None, () => { if (_isFullscreen) ExitFullscreen(); });
        Add(Key.D0, ModifierKeys.Alt, () => FitWindowToVideo(0.5));
        Add(Key.D1, ModifierKeys.Alt, () => FitWindowToVideo(1.0));
        Add(Key.D2, ModifierKeys.Alt, () => FitWindowToVideo(2.0));

        // Seeking (← → 5 s, J L 10 s, Ctrl 30 s, Shift 1 min)
        Add(Key.Left, ModifierKeys.None, () => p.SeekRelative(-5));
        Add(Key.Right, ModifierKeys.None, () => p.SeekRelative(5));
        Add(Key.J, ModifierKeys.None, () => p.SeekRelative(-10));
        Add(Key.L, ModifierKeys.None, () => p.SeekRelative(10));
        Add(Key.Left, ModifierKeys.Control, () => p.SeekRelative(-30));
        Add(Key.Right, ModifierKeys.Control, () => p.SeekRelative(30));
        Add(Key.Left, ModifierKeys.Shift, () => p.SeekRelative(-60));
        Add(Key.Right, ModifierKeys.Shift, () => p.SeekRelative(60));
        Add(Key.Left, ModifierKeys.Alt, () => p.SeekRelative(-1));
        Add(Key.Right, ModifierKeys.Alt, () => p.SeekRelative(1));
        Add(Key.Home, ModifierKeys.None, () => p.SeekTo(0));
        Add(Key.End, ModifierKeys.None, () => p.SeekPercent(100));
        Add(Key.OemPeriod, ModifierKeys.None, p.FrameStep);
        Add(Key.OemComma, ModifierKeys.None, p.FrameBackStep);

        // Volume
        Add(Key.Up, ModifierKeys.None, () => p.ChangeVolume(5));
        Add(Key.Down, ModifierKeys.None, () => p.ChangeVolume(-5));
        Add(Key.Up, ModifierKeys.Control, () => p.ChangeVolume(1));
        Add(Key.Down, ModifierKeys.Control, () => p.ChangeVolume(-1));
        Add(Key.M, ModifierKeys.None, p.ToggleMute);

        // Files & chapters
        Add(Key.PageUp, ModifierKeys.None, p.Previous);
        Add(Key.PageDown, ModifierKeys.None, p.Next);
        Add(Key.PageUp, ModifierKeys.Control, p.PreviousChapter);
        Add(Key.PageDown, ModifierKeys.Control, p.NextChapter);

        // Speed (PotPlayer C/X/Z, mpv [ ] Backspace)
        Add(Key.C, ModifierKeys.None, () => p.ChangeSpeed(0.1));
        Add(Key.X, ModifierKeys.None, () => p.ChangeSpeed(-0.1));
        Add(Key.Z, ModifierKeys.None, () => p.SetSpeed(1.0));
        Add(Key.OemCloseBrackets, ModifierKeys.None, () => p.ChangeSpeed(0.1));
        Add(Key.OemOpenBrackets, ModifierKeys.None, () => p.ChangeSpeed(-0.1));
        Add(Key.Back, ModifierKeys.None, () => p.SetSpeed(1.0));

        // Screenshots
        Add(Key.E, ModifierKeys.Control, p.Screenshot);
        Add(Key.F8, ModifierKeys.None, p.Screenshot);

        // Subtitles / audio
        Add(Key.V, ModifierKeys.None, p.CycleSubtitleTrack);
        Add(Key.V, ModifierKeys.Shift, p.ToggleSubtitleVisibility);
        Add(Key.B, ModifierKeys.None, p.CycleAudioTrack);
        Add(Key.OemComma, ModifierKeys.Shift, () => p.SetSubDelay(p.SubDelay - 0.5)); // <
        Add(Key.OemPeriod, ModifierKeys.Shift, () => p.SetSubDelay(p.SubDelay + 0.5)); // >
        Add(Key.OemQuestion, ModifierKeys.None, () => p.SetSubDelay(0));

        // Video
        Add(Key.A, ModifierKeys.None, CycleAspect);
        Add(Key.R, ModifierKeys.Control, () => p.Rotate(90));
        Add(Key.R, ModifierKeys.Control | ModifierKeys.Shift, () => p.Rotate(-90));
        Add(Key.H, ModifierKeys.Control, () => { p.ToggleHwdec(); SaveSettings(); });

        // Repeat
        Add(Key.L, ModifierKeys.Shift, p.CycleLoop);

        // App
        Add(Key.F5, ModifierKeys.None, ShowSettings);
        Add(Key.F6, ModifierKeys.None, TogglePlaylistPanel);
        Add(Key.O, ModifierKeys.Control, () => OpenFilesDialog());
        Add(Key.O, ModifierKeys.Control | ModifierKeys.Shift, OpenFolderDialog);
        Add(Key.U, ModifierKeys.Control, OpenUrlDialog);
        Add(Key.V, ModifierKeys.Control, PasteAndPlay);
        Add(Key.D, ModifierKeys.Control, ShowDownloads);
        Add(Key.L, ModifierKeys.Control, TogglePlaylistPanel);
        Add(Key.T, ModifierKeys.Control, ToggleOnTop);
        Add(Key.Q, ModifierKeys.Control, RequestClose);

        // 0-9 jump to 0%..90% (YouTube)
        for (int i = 0; i <= 9; i++)
        {
            int pct = i * 10;
            Add(Key.D0 + i, ModifierKeys.None, () => p.SeekPercent(pct));
            Add(Key.NumPad0 + i, ModifierKeys.None, () => p.SeekPercent(pct));
        }
        return map;
    }

    /// <summary>Shortcuts that should work even while a text box has focus.</summary>
    private static bool IsGlobalShortcut(Key key, ModifierKeys mods)
        => (mods & ModifierKeys.Control) != 0 && key is Key.O or Key.U or Key.Q or Key.T or Key.L or Key.D
           || key is Key.F5 or Key.F6 or Key.F11 or Key.MediaPlayPause or Key.MediaNextTrack or Key.MediaPreviousTrack or Key.MediaStop;

    /// <summary>
    /// Command-line control of a running instance (used by scripts/tests): --key=Ctrl+Right, --fullscreen,
    /// --windowed, --player, --page=download|online|settings, --controls, --menu, --seek=SECONDS, --dump-state=FILE, --quit.
    /// </summary>
    public void ExecuteCommandArg(string arg)
    {
        var (name, value) = arg.TrimStart('-').Split('=', 2) is { Length: 2 } kv ? (kv[0], kv[1]) : (arg.TrimStart('-'), "");
        switch (name.ToLowerInvariant())
        {
            case "key":
                var mods = ModifierKeys.None;
                var parts = value.Split('+', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) break;
                foreach (var part in parts[..^1])
                    mods |= part.ToLowerInvariant() switch { "ctrl" => ModifierKeys.Control, "shift" => ModifierKeys.Shift, "alt" => ModifierKeys.Alt, _ => ModifierKeys.None };
                if (parts.Length > 0 && Enum.TryParse<Key>(parts[^1], ignoreCase: true, out var key))
                {
                    _shortcuts ??= BuildShortcuts();
                    if (_shortcuts.TryGetValue((key, mods), out var action)) action();
                }
                break;
            case "fullscreen": EnterFullscreen(); break;
            case "windowed": ExitFullscreen(); break;
            case "player": ShowPlayer(); break;
            case "controls": ShowControls(); break;
            case "menu": ShowContextMenu(null); break;
            case "seek" when double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var sec):
                _player.SeekTo(sec);
                break;
            case "page":
                switch (value.ToLowerInvariant())
                {
                    case "download" or "online": ShowDownloads(); break;
                    case "settings": ShowSettings(); break;
                }
                break;
            case "dump-state" when value.Length > 0:
                DumpState(value);
                break;
            case "quit": RequestClose(); break;
        }
    }

    /// <summary>Writes the player state as JSON (automated tests: --dump-state=C:\path\state.json).</summary>
    private async void DumpState(string file)
    {
        var p = _player;
        var state = new Dictionary<string, object?>
        {
            ["fullscreen"] = _isFullscreen,
            ["idle"] = p.IsIdle,
            ["loading"] = p.IsLoading,
            ["paused"] = p.IsPaused,
            ["path"] = p.CurrentPath,
            ["title"] = p.MediaTitle,
            ["position"] = Math.Round(p.Position, 3),
            ["duration"] = Math.Round(p.Duration, 3),
            ["videoWidth"] = p.VideoWidth,
            ["videoHeight"] = p.VideoHeight,
            ["sourceWidth"] = p.SourceWidth,
            ["sourceHeight"] = p.SourceHeight,
            ["hasVideo"] = p.HasVideo,
            ["hasAlbumArt"] = p.HasAlbumArt,
            ["audioOnly"] = p.IsAudioOnly,
            ["hwdec"] = p.HwdecCurrent,
            ["videoCodec"] = p.GetRaw("video-codec"),
            ["vo"] = p.GetRaw("current-vo"),
            ["audioTracks"] = p.AudioTracks.Count,
            ["subtitleTracks"] = p.SubtitleTracks.Count,
            ["selectedSub"] = p.SubtitleTracks.FirstOrDefault(t => t.IsSelected)?.DisplayName,
            ["chapters"] = p.Chapters.Count,
            ["playlistCount"] = p.PlaylistCount,
            ["playlistPos"] = p.PlaylistPos,
            ["volume"] = p.Volume,
            ["speed"] = p.Speed,
            ["shuffle"] = p.IsShuffled,
            ["loop"] = p.Loop.ToString(),
            ["onTop"] = Topmost,
            ["windowTitle"] = Title,
            ["surfaceMessage"] = SurfaceMessage,
            ["idlePanel"] = IdlePanel.Visibility == Visibility.Visible,
            ["playlistPanel"] = IsPlaylistPanelOpen,
            ["downloadWindow"] = _downloadWindow?.IsVisible == true,
            ["settingsWindow"] = _settingsWindow?.IsVisible == true,
        };
        try
        {
            await File.WriteAllTextAsync(file, System.Text.Json.JsonSerializer.Serialize(state,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        }
        catch (Exception ex) { Services.Log.Warn("dump-state failed: " + ex.Message); }
    }

    public void OpenScreenshotFolder()
    {
        try
        {
            Directory.CreateDirectory(_settings.ScreenshotDir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{_settings.ScreenshotDir}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { Services.Log.Warn("open screenshot folder: " + ex.Message); }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Handled) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.ImeProcessed) key = e.ImeProcessedKey; // Korean IME active: still allow letter shortcuts
        var mods = Keyboard.Modifiers;

        var focused = Keyboard.FocusedElement as DependencyObject;
        bool inText = focused is TextBoxBase or PasswordBox || (focused is ComboBox cb && cb.IsEditable);
        // An open drop-down gets its keys too (Esc closes it, letters search its items) instead of player shortcuts.
        bool inDropDown = focused is ComboBoxItem || focused is ComboBox { IsDropDownOpen: true };
        if ((inText || inDropDown) && !IsGlobalShortcut(key, mods)) return;
        // Esc only leaves fullscreen; otherwise it belongs to the focused control (drop-downs, search boxes, menus).
        if (key == Key.Escape && mods == ModifierKeys.None && !_isFullscreen) return;

        _shortcuts ??= BuildShortcuts();
        if (_shortcuts.TryGetValue((key, mods), out var action))
        {
            // Holding a key repeats seeks/volume but not toggles.
            if (e.IsRepeat && key is not (Key.Left or Key.Right or Key.Up or Key.Down or Key.OemPeriod or Key.OemComma or Key.J or Key.L or Key.C or Key.X))
            {
                e.Handled = true;
                return;
            }
            action();
            e.Handled = true;
            if (key is not (Key.Left or Key.Right or Key.Up or Key.Down)) NoteActivity(showControls: false);
        }
    }

    private void CycleAspect()
    {
        string[] order = ["no", "16:9", "4:3", "2.35:1", "1:1"];
        int i = Array.IndexOf(order, _player.AspectOverride);
        _player.SetAspect(order[(i + 1 + order.Length) % order.Length]);
    }

    private int _playRequests;   // bumped by Play(): a slow paste check must not replace what was opened meanwhile
    private int _pasteSeq;

    private async void PasteAndPlay()
    {
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                Play(Clipboard.GetFileDropList().Cast<string>().ToList());
                return;
            }
            // Trim('"'): Explorer's "경로로 복사" (copy as path) quotes the path.
            var text = Clipboard.GetText()?.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(text) || text.IndexOfAny(['\r', '\n']) >= 0) return;
            if (MediaFormats.IsUrl(text))
            {
                Play([text]);
                return;
            }
            if (!Path.IsPathFullyQualified(text)) return;
            // On a sleeping or offline share File/Directory.Exists wait for the network timeout (20 s and more):
            // check on the thread pool, and drop the result if something else was opened (or pasted) meanwhile.
            int seq = ++_pasteSeq, plays = _playRequests;
            var current = _player.CurrentPath;
            bool exists = await Task.Run(() => File.Exists(text) || Directory.Exists(text));
            if (exists && seq == _pasteSeq && plays == _playRequests && current == _player.CurrentPath && !_closing)
                Play([text]);
        }
        catch { }
    }

    // ---- mouse over the video ----------------------------------------------------

    private void VideoSurface_MouseMove(object sender, MouseEventArgs e)
    {
        var pos = e.GetPosition(VideoSurface);
        // Windows sends synthetic moves (e.g. when windows appear); only react to real movement.
        bool moved = double.IsNaN(_lastMovePos.X) || Math.Abs(pos.X - _lastMovePos.X) > 2 || Math.Abs(pos.Y - _lastMovePos.Y) > 2;
        _lastMovePos = pos;
        if (moved) NoteActivity(showControls: true);

        if (_leftDown && !_dragMoved && e.LeftButton == MouseButtonState.Pressed && !_isFullscreen && WindowState == WindowState.Normal)
        {
            var d = pos - _downPos;
            if (Math.Abs(d.X) > SystemParameters.MinimumHorizontalDragDistance * 2 || Math.Abs(d.Y) > SystemParameters.MinimumVerticalDragDistance * 2)
            {
                _dragMoved = true;
                _leftDown = false;
                HideOverlays();
                try { DragMove(); } catch (InvalidOperationException) { }
                PlaceOverlays();
                ShowControls();
            }
        }
    }

    private void VideoSurface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (MenuJustClosed)
        {
            _leftDown = false;
            e.Handled = true;
            return;
        }
        if (e.ClickCount >= 2)
        {
            _leftDown = false;
            ToggleFullscreen();
            if (_eofClickTimer?.IsEnabled == true)
            {
                // The first click was going to restart a finished video: cancel that instead of undoing it.
                _eofClickTimer.Stop();
            }
            else if (_lastClickPauseTick != 0 && Environment.TickCount64 - _lastClickPauseTick < Win32.GetDoubleClickTime() + 150)
            {
                // Undo the pause toggle performed by the first click of this double-click.
                _player.PlayPause();
            }
            _lastClickPauseTick = 0;
            e.Handled = true;
            return;
        }
        _leftDown = true;
        _dragMoved = false;
        _downPos = e.GetPosition(VideoSurface);
        Keyboard.Focus(this);
    }

    private void VideoSurface_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_leftDown && !_dragMoved && _player.HasMedia && !_player.IsAudioOnly)
        {
            if (_player.EofReached && _player.IsPaused)
            {
                // At the end, a click restarts from the beginning; a seek can't be undone, so wait until it is
                // clear this click is not the first half of a double-click (fullscreen).
                _eofClickTimer ??= CreateEofClickTimer();
                _eofClickTimer.Stop();
                _eofClickTimer.Start();
            }
            else
            {
                _player.PlayPause();
                _lastClickPauseTick = Environment.TickCount64;
            }
        }
        _leftDown = false;
    }

    private System.Windows.Threading.DispatcherTimer? _eofClickTimer;

    private System.Windows.Threading.DispatcherTimer CreateEofClickTimer()
    {
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Win32.GetDoubleClickTime()) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            if (_player.EofReached && _player.IsPaused) _player.PlayPause();
        };
        return t;
    }

    private void VideoSurface_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        ShowContextMenu(placementTarget: null);
        e.Handled = true;
    }

    private void VideoSurface_MouseDown(object sender, MouseButtonEventArgs e)
    {
        switch (e.ChangedButton)
        {
            case MouseButton.Middle:
                _player.ToggleMute();
                e.Handled = true;
                break;
            case MouseButton.XButton1:
                _player.SeekRelative(-10);
                e.Handled = true;
                break;
            case MouseButton.XButton2:
                _player.SeekRelative(10);
                e.Handled = true;
                break;
        }
    }

    private int _wheelRemainder;
    private long _wheelTick;

    /// <summary>Whole wheel notches from a delta; precision touchpads send many small deltas that add up to one notch.</summary>
    internal int WheelSteps(int delta)
    {
        long now = Environment.TickCount64;
        if (now - _wheelTick > 400 || Math.Sign(delta) != Math.Sign(_wheelRemainder)) _wheelRemainder = 0;
        _wheelTick = now;
        _wheelRemainder += delta;
        int steps = _wheelRemainder / 120;
        _wheelRemainder -= steps * 120;
        return steps;
    }

    private void VideoSurface_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        int steps = WheelSteps(e.Delta);
        if (steps == 0) return;
        // Wheel = volume, Ctrl+wheel = seek.
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) _player.SeekRelative(steps * 5);
        else _player.ChangeVolume(steps * 5);
        e.Handled = true;
    }

    private void VideoSurface_MouseLeave(object sender, MouseEventArgs e)
    {
        _lastMovePos = new Point(double.NaN, double.NaN);
    }

    // ---- drag & drop -----------------------------------------------------------

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.UnicodeText)
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Handled) return;
        bool append = (e.KeyStates & DragDropKeyStates.ControlKey) != 0 || (e.KeyStates & DragDropKeyStates.ShiftKey) != 0;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            Activate();
            Play(files, append && _player.HasMedia);
            e.Handled = true;
            return;
        }
        if (e.Data.GetData(DataFormats.UnicodeText) is string text)
        {
            var url = text.Trim().Split('\n')[0].Trim();
            if (MediaFormats.IsUrl(url))
            {
                Activate();
                Play([url], append && _player.HasMedia);
                e.Handled = true;
            }
        }
    }
}

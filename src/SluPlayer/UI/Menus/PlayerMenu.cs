using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SluPlayer.Core.Player;
using SluPlayer.Services.Settings;

namespace SluPlayer.UI.Menus;

/// <summary>Builds the player context menu (right click on video, or a section of it for control-bar buttons).</summary>
public static class PlayerMenu
{
    /// <param name="section">null = full menu; "speed" | "subtitles" | "audio".</param>
    public static ContextMenu Build(MainWindow shell, string? section)
    {
        var p = shell.Player;
        var menu = new ContextMenu();
        var items = menu.Items;

        switch (section)
        {
            case "speed":
                AddSpeedItems(items, p);
                return menu;
            case "subtitles":
                AddSubtitleItems(items, p);
                return menu;
            case "audio":
                AddAudioItems(items, p);
                return menu;
        }

        // ---- full menu --------------------------------------------------------
        items.Add(Sub("열기", "Icon.OpenFile", m =>
        {
            m.Add(Item("파일…", "Icon.OpenFile", () => shell.OpenFilesDialog(), "Ctrl+O"));
            m.Add(Item("폴더…", "Icon.Folder", shell.OpenFolderDialog, "Ctrl+Shift+O"));
            m.Add(Item("URL…", "Icon.Link", shell.OpenUrlDialog, "Ctrl+U"));
            m.Add(Item("자막 파일…", "Icon.Subtitles", shell.OpenSubtitleDialog, enabled: p.HasMedia));
        }));
        items.Add(new Separator());
        items.Add(Item(p.IsPaused ? "재생" : "일시정지", p.IsPaused ? "Icon.Play" : "Icon.Pause", p.PlayPause, "Space", enabled: p.HasMedia || p.PlaylistCount > 0));
        items.Add(Item("정지", "Icon.Stop", p.Stop, "S", enabled: p.HasMedia));
        items.Add(Item("이전", "Icon.Previous", p.Previous, "PgUp", enabled: p.HasMedia));
        items.Add(Item("다음", "Icon.Next", p.Next, "PgDn", enabled: p.HasMedia));
        items.Add(new Separator());
        items.Add(Sub("재생 속도", "Icon.Speed", m => AddSpeedItems(m, p)));
        items.Add(Sub("반복 / 무작위", "Icon.RepeatAll", m => AddLoopItems(m, p)));
        if (p.Chapters.Count > 0) items.Add(Sub("챕터", "Icon.ListView", m => AddChapterItems(m, p)));
        items.Add(new Separator());
        items.Add(Sub("영상", "Icon.Video", m => AddVideoItems(m, shell)));
        items.Add(Sub("오디오", "Icon.Music", m => AddAudioItems(m, p)));
        items.Add(Sub("자막", "Icon.Subtitles", m => AddSubtitleItems(m, p)));
        items.Add(Sub("스크린샷", "Icon.Camera", m =>
        {
            m.Add(Item("저장", "Icon.Camera", p.Screenshot, "Ctrl+E", enabled: p.HasVideo));
            m.Add(Item("폴더 열기", "Icon.FolderOpen", shell.OpenScreenshotFolder));
        }));
        if (p.IsNetworkStream && p.CurrentPath is { } streamUrl)
            items.Add(Item("이 영상 다운로드", "Icon.Download", () => shell.DownloadUrl(streamUrl)));
        items.Add(new Separator());
        items.Add(Check("항상 위", shell.Settings.OnTop != OnTopMode.Never, shell.ToggleOnTop, "Ctrl+T"));
        items.Add(Check("전체 화면", shell.IsFullscreen, shell.ToggleFullscreen, "Enter"));
        items.Add(Check("재생목록", shell.IsPlaylistPanelOpen, shell.TogglePlaylistPanel, "Ctrl+L"));
        items.Add(new Separator());
        items.Add(Item("다운로드", "Icon.Download", shell.ShowDownloads, "Ctrl+D"));
        items.Add(Item("설정", "Icon.Settings", shell.ShowSettings, "F5"));
        return menu;
    }

    // ---- sections -----------------------------------------------------------------

    private static void AddSpeedItems(ItemCollection m, PlayerEngine p)
    {
        foreach (var s in new[] { 0.25, 0.5, 0.75, 1.0, 1.25, 1.5, 1.75, 2.0, 3.0 })
        {
            double v = s;
            m.Add(Check($"{v:0.0#}x", Math.Abs(p.Speed - v) < 0.001, () => p.SetSpeed(v), v == 1.0 ? "Z" : null));
        }
    }

    private static void AddLoopItems(ItemCollection m, PlayerEngine p)
    {
        m.Add(Check("반복 안 함", p.Loop == LoopMode.None, () => p.SetLoop(LoopMode.None)));
        m.Add(Check("한 곡 반복", p.Loop == LoopMode.File, () => p.SetLoop(LoopMode.File)));
        m.Add(Check("전체 반복", p.Loop == LoopMode.Playlist, () => p.SetLoop(LoopMode.Playlist)));
        m.Add(new Separator());
        m.Add(Check("무작위", p.IsShuffled, p.ToggleShuffle));
    }

    private static void AddChapterItems(ItemCollection m, PlayerEngine p)
    {
        foreach (var ch in p.Chapters)
        {
            var c = ch;
            m.Add(Check($"{Core.Formatters.Time(c.Time)}   {c.DisplayName}", c.Index == p.CurrentChapter, () => p.SeekTo(c.Time)));
        }
    }

    private static void AddVideoItems(ItemCollection m, MainWindow shell)
    {
        var p = shell.Player;
        if (p.VideoTracks.Count > 1)
        {
            m.Add(Sub("비디오 트랙", null, s =>
            {
                foreach (var t in p.VideoTracks)
                {
                    var id = t.Id;
                    s.Add(Check(t.DisplayName, t.IsSelected, () => p.SetVideoTrack(id)));
                }
            }));
        }
        m.Add(Sub("화면 비율", "Icon.AspectRatio", s =>
        {
            foreach (var (label, value) in new[] { ("자동", "no"), ("16:9", "16:9"), ("4:3", "4:3"), ("2.35:1", "2.35:1"), ("1.85:1", "1.85:1"), ("1:1", "1:1"), ("9:16", "9:16") })
            {
                var v = value;
                s.Add(Check(label, p.AspectOverride == v, () => p.SetAspect(v)));
            }
        }));
        m.Add(Sub("회전", "Icon.Rotate", s =>
        {
            s.Add(Item("시계 방향 90°", null, () => p.Rotate(90), "Ctrl+R"));
            s.Add(Item("반시계 방향 90°", null, () => p.Rotate(-90), "Ctrl+Shift+R"));
            s.Add(Item("180°", null, () => p.Rotate(180)));
            s.Add(Item("초기화", null, p.ResetRotate));
        }));
        m.Add(Sub("하드웨어 가속", null, s =>
        {
            var cur = shell.Settings.Hwdec;
            foreach (var (label, value) in new[] { ("자동", "auto-safe"), ("D3D11VA", "d3d11va"), ("NVDEC", "nvdec"), ("D3D11VA (복사)", "d3d11va-copy"), ("끄기", "no") })
            {
                var v = value;
                s.Add(Check(label, cur == v, () => { p.SetHwdec(v); shell.SaveSettings(); p.ShowOsd("하드웨어 가속: " + label); }));
            }
            s.Add(new Separator());
            s.Add(new MenuItem { Header = "현재: " + (string.IsNullOrEmpty(p.HwdecCurrent) || p.HwdecCurrent == "no" ? "CPU" : p.HwdecCurrent), IsEnabled = false });
        }));
    }

    private static void AddAudioItems(ItemCollection m, PlayerEngine p)
    {
        if (p.AudioTracks.Count == 0)
        {
            m.Add(new MenuItem { Header = "트랙 없음", IsEnabled = false });
            return;
        }
        foreach (var t in p.AudioTracks)
        {
            var id = t.Id;
            m.Add(Check(t.DisplayName, t.IsSelected, () => p.SetAudioTrack(id)));
        }
    }

    private static void AddSubtitleItems(ItemCollection m, PlayerEngine p)
    {
        m.Add(Check("끄기", p.SelectedSubtitleId == null, () => p.SetSubtitleTrack(null)));
        foreach (var t in p.SubtitleTracks)
        {
            var id = t.Id;
            m.Add(Check(t.DisplayName, t.IsSelected, () => p.SetSubtitleTrack(id)));
        }
        m.Add(new Separator());
        m.Add(Sub("싱크", null, s =>
        {
            s.Add(Item("+0.5초", null, () => p.SetSubDelay(p.SubDelay + 0.5), ">"));
            s.Add(Item("-0.5초", null, () => p.SetSubDelay(p.SubDelay - 0.5), "<"));
            s.Add(Item("+0.1초", null, () => p.SetSubDelay(p.SubDelay + 0.1)));
            s.Add(Item("-0.1초", null, () => p.SetSubDelay(p.SubDelay - 0.1)));
            s.Add(Item($"초기화 ({p.SubDelay * 1000:+0;-0;0} ms)", null, () => p.SetSubDelay(0), "/"));
        }, enabled: p.HasMedia));
    }

    // ---- item helpers -----------------------------------------------------------

    private static MenuItem Item(string header, string? iconKey, Action click, string? gesture = null, bool enabled = true)
    {
        var mi = new MenuItem { Header = header, InputGestureText = gesture ?? "", IsEnabled = enabled, Icon = Icon(iconKey) };
        mi.Click += (_, _) => click();
        return mi;
    }

    private static MenuItem Check(string header, bool isChecked, Action click, string? gesture = null)
    {
        var mi = new MenuItem { Header = header, IsCheckable = false, IsChecked = isChecked, InputGestureText = gesture ?? "" };
        mi.Click += (_, _) => click();
        return mi;
    }

    private static MenuItem Sub(string header, string? iconKey, Action<ItemCollection> fill, bool enabled = true)
    {
        var mi = new MenuItem { Header = header, Icon = Icon(iconKey), IsEnabled = enabled };
        fill(mi.Items);
        if (mi.Items.Count == 0) mi.IsEnabled = false;
        return mi;
    }

    private static object? Icon(string? key)
    {
        if (key == null) return null;
        var glyph = Application.Current.TryFindResource(key) as string;
        if (glyph == null) return null;
        return new TextBlock
        {
            Text = glyph,
            FontFamily = (FontFamily)Application.Current.FindResource("Font.Icons"),
            FontSize = 14,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }
}

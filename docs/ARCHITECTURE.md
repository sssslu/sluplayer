# SluPlayer architecture

SluPlayer is a light Windows video player (5KPlayer-like) with a downloader for links the user pastes.

| Layer | Technology |
|---|---|
| UI | .NET 10 WPF (C#), dark theme, Korean UI |
| Playback | libmpv (`libmpv-2.dll`, mpv 0.41): FFmpeg decoding, `gpu-next` renderer on Direct3D 11, hardware decoding (`hwdec=auto-safe` → d3d11va / nvdec) |
| Downloads | `yt-dlp.exe` (+ `deno.exe` JS runtime for YouTube, `ffmpeg.exe` for merging/conversion) run as subprocesses |
| YouTube sign-in | WebView2, only while the sign-in window is open |

## Windows

- **Main window = the player.** Without media the video area shows an idle panel: icon, one line, [열기] [다운로드] (plus [YouTube 로그인] when an online video failed because YouTube wants a sign-in). Controls float over the video in two overlay windows (title bar, control bar). Everything else is in the right-click menu (`PlayerMenu`).
- **Download and settings windows** are `PageWindow`s: non-modal, one instance each, created on first open; closing only hides them. They host `OnlineView` and `SettingsView` (`UserControl`s built with `IShell`). `IPage.OnNavigatedTo()` runs each time the window is shown; views that are `IDisposable` are disposed at app exit.
- **Dialogs** (`DialogWindow` base): `OpenUrlDialog`, `YouTubeLoginDialog`.

## Source layout (`src/SluPlayer`)

```
App.xaml(.cs)            startup: settings, single instance, crash logging, main window
AppPaths.cs              every file-system location (portable mode: portable.txt next to the exe)
Core/
  Mpv/                   raw libmpv interop: MpvNative (P/Invoke), MpvTypes, MpvNodeConverter, MpvClient (handle + event thread)
  Player/                PlayerEngine (UI-thread facade: observable state + commands), MpvOptionBuilder (every mpv option),
                         PlayerModels, MediaFormats
  Interop/               Win32 P/Invoke, GpuInfo (DXGI adapter selection for hybrid GPUs)
  Formatters.cs          time/bytes formatting
Services/                Log, Settings/ (AppSettings + SettingsService), HistoryService (resume positions), SingleInstance,
                         AppLifetime (exit prompt + shutdown handlers), ToolLocator, FileAssociations, YtDlpCookies, YtDlpErrors
  Downloads/             DownloadManager (queue, persistence in downloads.json), DownloadItem, DownloadOptions (+ yt-dlp
                         arguments), YtDlpService (+ Protocol, Models), YtDlpUpdater, WebViewCookieExport + NetscapeCookies
                         (sign-in cookies), UrlTools, ChildProcessJob (kill-on-close job object for yt-dlp/ffmpeg/deno)
ViewModels/              OnlineViewModel (+ Online/*), SettingsViewModel
UI/
  MainWindow*.cs         the player window: video, idle panel, overlays (.Overlay), input and remote commands (.Input),
                         window state, fullscreen, taskbar buttons (.Window)
  PageWindow.cs          window that hosts the download or the settings view
  IShell.cs              what views and dialogs may ask of the main window
  Controls/              VideoHost (HwndHost child window mpv renders into), SeekBar (custom-drawn)
  Overlay/               transparent non-activating windows over the video (ControlBarWindow, TopBarWindow)
  Menus/PlayerMenu.cs    right-click and control-bar menus
  Views/                 OnlineView, SettingsView, PlaylistPanel, AudioView (Online/, Player/, Settings/ hold helpers)
  Dialogs/               DialogWindow + DialogStyles.xaml, OpenUrlDialog, YouTubeLoginDialog (WebView2)
  Toasts.cs              short notifications inside SluPlayer windows
Themes/                  Colors.xaml, Icons.xaml, Controls.xaml (merged in that order by App.xaml)
```

## Key design decisions

### Startup stays light
- Startup does only what the first frame needs: settings, single instance, main window, the mpv core. No yt-dlp/ffmpeg process, no network, no folder scans, no WebView2. The position timer stops while idle.
- The download window, its view model and `DownloadManager` are created when the window first opens. `DownloadManager` then restores `downloads.json` (unfinished items come back paused) and `YtDlpUpdater.CheckOnceAsync` runs the engine housekeeping and, at most once a day, the yt-dlp update check.
- The theme dictionaries hold only resources that something uses: they are parsed at startup.

### Video embedding and the airspace problem
- mpv renders into a child HWND (`VideoHost`, passed as `wid`): mpv's own D3D11 flip-model swapchain, zero-copy hardware decoding, proper frame pacing.
- WPF cannot draw over a child HWND. Player controls are therefore separate top-level windows (`OverlayWindow`: owned by the main window, `AllowsTransparency`, `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`), positioned in physical pixels over the video and faded in/out.
- Text feedback (volume, seek, track changes) uses mpv's OSD (`show-text`), drawn on the GPU with the video.
- **Input:** mpv's child window is disabled (`EnableWindow(FALSE)`) and `VideoHost` answers `WM_NCHITTEST` with `HTTRANSPARENT`, so mouse input reaches the WPF window. All mouse and keyboard handling is ordinary WPF code in `MainWindow.Input.cs`.
- **Overlay controls are not focusable** (`Focusable=False`); otherwise a click would activate the overlay window and take keyboard focus from the main window.
- There is exactly one `VideoHost`, never re-parented (that would destroy the HWND). `MainWindow.SyncVideoHost()` moves it over the video surface, or over the cover-art slot of `AudioView` for music with album art. It is never hidden, only shrunk to 0×0: mpv calls `ShowWindow(SW_SHOW)` on an invisible window at the next video reconfig, which would activate SluPlayer.

### Threading
- `MpvClient` owns one event thread (`mpv_wait_event`); callbacks run on it.
- `PlayerEngine` marshals everything to the UI thread with `Dispatcher.BeginInvoke`. **Never use `Dispatcher.Invoke` from mpv callbacks**: it can deadlock shutdown.
- `time-pos` is coalesced: the event thread stores the latest value and a 100 ms `DispatcherTimer` publishes `Position`.
- UI commands use the async mpv API (`CommandAsync`, `SetPropertyAsync`), so the UI never blocks on the core.
- Shutdown: `PlayerEngine.ShutdownAsync()` stops the event loop (`mpv_wakeup` + join), then calls `mpv_terminate_destroy` on a worker thread while the UI keeps pumping messages.

### Playback
- **Resume:** an `on_load` hook looks up `HistoryService` and sets `file-local-options/start`. Positions live in `history.json` (mpv's watch-later files are not used).
- **Opening:** `Open`/`OpenMany` scan folders off the UI thread and apply requests in order (`_openQueue`). Lists of more than 16 local files go to mpv as one `loadlist` of a temporary `.m3u8` (`LoadEntries`); one async `loadfile` per entry would overflow mpv's 1000-slot client event queue for big folders. Opening a file unpauses (keep-open leaves the player paused at the end of the last file).
- **Folder auto-playlist:** once a single local file has loaded, its sibling media files are scanned off the UI thread and inserted around it in natural sort order (`loadlist … insert-at 0` / `append`), unless the user opened or queued something else meanwhile.
- **Window auto-fit:** sized on the first frame of a file (`PlayerEngine.MediaReady`, which re-reads `video-params` synchronously because property notifications trail mpv's events).
- **Online streams:** mpv's built-in `ytdl_hook` runs the bundled `yt-dlp.exe` (`script-opts ytdl_hook-ytdl_path`, `ytdl-raw-options js-runtimes=deno:...`), up to 1080p.

### GPU
- `vo=gpu-next`, `gpu-api=d3d11`, `hwdec` from the settings (`auto-safe` by default). `MpvOptionBuilder.EffectiveHwdec` maps NVDEC to `nvdec-copy` (Direct3D 11 has no CUDA interop) and D3D11VA to `d3d11va,d3d11va-copy`.
- On hybrid systems (iGPU + dGPU), `GpuInfo.PreferredAdapterName` asks DXGI 1.6 for the high-performance adapter and passes it as `d3d11-adapter`.
- `target-colorspace-hint=auto`: HDR output on an HDR desktop, tone mapping on SDR.
- A shader cache in `%LOCALAPPDATA%\SluPlayer\shader-cache` speeds up later starts.

### Settings
- `AppSettings` holds only what the settings window and the download window offer, plus state the app remembers itself (window bounds, volume, mute, loop, shuffle, playlist panel, last yt-dlp check). Fixed behaviour is written as constants where it is used, mostly in `MpvOptionBuilder`. Add a setting only together with its UI.
- Unknown properties in `settings.json` (settings removed in later versions) are ignored on load and dropped on the next save.

### YouTube sign-in and cookies
- `YouTubeLoginDialog` (WebView2 with its own profile in `%LOCALAPPDATA%\SluPlayer\webview2`) exports the youtube.com cookies to `%LOCALAPPDATA%\SluPlayer\cookies\youtube-cookies.txt`. That file is the only cookie source (`YtDlpCookies`): every yt-dlp run gets a private copy (`CookieLease`, yt-dlp rewrites its cookie file at exit) and mpv gets its own copy through `ytdl-raw-options`. Sign-out deletes the file and clears the WebView2 profile.
- `YouTubeLoginDialog.LoginChanged` keeps the download window, the settings window and failed downloads (`RetryLoginFailures`) in step, wherever the sign-in started.

### Downloads
- `DownloadManager` (singleton, UI thread) runs up to two yt-dlp processes, parses their `--progress-template` output, and saves the list to `downloads.json`.
- yt-dlp, ffmpeg and deno run in a kill-on-close job object (`ChildProcessJob`), so they never outlive SluPlayer. On exit `AppLifetime` asks before closing while downloads run; running items are paused, saved and come back paused on the next start.

## Conventions
- **UI text is Korean and terse.** Labels and buttons 1–3 words ("열기", "다시 시도"); tooltips are name + shortcut ("재생 (Space)"); status, toasts and OSD are short noun phrases ("다운로드 완료", "속도 1.5x"); errors give a short cause and an optional action ("YouTube 로그인 필요"). No 해요체; a confirmation question may use one short 합쇼체 sentence. No hints, tips or descriptions.
- **Use the theme keys** from `Themes/*.xaml`: `Brush.*`, `Font.*`, `FontSize.*`, `Icon.*`, `Btn.*`, `Toggle.Overlay`, `Slider.*`, `Radio.Chip`, `CheckBox.Switch`, `Card`, `Text.Secondary`. Some keys are looked up from C# by name (`FindResource("Icon.Play")`, `PlayerMenu`, `PageWindow`, `Toasts`, `OnlineConverters`): search the C# code too before removing a key. Don't hard-code colours, except on the video overlays (white on dark).
- **Views** are `UserControl`s constructed with `IShell`. They may implement `IPage.OnNavigatedTo()` and `IDisposable`.
- **Settings** are `SettingsService.Current` (also `IShell.Settings`). Call `IShell.SaveSettings()` after changing them; it is debounced.
- **Logging:** `Services.Log.Info/Warn/Error`. The log file is `%LOCALAPPDATA%\SluPlayer\logs\sluplayer.log`.
- No `Dispatcher.Invoke` from background threads (use `BeginInvoke`/`InvokeAsync`). No blocking I/O on the UI thread.

## Build and run (developer machine)
- .NET SDK 10 is installed per-user. Set `DOTNET_ROOT=%LOCALAPPDATA%\Microsoft\dotnet` and run `%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe build src\SluPlayer\SluPlayer.csproj`.
- Build output goes outside the repository: `%LOCALAPPDATA%\SluPlayerDev\artifacts\bin\SluPlayer\debug_win-x64\`.
- Native engine and tools are copied from `%LOCALAPPDATA%\SluPlayerDev\vendor` (`build\fetch-vendor.ps1`).
- **Remote control of a running instance** (tests, scripts): `SluPlayer.exe --key=Ctrl+Right`, `--fullscreen`, `--windowed`, `--player`, `--page=download|settings` (`online` = `download`), `--controls`, `--menu`, `--seek=12.5`, `--dump-state=<file.json>`, `--quit`. Add `--noactivate` so SluPlayer never takes the focus (windows appear without activation, prompts are skipped). Files given on the command line open in the running instance (forwarded with absolute paths; forwards that arrive while the first instance is still starting are queued, not lost).
- **Release:** `build\publish.ps1 [-Installer]` (see README): self-contained single-file exe + native DLLs, `tools\`, `licenses\`, zip and per-user installer in `dist\`. Every build is self-contained (`SelfContained=true` in the csproj), so no exe ever asks to install .NET.
- **Isolated test copies:** put an empty `portable.txt` next to the exe. Data then lives in `<exe>\data`, and single-instance is scoped to that folder.

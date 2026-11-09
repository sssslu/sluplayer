using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using SluPlayer.Services;
using SluPlayer.Services.Downloads;

namespace SluPlayer.UI.Dialogs;

/// <summary>
/// In-app sign-in for a <see cref="LoginSite"/> (YouTube, X). YouTube may refuse anonymous requests from some networks
/// ("Sign in to confirm you're not a bot"), X shows sensitive and protected posts only to accounts, and Chrome/Edge
/// cookie import is broken on Windows (App-Bound Encryption), so the user signs in here (WebView2, one profile per
/// site) and the session cookies are exported to a Netscape cookies.txt that yt-dlp — downloads and mpv streaming — uses.
/// </summary>
public sealed class LoginDialog : DialogWindow
{
    private const string RuntimeDownloadUrl = "https://developer.microsoft.com/microsoft-edge/webview2/";

    private readonly IShell _shell;
    private readonly LoginSite _site;
    private readonly WebView2 _web;
    private readonly Grid _content;
    private readonly TextBlock _status;
    private readonly TextBlock _loading;
    private readonly Button _doneButton;
    private readonly TaskCompletionSource<bool> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly DispatcherTimer _poll;
    private CoreWebView2Environment? _env;
    private bool _exporting;
    private bool _exported;
    private bool _closed;

    private LoginDialog(IShell shell, LoginSite site)
    {
        _shell = shell;
        _site = site;
        Title = site.Name + " 로그인";
        DialogIcon = (string)FindResource(site == LoginSite.YouTube ? "Icon.Youtube" : "Icon.Account");
        SizeToContent = SizeToContent.Manual;
        ResizeMode = ResizeMode.CanResize;
        Width = 1000;
        Height = 720;
        MinWidth = 620;
        MinHeight = 460;
        Padding = new Thickness(0);
        AutomationProperties.SetAutomationId(this, site.Name + "LoginDialog");

        Brush B(string key) => (Brush)FindResource(key);

        // ---- status bar: state + manual "done" for when the automatic detection misses the sign-in ----
        _doneButton = new Button
        {
            Content = "로그인 완료",
            Style = (Style)FindResource("Btn.Secondary"),
            MinHeight = 30,
            Padding = new Thickness(14, 4, 14, 4),
            Margin = new Thickness(16, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetAutomationId(_doneButton, "LoginDoneButton");
        _doneButton.Click += async (_, _) => await ExportAsync(automatic: false);

        _status = new TextBlock
        {
            Text = "로그인 페이지 여는 중…",
            Foreground = B("Brush.TextSecondary"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        AutomationProperties.SetAutomationId(_status, "LoginStatus");
        var statusDock = new DockPanel();
        DockPanel.SetDock(_doneButton, Dock.Right);
        statusDock.Children.Add(_doneButton);
        statusDock.Children.Add(_status);
        var statusBar = new Border
        {
            Background = B("Brush.Surface1"),
            BorderBrush = B("Brush.Border"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(18, 8, 14, 8),
            Child = statusDock,
        };

        // ---- browser ----
        _web = NewBrowser();
        AutomationProperties.SetAutomationId(_web, "LoginBrowser");
        _web.Visibility = Visibility.Hidden;
        _loading = new TextBlock
        {
            Text = "불러오는 중…",
            Foreground = B("Brush.TextSecondary"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = (double)FindResource("FontSize.BodyLarge"),
        };
        _content = new Grid { Background = B("Brush.Bg") };
        _content.Children.Add(_loading);
        _content.Children.Add(_web);

        var root = new DockPanel();
        DockPanel.SetDock(statusBar, Dock.Bottom);
        root.Children.Add(statusBar);
        root.Children.Add(_content);
        Content = root;

        _poll = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        _poll.Tick += async (_, _) => await CheckSessionAsync();

        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    /// <summary>
    /// Opens the sign-in window of <paramref name="site"/> (one per site; modeless). <paramref name="owner"/> is the
    /// window it belongs to (the download window, or the player when null). The task completes with true after the
    /// cookies were exported, false when the window was closed without signing in.
    /// </summary>
    public static Task<bool> ShowAsync(IShell shell, LoginSite site, Window? owner = null)
    {
        var existing = Open(site);
        if (existing != null)
        {
            if (!NoActivate) existing.Activate();
            return existing._result.Task;
        }
        var dlg = new LoginDialog(shell, site) { Owner = owner ?? shell.Window, ShowActivated = !NoActivate };
        dlg.Show();
        return dlg._result.Task;
    }

    private static LoginDialog? Open(LoginSite site)
        => Application.Current?.Windows.OfType<LoginDialog>().FirstOrDefault(d => d._site == site);

    /// <summary>Started by scripts/tests with --noactivate: never take the focus.</summary>
    private static bool NoActivate
        => Environment.GetCommandLineArgs().Any(a => a.Equals("--noactivate", StringComparison.OrdinalIgnoreCase));

    private static CoreWebView2EnvironmentOptions EnvironmentOptions() => new() { Language = "ko-KR" };

    private static WebView2 NewBrowser() => new() { DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 14, 16, 20) };

    private static void Configure(CoreWebView2 core)
    {
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_site.WebView2DataDir);
            _env = await CoreWebView2Environment.CreateAsync(null, _site.WebView2DataDir, EnvironmentOptions());
            if (_closed) return;
            await _web.EnsureCoreWebView2Async(_env);
            if (_closed) return;
            var core = _web.CoreWebView2;
            Configure(core);
            core.NewWindowRequested += (_, a) =>
            {
                if (_site.RealPopups)
                {
                    OpenPopup(a);
                    return;
                }
                // Keep Google's pop-ups (help, account chooser) inside this window.
                a.Handled = true;
                core.Navigate(a.Uri);
            };
            core.NavigationCompleted += async (_, a) =>
            {
                _loading.Visibility = Visibility.Collapsed;
                _web.Visibility = Visibility.Visible;
                if (!a.IsSuccess && a.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
                    _status.Text = "페이지 로드 실패: 네트워크 오류";
                else if (!_exporting && !_exported)
                    _status.Text = _site.SignInPrompt;
                await CheckSessionAsync();
            };
            core.SourceChanged += async (_, _) => await CheckSessionAsync();
            core.ProcessFailed += (_, a) =>
            {
                Log.Warn($"WebView2 process failed: {a.ProcessFailedKind}");
                _status.Text = "로그인 창 오류. 창을 닫고 다시 열기";
            };
            core.Navigate(_site.SignInUrl);
            _poll.Start();
        }
        catch (WebView2RuntimeNotFoundException ex)
        {
            Log.Warn("WebView2 runtime missing: " + ex.Message);
            ShowProblem("WebView2 런타임 필요", "설치 페이지 열기", () => OpenUrl(RuntimeDownloadUrl));
        }
        catch (Exception ex)
        {
            Log.Error("could not start the sign-in browser", ex);
            ShowProblem("로그인 창 열기 실패: " + ex.Message, null, null);
        }
    }

    /// <summary>
    /// A window the page opens ("Google로 로그인" on X) gets its own small window with the same profile: the provider
    /// reports the sign-in back to its opener, which a page opened in place would no longer have.
    /// </summary>
    private async void OpenPopup(CoreWebView2NewWindowRequestedEventArgs a)
    {
        var deferral = a.GetDeferral();
        a.Handled = true;
        var features = a.WindowFeatures;
        bool sized = features is { HasSize: true, Width: > 0, Height: > 0 };
        var web = NewBrowser();
        var popup = new DialogWindow
        {
            Title = Title,
            DialogIcon = DialogIcon,
            Owner = this,
            Content = web,
            SizeToContent = SizeToContent.Manual,
            ResizeMode = ResizeMode.CanResize,
            Padding = new Thickness(0),
            // The size the page asked for, plus the title bar.
            Width = sized ? Math.Clamp((double)features!.Width, 400, 1000) : 520,
            Height = sized ? Math.Clamp((double)features!.Height + 44, 400, 900) : 700,
            MinWidth = 360,
            MinHeight = 360,
            ShowActivated = !NoActivate,
        };
        popup.Closed += (_, _) =>
        {
            try { web.Dispose(); } catch { }
        };
        try
        {
            popup.Show();
            await web.EnsureCoreWebView2Async(_env);
            var core = web.CoreWebView2;
            Configure(core);
            core.WindowCloseRequested += (_, _) => popup.Close();
            core.NewWindowRequested += (_, b) =>
            {
                b.Handled = true;
                core.Navigate(b.Uri);
            };
            a.NewWindow = core;
        }
        catch (Exception ex)
        {
            // Closed before it was ready, or the browser failed: the page gets no window.
            Log.Warn("sign-in pop-up failed: " + ex.Message);
            try { popup.Close(); } catch { }
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void ShowProblem(string message, string? actionText, Action? action)
    {
        _loading.Visibility = Visibility.Collapsed;
        _web.Visibility = Visibility.Collapsed;
        _doneButton.IsEnabled = false;
        _status.Text = message;
        var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 460 };
        panel.Children.Add(new TextBlock
        {
            Text = (string)FindResource("Icon.Warning"),
            FontFamily = (FontFamily)FindResource("Font.Icons"),
            FontSize = 40,
            Foreground = (Brush)FindResource("Brush.Warning"),
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        panel.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 16, 0, 0),
            FontSize = (double)FindResource("FontSize.BodyLarge"),
        });
        if (actionText != null && action != null)
        {
            var b = new Button { Content = actionText, Style = (Style)FindResource("Btn.Primary"), Margin = new Thickness(0, 18, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
            b.Click += (_, _) => action();
            panel.Children.Add(b);
        }
        _content.Children.Add(panel);
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn("could not open browser: " + ex.Message); }
    }

    // =====================================================================
    // Session detection + export
    // =====================================================================

    private async Task CheckSessionAsync()
    {
        if (_exported || _exporting || _closed) return;
        var core = _web.CoreWebView2;
        if (core == null) return;
        // Only once the sign-in is back on the site itself (YouTube's session cookies are set on the way back from Google).
        if (!Uri.TryCreate(core.Source, UriKind.Absolute, out var u) || !_site.IsSiteHost(u.Host)) return;
        try
        {
            var cookies = await WebViewCookieExport.CollectAsync(core.CookieManager, _site.CookieUris);
            if (_site.HasSession(cookies)) await ExportAsync(automatic: true);
        }
        catch (Exception ex)
        {
            Log.Warn("cookie check failed: " + ex.Message);
        }
    }

    private async Task ExportAsync(bool automatic)
    {
        if (_exported || _exporting || _closed) return;
        var core = _web.CoreWebView2;
        if (core == null) return;
        _exporting = true;
        try
        {
            _status.Text = "로그인 정보 저장 중…";
            // Leave the site's pages first: their scripts may change the session cookies after we copied them.
            await NavigateAndWaitAsync(core, _site.ParkUrl, TimeSpan.FromSeconds(6));
            var cookies = await WebViewCookieExport.CollectAsync(core.CookieManager, _site.CookieUris);
            if (!_site.HasSession(cookies))
            {
                _status.Text = automatic ? _site.SignInPrompt : "아직 로그인 안 됨";
                if (!automatic) core.Navigate(_site.SignInUrl);
                return;
            }
            // Only the site's own cookies go into the plaintext file (yt-dlp sends nothing else to it).
            var own = cookies.Where(_site.IsSiteCookie).ToList();
            await Task.Run(() => NetscapeCookies.WriteFile(_site.CookiesFile, own));
            _exported = true;
            Log.Info($"{_site.Name} sign-in: exported {own.Count} cookies to {_site.CookiesFile}");

            _shell.Player.RefreshYtdlCookies();
            LoginChanged?.Invoke(_site, true);
            _result.TrySetResult(true);
            Close();
        }
        catch (Exception ex)
        {
            Log.Error($"{_site.Name} cookie export failed", ex);
            _status.Text = "로그인 정보 저장 실패: " + ex.Message;
        }
        finally
        {
            _exporting = false;
        }
    }

    private static async Task NavigateAndWaitAsync(CoreWebView2 core, string url, TimeSpan timeout)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? s, CoreWebView2NavigationCompletedEventArgs e) => done.TrySetResult();
        core.NavigationCompleted += Handler;
        try
        {
            core.Navigate(url);
            await Task.WhenAny(done.Task, Task.Delay(timeout));
        }
        finally
        {
            core.NavigationCompleted -= Handler;
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _closed = true;
        _poll.Stop();
        _result.TrySetResult(_exported);
        try { _web.Dispose(); } catch { }
    }

    // =====================================================================
    // Sign-out
    // =====================================================================

    /// <summary>
    /// Raised on the UI thread after a sign-in was saved (true) or after sign-out (false), wherever it started, so every
    /// window shows the same state.
    /// </summary>
    public static event Action<LoginSite, bool>? LoginChanged;

    /// <summary>
    /// "로그아웃": deletes the site's exported cookies and clears its WebView2 profile (cookies + site data), then makes
    /// mpv's streaming forget the cookies.
    /// </summary>
    public static async Task SignOutAsync(IShell shell, LoginSite site)
    {
        try
        {
            if (File.Exists(site.CookiesFile)) File.Delete(site.CookiesFile);
        }
        catch (Exception ex)
        {
            Log.Warn($"could not delete the {site.Name} cookies file: " + ex.Message);
        }

        var open = Open(site);
        if (open?._web.CoreWebView2 is { } core)
        {
            core.CookieManager.DeleteAllCookies();
            open.Close();
        }
        else if (Directory.Exists(site.WebView2DataDir))
        {
            await ClearProfileHeadlessAsync(site.WebView2DataDir);
        }
        shell.Player.RefreshYtdlCookies();
        Log.Info($"{site.Name} sign-out done");
        LoginChanged?.Invoke(site, false);
    }

    /// <summary>Clears a sign-in profile through an invisible WebView2 controller (no window is shown).</summary>
    private static async Task ClearProfileHeadlessAsync(string dataDir)
    {
        HwndSource? host = null;
        CoreWebView2Controller? controller = null;
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, dataDir, EnvironmentOptions());
            host = new HwndSource(new HwndSourceParameters("SluPlayerWebViewSignOut")
            {
                Width = 1,
                Height = 1,
                WindowStyle = unchecked((int)0x80000000),    // WS_POPUP without WS_VISIBLE: never shown
                ExtendedWindowStyle = 0x08000000 | 0x00000080, // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
            });
            controller = await env.CreateCoreWebView2ControllerAsync(host.Handle);
            controller.IsVisible = false;
            controller.CoreWebView2.CookieManager.DeleteAllCookies();
            await controller.CoreWebView2.Profile.ClearBrowsingDataAsync();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            // No runtime: nothing could have been stored by it.
        }
        catch (Exception ex)
        {
            Log.Warn("could not clear the WebView2 sign-in profile: " + ex.Message);
        }
        finally
        {
            try { controller?.Close(); } catch { }
            host?.Dispose();
        }
    }
}

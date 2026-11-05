using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;

namespace SluPlayer.Setup
{
    /// <summary>Install / update / uninstall UI. All work runs on a background task; the UI only shows progress.</summary>
    public partial class SetupWindow : Window
    {
        private enum Phase { Options, Busy, Done, Failed }

        private readonly SetupOptions _options;
        private readonly Payload _payload;          // install mode
        private readonly ExistingInstall _existing; // install mode
        private readonly Manifest _manifest;        // uninstall mode
        private readonly string _uninstallDir;      // uninstall mode
        private readonly bool _uninstall;
        private long _payloadBytes;
        private Phase _phase = Phase.Options;
        private bool _isUpdate;

        /// <summary>0 = done, 1 = cancelled or failed.</summary>
        public int ExitCode { get; private set; } = 1;

        /// <summary>Set after a successful install with "launch" checked: the app to start once the window is gone.</summary>
        public string LaunchAfterClose { get; private set; }

        internal SetupWindow(SetupOptions options, Payload payload, ExistingInstall existing)
        {
            InitializeComponent();
            _options = options;
            _payload = payload;
            _existing = existing;

            int cmp = existing == null ? 0 : CompareVersions(Installer.Version, existing.Version);
            bool update = existing != null && cmp > 0;
            _isUpdate = update;
            Title = update ? "SluPlayer 업데이트" : "SluPlayer 설치";
            TitleText.Text = Title;
            SubtitleText.Text = $"버전 {Installer.Version}";
            DirBox.Text = options.InstallDir ?? existing?.Location ?? Installer.DefaultInstallDir;
            LaunchCheck.IsChecked = options.Launch;
            bool hadDesktop = true;
            if (existing != null)
            {
                var m = SafeLoadManifest(existing.Location);
                if (m != null) hadDesktop = m.Shortcuts.Any(s => s.StartsWith(options.DesktopDir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));
                if (cmp > 0)
                {
                    UpdateText.Text = $"설치된 버전 {existing.Version} → {Installer.Version}\n설정과 재생 기록 유지";
                    PrimaryButton.Content = "업데이트";
                }
                else if (cmp == 0)
                {
                    UpdateText.Text = $"같은 버전({existing.Version}) 설치됨\n설정과 재생 기록 유지";
                    PrimaryButton.Content = "다시 설치";
                }
                else
                {
                    UpdateText.Text = $"더 새 버전({existing.Version}) 설치됨. 계속하면 {Installer.Version}(으)로 교체\n설정과 재생 기록 유지";
                }
                UpdateNote.Visibility = Visibility.Visible;
            }
            DesktopCheck.IsChecked = options.Desktop ?? hadDesktop;
            Loaded += async (s, e) =>
            {
                try { _payloadBytes = await Task.Run(() => Installer.PayloadSize(_payload)); }
                catch (Exception ex) { Log.Write("payload size failed: " + ex.Message); }
                UpdateSpaceText();
            };
        }

        internal SetupWindow(SetupOptions options, string installDir, Manifest manifest)
        {
            InitializeComponent();
            _options = options;
            _uninstall = true;
            _uninstallDir = installDir;
            _manifest = manifest;
            Title = "SluPlayer 제거";
            TitleText.Text = Title;
            SubtitleText.Text = string.IsNullOrEmpty(manifest.Version) ? "" : $"버전 {manifest.Version}";
            OptionsPage.Visibility = Visibility.Collapsed;
            UninstallPage.Visibility = Visibility.Visible;
            UninstallDirText.Text = "설치 위치: " + installDir;
            RemoveDataCheck.IsChecked = options.RemoveData;
            PrimaryButton.Content = "제거";
            PrimaryButton.Style = (Style)FindResource("Btn.Danger");
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            try { NativeMethods.UseDarkTitleBar(new WindowInteropHelper(this).Handle); } catch { }
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            // Never leave a half-copied install behind: closing waits until the work is done.
            if (_phase == Phase.Busy) e.Cancel = true;
            base.OnClosing(e);
        }

        private static Manifest SafeLoadManifest(string dir)
        {
            try { return Manifest.Load(dir); } catch { return null; }
        }

        /// <summary>Compares "1.2.3[-suffix]" versions by their numeric part; unknown versions count as older.</summary>
        private static int CompareVersions(string a, string b)
        {
            Version Parse(string s)
            {
                var core = (s ?? "").Split('-', '+')[0];
                return Version.TryParse(core, out var v) ? v : new Version(0, 0);
            }
            return Parse(a).CompareTo(Parse(b));
        }

        private void DirBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => UpdateSpaceText();

        private void UpdateSpaceText()
        {
            if (SpaceText == null) return;
            var parts = new List<string>();
            if (_payloadBytes > 0) parts.Add($"필요 공간 {_payloadBytes / (1024.0 * 1024):N0} MB");
            try
            {
                var root = Path.GetPathRoot(Path.GetFullPath(DirBox.Text.Trim().Trim('"')));
                if (!string.IsNullOrEmpty(root))
                {
                    var drive = new DriveInfo(root);
                    if (drive.IsReady) parts.Add($"{drive.Name.TrimEnd('\\')} 남은 공간 {drive.AvailableFreeSpace / (1024.0 * 1024 * 1024):N1} GB");
                }
            }
            catch { /* half-typed path */ }
            SpaceText.Text = string.Join(" · ", parts);
        }

        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var picked = NativeMethods.PickFolder(new WindowInteropHelper(this).Handle, "설치할 폴더 선택", DirBox.Text);
                if (picked == null) return;
                // A chosen parent folder gets its own "SluPlayer" sub folder unless it already is one.
                if (!string.Equals(Path.GetFileName(picked.TrimEnd('\\')), "SluPlayer", StringComparison.OrdinalIgnoreCase))
                    picked = Path.Combine(picked, "SluPlayer");
                DirBox.Text = picked;
            }
            catch (Exception ex)
            {
                Log.Write("folder picker failed: " + ex);
            }
        }

        private void Secondary_Click(object sender, RoutedEventArgs e)
        {
            if (_phase != Phase.Busy) Close();
        }

        private async void Primary_Click(object sender, RoutedEventArgs e)
        {
            switch (_phase)
            {
                case Phase.Done:
                    Close();
                    return;
                case Phase.Busy:
                    return;
            }
            if (_uninstall) await RunUninstallAsync();
            else await RunInstallAsync();
        }

        private void ShowPage(UIElement page)
        {
            foreach (var p in new UIElement[] { OptionsPage, UninstallPage, ProgressPage, DonePage })
                p.Visibility = p == page ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SetBusy(string text)
        {
            _phase = Phase.Busy;
            ErrorBanner.Visibility = Visibility.Collapsed;
            ProgressText.Text = text;
            ProgressDetail.Text = "";
            Bar.Value = 0;
            ShowPage(ProgressPage);
            PrimaryButton.IsEnabled = false;
            SecondaryButton.IsEnabled = false;
        }

        private void ShowFailed(string message)
        {
            _phase = Phase.Failed;
            ErrorText.Text = message;
            ErrorBanner.Visibility = Visibility.Visible;
            ShowPage(_uninstall ? UninstallPage : OptionsPage);
            PrimaryButton.Content = "다시 시도";
            PrimaryButton.IsEnabled = true;
            SecondaryButton.Content = "닫기";
            SecondaryButton.IsEnabled = true;
        }

        private void ShowDone(string title, string detail)
        {
            _phase = Phase.Done;
            DoneText.Text = title;
            DoneDetail.Text = detail;
            DoneDetail.Visibility = string.IsNullOrEmpty(detail) ? Visibility.Collapsed : Visibility.Visible;
            ShowPage(DonePage);
            PrimaryButton.Style = (Style)FindResource("Btn.Primary");
            PrimaryButton.Content = "닫기";
            PrimaryButton.IsEnabled = true;
            SecondaryButton.Visibility = Visibility.Collapsed;
            PrimaryButton.Focus();
        }

        private Progress<SetupProgress> MakeProgress() => new Progress<SetupProgress>(p =>
        {
            Bar.Value = Math.Max(0, Math.Min(1, p.Fraction));
            ProgressDetail.Text = p.Text;
        });

        private async Task RunInstallAsync()
        {
            string dir;
            try { dir = Installer.ResolveInstallDir(DirBox.Text); }
            catch (SetupException ex) { ShowFailed(ex.Message); return; }
            DirBox.Text = dir;
            bool desktop = DesktopCheck.IsChecked == true;
            bool launch = LaunchCheck.IsChecked == true;
            bool update = _isUpdate;

            SetBusy(update ? "업데이트 중…" : "설치 중…");
            var progress = MakeProgress();
            try
            {
                await Task.Run(() =>
                {
                    Installer.EnsureAppClosed(dir, force: false);
                    Installer.Install(_options, dir, desktop, _payload, progress);
                });
                ExitCode = 0;
                if (launch)
                {
                    LaunchAfterClose = Path.Combine(dir, Installer.AppExe);
                    _phase = Phase.Done;
                    Close();
                    return;
                }
                ShowDone(update ? "업데이트 완료" : "설치 완료", "");
            }
            catch (SetupException ex)
            {
                Log.Write("install failed: " + ex);
                ShowFailed(ex.Message);
            }
            catch (Exception ex)
            {
                Log.Write("install failed: " + ex);
                ShowFailed("설치 실패: " + ex.Message);
            }
        }

        private async Task RunUninstallAsync()
        {
            bool removeData = RemoveDataCheck.IsChecked == true;
            SetBusy("제거 중…");
            var progress = MakeProgress();
            try
            {
                List<string> leftovers = null;
                await Task.Run(() =>
                {
                    Installer.EnsureAppClosed(_uninstallDir, force: false);
                    leftovers = Installer.Uninstall(_uninstallDir, _manifest, removeData, progress);
                });
                ExitCode = 0;
                ShowDone("제거 완료",
                    leftovers.Count == 0 ? "" : "사용 중이라 남은 파일:\n" + string.Join("\n", leftovers.Take(3)));
            }
            catch (SetupException ex)
            {
                Log.Write("uninstall failed: " + ex);
                ShowFailed(ex.Message);
            }
            catch (Exception ex)
            {
                Log.Write("uninstall failed: " + ex);
                ShowFailed("제거 실패: " + ex.Message);
            }
        }
    }
}

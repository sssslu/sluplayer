using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Windows;
using SluPlayer.Services.Settings;

namespace SluPlayer.Services.Downloads;

public enum UpdateOutcomeKind { UpToDate, Updated, Staged, Failed }

public sealed record UpdateOutcome(UpdateOutcomeKind Kind, string? Version, string? Message = null);

/// <summary>
/// Updates the onedir yt-dlp build without the GitHub API (60 requests/hour) and without "yt-dlp -U"
/// (unsupported for onedir builds; and releases before 2026.10.21 must not self-update, see the release notes):
///   1. HEAD github.com/yt-dlp/yt-dlp/releases/latest → tag from the Location header
///   2. download SHA2-256SUMS and yt-dlp_win.zip, verify SHA-256
///   3. extract to tools\yt-dlp.staged, test "--version", then swap it into tools\yt-dlp (ToolLocator prefers that
///      folder while it is newer than the bundled engine) when no yt-dlp from there is running; otherwise the swap
///      happens when downloads finish or the next time the download window opens.
/// Nothing here runs at app startup: <see cref="CheckOnceAsync"/> is called when the download window first opens.
/// </summary>
public static class YtDlpUpdater
{
    private const string Repo = "https://github.com/yt-dlp/yt-dlp";
    private const string AssetName = "yt-dlp_win.zip";

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly HttpClient Http = CreateClient(allowRedirect: true);
    private static readonly HttpClient NoRedirect = CreateClient(allowRedirect: false);
    private static int _checked;

    /// <summary>Raised (on the UI thread) with the yt-dlp.exe now in use after an update was activated.</summary>
    public static event Action<string>? EngineUpdated;

    private static string ToolsDir => AppPaths.UpdatedToolsDir;
    private static string ActiveDir => ToolLocator.UpdatedYtDlpDir;
    private static string StagedDir => ActiveDir + ".staged";
    private static string WorkDir => ActiveDir + ".download";

    private static HttpClient CreateClient(bool allowRedirect)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = allowRedirect,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(20),
        };
        var c = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("SluPlayer/1.0");
        return c;
    }

    // =====================================================================
    // Version info
    // =====================================================================

    /// <summary>Latest release tag ("2026.10.26") from the redirect of /releases/latest.</summary>
    public static async Task<string?> GetLatestVersionAsync(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Head, Repo + "/releases/latest");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        using var resp = await NoRedirect.SendAsync(req, cts.Token).ConfigureAwait(false);
        var location = resp.Headers.Location?.ToString();
        if (location == null) return null;
        var m = Regex.Match(location, @"/releases/tag/([^/?#]+)");
        return m.Success ? Uri.UnescapeDataString(m.Groups[1].Value) : null;
    }

    /// <summary>Numeric, part-wise comparison ("2026.10.26" vs "2026.10.26.1").</summary>
    public static int CompareVersions(string a, string b)
    {
        static long[] Parse(string v) => v.Trim().TrimStart('v', 'V')
            .Split(['.', '-', '+'], StringSplitOptions.RemoveEmptyEntries)
            .Select(x => long.TryParse(x, out var n) ? n : 0).ToArray();
        var pa = Parse(a);
        var pb = Parse(b);
        for (int i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            long x = i < pa.Length ? pa[i] : 0, y = i < pb.Length ? pb[i] : 0;
            if (x != y) return x.CompareTo(y);
        }
        return 0;
    }

    // =====================================================================
    // Update
    // =====================================================================

    /// <summary>Checks GitHub and installs a newer release. Never throws (failures come back as <see cref="UpdateOutcomeKind.Failed"/>).</summary>
    public static async Task<UpdateOutcome> UpdateAsync(IProgress<string>? progress, CancellationToken ct)
    {
        if (!await Gate.WaitAsync(0, ct).ConfigureAwait(false))
            return new UpdateOutcome(UpdateOutcomeKind.Failed, null, "이미 확인 중");
        try
        {
            progress?.Report("최신 버전 확인 중…");
            var installed = await YtDlpService.GetVersionAsync(ct).ConfigureAwait(false);
            var latest = await GetLatestVersionAsync(ct).ConfigureAwait(false);
            if (latest == null)
                return new UpdateOutcome(UpdateOutcomeKind.Failed, installed, "최신 버전 확인 실패");
            Log.Info($"yt-dlp update check: installed {installed ?? "none"}, latest {latest}");
            if (installed != null && CompareVersions(latest, installed) <= 0)
                return new UpdateOutcome(UpdateOutcomeKind.UpToDate, installed);

            if (ReadVersionFile(StagedDir) is { } staged && CompareVersions(staged, latest) == 0)
                return TryActivateStaged() ? new UpdateOutcome(UpdateOutcomeKind.Updated, staged) : new UpdateOutcome(UpdateOutcomeKind.Staged, staged);

            await DownloadAndStageAsync(latest, progress, ct).ConfigureAwait(false);
            return TryActivateStaged()
                ? new UpdateOutcome(UpdateOutcomeKind.Updated, latest)
                : new UpdateOutcome(UpdateOutcomeKind.Staged, latest);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new UpdateOutcome(UpdateOutcomeKind.Failed, null, "업데이트 취소됨");
        }
        catch (Exception ex)
        {
            Log.Error("yt-dlp update failed", ex);
            var msg = ex is HttpRequestException or TaskCanceledException
                ? "네트워크 오류"
                : ex is UpdateException ue ? ue.Message : "업데이트 실패: " + ex.Message;
            return new UpdateOutcome(UpdateOutcomeKind.Failed, null, msg);
        }
        finally
        {
            Gate.Release();
        }
    }

    private sealed class UpdateException(string message) : Exception(message);

    private static async Task DownloadAndStageAsync(string tag, IProgress<string>? progress, CancellationToken ct)
    {
        TryDeleteDir(WorkDir);
        Directory.CreateDirectory(WorkDir);
        var baseUrl = $"{Repo}/releases/download/{Uri.EscapeDataString(tag)}/";

        progress?.Report("검증 정보 받는 중…");
        var sums = await Http.GetStringAsync(baseUrl + "SHA2-256SUMS", ct).ConfigureAwait(false);
        var expected = sums.Split('\n')
            .Select(l => l.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .FirstOrDefault(p => p.Length >= 2 && p[^1].TrimStart('*') == AssetName)?[0];
        if (expected == null || expected.Length != 64)
            throw new UpdateException("검증 정보 읽기 실패");

        var zipPath = Path.Combine(WorkDir, AssetName);
        string actual;
        using (var resp = await Http.GetAsync(baseUrl + AssetName, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            resp.EnsureSuccessStatusCode();
            long? total = resp.Content.Headers.ContentLength;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var dst = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
            var buffer = new byte[1 << 16];
            long done = 0;
            int lastPct = -1;
            int n;
            while ((n = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                hash.AppendData(buffer, 0, n);
                await dst.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                done += n;
                if (total is > 0)
                {
                    int pct = (int)(done * 100 / total.Value);
                    if (pct != lastPct) { lastPct = pct; progress?.Report($"새 엔진 받는 중… {pct}%"); }
                }
            }
            actual = Convert.ToHexString(hash.GetHashAndReset());
        }
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            TryDeleteDir(WorkDir);
            Log.Warn($"yt-dlp update hash mismatch: expected {expected}, got {actual}");
            throw new UpdateException("검증 실패 (SHA-256 불일치)");
        }

        progress?.Report("설치 준비 중…");
        var extractDir = Path.Combine(WorkDir, "x");
        await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, extractDir), ct).ConfigureAwait(false);
        var exe = Path.Combine(extractDir, "yt-dlp.exe");
        if (!File.Exists(exe))
        {
            var nested = Directory.EnumerateFiles(extractDir, "yt-dlp.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (nested == null) throw new UpdateException("업데이트 파일에 yt-dlp.exe 없음");
            extractDir = Path.GetDirectoryName(nested)!;
            exe = nested;
        }

        // The new build must actually start (antivirus quarantine, broken download...).
        var version = await YtDlpService.RunVersionAsync(exe, ct).ConfigureAwait(false);
        if (version == null) throw new UpdateException("새 엔진 실행 실패");
        File.WriteAllText(Path.Combine(extractDir, "sluplayer-version.txt"), version);

        TryDeleteDir(StagedDir);
        Directory.Move(extractDir, StagedDir);
        TryDeleteDir(WorkDir);
        Log.Info($"yt-dlp {version} staged (sha256 {actual[..12]}…)");
    }

    /// <summary>Moves the staged build into place when no yt-dlp from the active folder is running.</summary>
    public static bool TryActivateStaged()
    {
        if (!Directory.Exists(StagedDir)) return false;
        if (IsActiveEngineRunning())
        {
            Log.Info("yt-dlp update staged; will apply when the engine is idle");
            return false;
        }
        string? old = null;
        try
        {
            if (Directory.Exists(ActiveDir))
            {
                old = ActiveDir + ".old-" + DateTime.UtcNow.Ticks;
                Directory.Move(ActiveDir, old);
            }
            try
            {
                Directory.Move(StagedDir, ActiveDir);
            }
            catch
            {
                if (old != null && !Directory.Exists(ActiveDir)) Directory.Move(old, ActiveDir);
                throw;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("could not activate the yt-dlp update yet: " + ex.Message);
            return false;
        }
        if (old != null) TryDeleteDir(old);
        var exe = ToolLocator.YtDlp ?? Path.Combine(ActiveDir, "yt-dlp.exe");
        Log.Info($"yt-dlp update activated: {ReadVersionFile(ActiveDir)} ({exe})");
        PostToUi(() => EngineUpdated?.Invoke(exe));
        return true;
    }

    /// <summary>Called by the download manager when the last download finished.</summary>
    public static void OnDownloadsIdle()
    {
        if (Directory.Exists(StagedDir)) _ = Task.Run(TryActivateStaged);
    }

    /// <summary>
    /// Housekeeping for the download window, once per app run (call it when the window first opens): trims the saved
    /// sign-in cookies, deletes leftovers, applies a staged update, removes a self-updated copy the bundled engine
    /// has caught up with, and (at most once a day) installs a new release.
    /// </summary>
    public static Task CheckOnceAsync(AppSettings s)
    {
        if (Interlocked.Exchange(ref _checked, 1) != 0) return Task.CompletedTask;
        return Task.Run(async () =>
        {
            Log.Info("download window: engine housekeeping");
            WebViewCookieExport.TrimSavedFile();
            CookieLease.CleanupStale();
            try
            {
                CleanupLeftovers();
                if (ReadVersionFile(StagedDir) != null) TryActivateStaged();
                await DropOutdatedCopyAsync().ConfigureAwait(false);

                var last = s.LastYtDlpUpdateCheck;
                if (last != default && DateTime.Now - last < TimeSpan.FromDays(1) && last <= DateTime.Now) return;
                PostToUi(() =>
                {
                    s.LastYtDlpUpdateCheck = DateTime.Now;
                    SettingsService.Save();
                });
                var outcome = await UpdateAsync(null, CancellationToken.None).ConfigureAwait(false);
                if (outcome.Kind == UpdateOutcomeKind.Failed) Log.Warn("background yt-dlp update: " + outcome.Message);
            }
            catch (Exception ex)
            {
                Log.Warn("background yt-dlp update check failed: " + ex.Message);
            }
        });
    }

    /// <summary>
    /// A self-updated copy that is not newer than the bundled engine is no longer used (see ToolLocator.YtDlp):
    /// delete it unless something still runs from it, and only once the bundled engine is proven to start
    /// (an antivirus may have quarantined part of it; the copy is then the only working engine).
    /// </summary>
    private static async Task DropOutdatedCopyAsync()
    {
        var updatedExe = Path.Combine(ActiveDir, "yt-dlp.exe");
        var bundledExe = ToolLocator.YtDlp;
        if (!File.Exists(updatedExe) || string.Equals(bundledExe, updatedExe, StringComparison.OrdinalIgnoreCase)) return;
        if (IsActiveEngineRunning()) return;
        if (bundledExe == null || await YtDlpService.RunVersionAsync(bundledExe).ConfigureAwait(false) == null)
        {
            Log.Warn("the bundled yt-dlp does not start; using the updated copy instead");
            ToolLocator.PreferUpdatedYtDlp = true;
            PostToUi(() => EngineUpdated?.Invoke(updatedExe)); // online playback switches too
            return;
        }
        Log.Info($"the bundled yt-dlp is not older than the updated copy {ToolLocator.YtDlpVersionOf(updatedExe)}; removing the copy");
        var old = ActiveDir + ".old-" + DateTime.UtcNow.Ticks;
        try
        {
            Directory.Move(ActiveDir, old);
        }
        catch (Exception ex)
        {
            Log.Warn("could not remove the outdated yt-dlp copy: " + ex.Message);
            return;
        }
        TryDeleteDir(old);
    }

    private static void CleanupLeftovers()
    {
        try
        {
            if (!Directory.Exists(ToolsDir)) return;
            foreach (var d in Directory.EnumerateDirectories(ToolsDir, "yt-dlp.old-*")) TryDeleteDir(d);
            if (Directory.Exists(WorkDir) && Gate.CurrentCount > 0) TryDeleteDir(WorkDir);
        }
        catch { }
    }

    /// <summary>Is any yt-dlp.exe from the self-updated folder running (downloads, mpv streaming, analysis)?</summary>
    private static bool IsActiveEngineRunning()
    {
        var prefix = Path.GetFullPath(ActiveDir).TrimEnd('\\') + "\\";
        foreach (var p in Process.GetProcessesByName("yt-dlp"))
        {
            try
            {
                var path = p.MainModule?.FileName;
                if (path == null || path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch
            {
                return true; // can't tell: be safe
            }
            finally
            {
                p.Dispose();
            }
        }
        return false;
    }

    private static string? ReadVersionFile(string dir)
    {
        try
        {
            var f = Path.Combine(dir, "sluplayer-version.txt");
            return File.Exists(f) ? File.ReadAllText(f).Trim() : null;
        }
        catch { return null; }
    }

    private static void TryDeleteDir(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex)
        {
            Log.Debug($"could not delete {dir}: {ex.Message}");
        }
    }

    private static void PostToUi(Action a)
    {
        var d = Application.Current?.Dispatcher;
        if (d == null) { a(); return; }
        d.BeginInvoke(() =>
        {
            try { a(); }
            catch (Exception ex) { Log.Warn("update notification failed: " + ex.Message); }
        });
    }
}

using System.Diagnostics;
using System.IO;
using System.Text;

namespace SluPlayer.Services.Downloads;

/// <summary>Paths of the helper tools passed to yt-dlp explicitly (never rely on its PATH/CWD search).</summary>
public sealed record ToolPaths(string YtDlp, string? Deno, string? FfmpegDir)
{
    public string WorkingDirectory => Path.GetDirectoryName(YtDlp) ?? AppPaths.AppDir;
}

/// <summary>
/// Runs yt-dlp: metadata (-J), version, and long-running downloads (<see cref="YtDlpProcess"/>).
/// Every call uses explicit tool paths, UTF-8 pipes and a private cookies copy (see <see cref="CookieLease"/>).
/// </summary>
public static class YtDlpService
{
    public static ToolPaths? Tools
    {
        get
        {
            var exe = ToolLocator.YtDlp;
            return exe == null ? null : new ToolPaths(exe, ToolLocator.Deno, ToolLocator.FfmpegDir);
        }
    }

    /// <summary>Arguments shared by every invocation.</summary>
    public static List<string> CommonArgs(ToolPaths t, CookieLease cookies)
    {
        var a = new List<string> { "--ignore-config", "--no-plugin-dirs", "--color", "never", "--encoding", "utf-8" };
        if (t.Deno != null) { a.Add("--js-runtimes"); a.Add("deno:" + t.Deno); }
        if (t.FfmpegDir != null) { a.Add("--ffmpeg-location"); a.Add(t.FfmpegDir); }
        a.Add("--cache-dir"); a.Add(AppPaths.YtDlpCacheDir);
        a.AddRange(cookies.Args);
        return a;
    }

    public static ProcessStartInfo CreateStartInfo(ToolPaths t, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(t.YtDlp)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            // yt-dlp searches its own folder, then the CWD, then PATH for deno/ffmpeg: never use the download folder.
            WorkingDirectory = t.WorkingDirectory,
        };
        psi.Environment["PYTHONUTF8"] = "1";           // ignored by the official exe; harmless for other builds
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        foreach (var x in args) psi.ArgumentList.Add(x);
        return psi;
    }

    /// <summary>Runs yt-dlp to completion and captures both pipes. Kills the process tree on timeout/cancel.</summary>
    public static async Task<(int ExitCode, string Stdout, string Stderr)> RunCaptureAsync(
        ToolPaths t, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
    {
        using var p = new Process { StartInfo = CreateStartInfo(t, args) };
        var sw = Stopwatch.StartNew();
        p.Start();
        ChildProcessJob.Add(p);
        try { p.StandardInput.Close(); } catch { }
        var outTask = p.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var errTask = p.StandardError.ReadToEndAsync(CancellationToken.None);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeout);
        try
        {
            await p.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            if (ct.IsCancellationRequested) throw;
            throw new YtDlpException(YtDlpError.TimedOut());
        }
        var stdout = await outTask.ConfigureAwait(false);
        var stderr = await errTask.ConfigureAwait(false);
        Log.Info($"yt-dlp {Describe(args)} → exit {p.ExitCode} in {sw.ElapsedMilliseconds} ms ({stdout.Length} chars)");
        if (p.ExitCode != 0 && !string.IsNullOrWhiteSpace(stderr))
            Log.Warn("yt-dlp stderr: " + Truncate(stderr.Trim(), 1500));
        return (p.ExitCode, stdout, stderr);
    }

    // ---- version ------------------------------------------------------------------

    /// <summary>"2026.10.26" of the engine in use (null when it is missing or broken). Reads the version stamp when there
    /// is one; only other builds are started.</summary>
    public static async Task<string?> GetVersionAsync(CancellationToken ct = default)
    {
        var exe = ToolLocator.YtDlp;
        if (exe == null) return null;
        return ToolLocator.YtDlpVersionOf(exe) ?? await RunVersionAsync(exe, ct).ConfigureAwait(false);
    }

    /// <summary>Starts <paramref name="exe"/> with --version: proves that it runs (null when it doesn't).</summary>
    public static async Task<string?> RunVersionAsync(string exe, CancellationToken ct = default)
    {
        try
        {
            var t = new ToolPaths(exe, null, null);
            var (code, stdout, _) = await RunCaptureAsync(t, ["--version"], TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            var v = stdout.Trim().Split('\n').FirstOrDefault()?.Trim();
            return code != 0 || string.IsNullOrEmpty(v) ? null : v;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn("yt-dlp --version failed: " + ex.Message);
            return null;
        }
    }

    // ---- analysis -------------------------------------------------------------------

    /// <summary>
    /// Single video → full info with formats; playlist/channel → flat entries.
    /// <paramref name="wholePlaylist"/> decides "watch?v=…&amp;list=…" links (false = only the video).
    /// </summary>
    public static async Task<AnalysisResult> AnalyzeAsync(string url, bool wholePlaylist, CancellationToken ct)
    {
        var t = Tools ?? throw new YtDlpException(YtDlpError.EngineMissing());
        using var cookies = CookieLease.Acquire();
        var args = CommonArgs(t, cookies);
        args.AddRange(["-J", "--flat-playlist", wholePlaylist ? "--yes-playlist" : "--no-playlist",
            "--playlist-end", "1000", "--", url]);
        var (code, stdout, stderr) = await RunCaptureAsync(t, args, TimeSpan.FromSeconds(150), ct).ConfigureAwait(false);
        var json = stdout.Trim();
        if (code != 0 || json.Length == 0 || json == "null")
            throw new YtDlpException(YtDlpError.From(stderr, url, downloading: false));
        try
        {
            return YtDlpJson.ParseAnalysis(json, url);
        }
        catch (Exception ex)
        {
            Log.Error("could not parse yt-dlp JSON", ex);
            throw new YtDlpException(new YtDlpError(YtErrorKind.Other, "분석 결과 읽기 실패. 엔진 업데이트 필요", false, ex.Message));
        }
    }

    internal static string Describe(IReadOnlyList<string> args)
    {
        // Log the interesting tail (mode flags + URL), not the long tool paths.
        int i = args.ToList().FindIndex(a => a is "-J" or "--version" or "--newline");
        var tail = i >= 0 ? args.Skip(i) : args.TakeLast(6);
        return Truncate(string.Join(' ', tail.Where(a => !a.Contains("%(", StringComparison.Ordinal))), 300);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}

/// <summary>
/// Cookie arguments for one yt-dlp run. yt-dlp rewrites its --cookies file when it exits, so every process gets its
/// own copy: parallel downloads never race on the shared file and a user-supplied cookies.txt is never modified.
/// </summary>
public sealed class CookieLease : IDisposable
{
    private readonly string? _tempFile;

    public IReadOnlyList<string> Args { get; }

    private CookieLease(IReadOnlyList<string> args, string? tempFile)
    {
        Args = args;
        _tempFile = tempFile;
    }

    public static string RunDir => Path.Combine(Path.GetDirectoryName(AppPaths.YouTubeCookiesFile)!, "run");

    /// <summary>A private copy of the sign-in cookies for one yt-dlp run (no arguments when signed out).</summary>
    public static CookieLease Acquire()
    {
        if (!YtDlpCookies.HasYouTubeLogin) return new CookieLease([], null);
        var value = AppPaths.YouTubeCookiesFile;
        try
        {
            Directory.CreateDirectory(RunDir);
            var tmp = Path.Combine(RunDir, Guid.NewGuid().ToString("N") + ".txt");
            File.Copy(value, tmp, overwrite: true);
            // File.Copy keeps the attributes: yt-dlp can't save a read-only (or hidden) copy at exit and then
            // fails with exit code 1, and the copy couldn't be deleted afterwards.
            File.SetAttributes(tmp, FileAttributes.Normal);
            return new CookieLease(["--cookies", tmp], tmp);
        }
        catch (Exception ex)
        {
            Log.Warn("cookie copy failed, using the file directly: " + ex.Message);
            return File.Exists(value) ? new CookieLease(["--cookies", value], null) : new CookieLease([], null);
        }
    }

    public void Dispose()
    {
        if (_tempFile == null) return;
        try { File.Delete(_tempFile); } catch { /* removed by CleanupStale() later */ }
    }

    /// <summary>Deletes copies left behind by a crash (called once per run, see YtDlpUpdater.CheckOnceAsync).</summary>
    public static void CleanupStale()
    {
        try
        {
            if (!Directory.Exists(RunDir)) return;
            foreach (var f in Directory.EnumerateFiles(RunDir, "*.txt"))
            {
                try
                {
                    if (DateTime.UtcNow - File.GetLastWriteTimeUtc(f) > TimeSpan.FromHours(12))
                    {
                        File.SetAttributes(f, FileAttributes.Normal); // read-only copies made by earlier versions
                        File.Delete(f);
                    }
                }
                catch { }
            }
        }
        catch { }
    }
}

/// <summary>
/// A running yt-dlp download. Lines from both pipes are delivered (on a worker thread) in order per pipe;
/// <see cref="Completion"/> finishes after the process exited and both pipes reached EOF.
/// </summary>
public sealed class YtDlpProcess : IDisposable
{
    private readonly Process _p;

    public Task<int> Completion { get; }

    private YtDlpProcess(Process p, Action<string, bool> onLine)
    {
        _p = p;
        var outPump = Pump(p.StandardOutput, false, onLine);
        var errPump = Pump(p.StandardError, true, onLine);
        Completion = WaitAsync(outPump, errPump);
    }

    public static YtDlpProcess Start(ToolPaths t, IReadOnlyList<string> args, Action<string, bool> onLine)
    {
        var p = new Process { StartInfo = YtDlpService.CreateStartInfo(t, args) };
        p.Start();
        ChildProcessJob.Add(p);
        try { p.StandardInput.Close(); } catch { }
        Log.Info($"yt-dlp download started (pid {p.Id}): {YtDlpService.Describe(args)}");
        return new YtDlpProcess(p, onLine);
    }

    private static Task Pump(StreamReader reader, bool isErr, Action<string, bool> onLine) => Task.Run(async () =>
    {
        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
            {
                try { onLine(line, isErr); }
                catch (Exception ex) { Log.Warn("yt-dlp line handler failed: " + ex.Message); }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    });

    private async Task<int> WaitAsync(Task outPump, Task errPump)
    {
        await _p.WaitForExitAsync().ConfigureAwait(false);
        // Kill(entireProcessTree) closes every handle, so the pipes reach EOF quickly; don't hang on a stray child.
        await Task.WhenAny(Task.WhenAll(outPump, errPump), Task.Delay(TimeSpan.FromSeconds(10))).ConfigureAwait(false);
        int code;
        try { code = _p.ExitCode; } catch { code = -1; }
        return code;
    }

    /// <summary>Stops yt-dlp and its children (ffmpeg, deno). Partial files stay, so a rerun resumes.</summary>
    public void Kill()
    {
        try
        {
            if (!_p.HasExited) _p.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            Log.Warn("failed to stop yt-dlp: " + ex.Message);
        }
    }

    public void Dispose() => _p.Dispose();
}

using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using SluPlayer.Services;

namespace SluPlayer.Core.Player;

/// <summary>
/// The frame generation runtime in &lt;app&gt;\framegen: VapourSynth with its own Python (mpv's vapoursynth filter runs
/// framegen.vpy in it) and the RIFE plugin with one model. None of it is loaded until a video uses it.
/// </summary>
public static class FrameGenRuntime
{
    private static string Dir => Path.Combine(AppPaths.AppDir, "framegen");
    // VSScript looks for its Python (python.exe, python3.dll) four folders up: the layout is Python's own.
    private static string VsScript => Path.Combine(Dir, "Lib", "site-packages", "vapoursynth", "vsscript.dll");
    private static string Script => Path.Combine(Dir, "framegen.vpy");
    private static string Plugin => Path.Combine(Dir, "rife", "librife.dll");
    // The plugin reads the model's version from this folder's name.
    private static string Model => Path.Combine(Dir, "rife", "rife-v4.15_lite_ensembleFalse");

    /// <summary>framegen.vpy writes the reason here when RIFE cannot start (no Vulkan GPU, too little memory...).</summary>
    public static string ErrorFile => Path.Combine(AppPaths.LocalDir, "framegen-error.txt");

    public static bool IsAvailable { get; } = new[]
    {
        VsScript, Script, Plugin, Path.Combine(Model, "flownet.bin"), Path.Combine(Model, "flownet.param"),
        Path.Combine(Dir, "python.exe"), Path.Combine(Dir, "python3.dll"),
    }.All(File.Exists);

    /// <summary>
    /// Points mpv at this VSScript (and away from a VapourSynth the user installed). Call before mpv is created: mpv
    /// reads the variable when it builds its first vapoursynth filter, from an environment it may have copied by then.
    /// </summary>
    public static void Register()
    {
        if (!IsAvailable) return;
        const string name = "VSSCRIPT_PATH";
        Environment.SetEnvironmentVariable(name, VsScript);
        // The C runtime libmpv uses (UCRT) keeps its own copy of the environment once something has read it.
        try { UcrtPutEnv(name, VsScript); } catch (Exception ex) { Log.Debug("_wputenv_s: " + ex.Message); }
    }

    [DllImport("ucrtbase.dll", EntryPoint = "_wputenv_s", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    private static extern int UcrtPutEnv(string name, string value);

    private static readonly Lazy<string?> StartError = new(Start);

    /// <summary>
    /// Starts VSScript (Python with the VapourSynth module) the way mpv's filter is about to, once per process and
    /// about half a second the first time. False with the reason when it cannot start: a filter added then would
    /// make mpv drop the video.
    /// </summary>
    public static bool TryStart(out string? error) => (error = StartError.Value) == null;

    private static unsafe string? Start()
    {
        try
        {
            nint lib = NativeLibrary.Load(VsScript);
            var api = (delegate* unmanaged<int, nint>)NativeLibrary.GetExport(lib, "getVSScriptAPI");
            if (api(4 << 16) != 0) return null; // VSScript API 4.0
            var lastError = (delegate* unmanaged<nint>)NativeLibrary.GetExport(lib, "getVSScriptAPILastError");
            return Marshal.PtrToStringUTF8(lastError()) ?? "VSScript did not start";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    // Frames mpv asks for at a time and frames RIFE generates at a time. Measured (RTX 4080 SUPER): 4 and 2 leave the
    // GPU a quarter idle on 1080p and 1440p video and drop frames there; 8 and 4 need a gigabyte more video memory.
    private const int GpuThreads = 3, ConcurrentFrames = 6, BufferedFrames = 4;

    /// <summary>The mpv filter that multiplies the frame rate by <paramref name="factor"/>.</summary>
    /// <param name="maxWidth">Videos larger than this (the screen) are generated at this size.</param>
    public static string Filter(int factor, int maxWidth, int maxHeight)
    {
        var data = string.Join('|', Plugin, Model, factor.ToString(CultureInfo.InvariantCulture),
            GpuThreads.ToString(CultureInfo.InvariantCulture), maxWidth.ToString(CultureInfo.InvariantCulture),
            maxHeight.ToString(CultureInfo.InvariantCulture), ErrorFile);
        return string.Create(CultureInfo.InvariantCulture,
            $"vapoursynth=file={Quote(Script)}:concurrent-frames={ConcurrentFrames}:buffered-frames={BufferedFrames}:user-data={Quote(data)}");
    }

    /// <summary>mpv's length-prefixed quoting (%n%text, n in bytes): the text needs no escaping.</summary>
    private static string Quote(string text) => $"%{Encoding.UTF8.GetByteCount(text)}%{text}";
}

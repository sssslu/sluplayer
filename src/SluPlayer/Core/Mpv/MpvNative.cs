using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SluPlayer.Core.Mpv;

/// <summary>Raw libmpv client API imports. Strings are UTF-8 in both directions.</summary>
internal static unsafe partial class MpvNative
{
    public const string Lib = "libmpv-2.dll";

    /// <summary>
    /// libmpv-2.dll imports vulkan-1.dll at load time. GPU drivers install it in System32; on PCs without one
    /// (virtual machines, no graphics driver) the bundled Khronos loader in &lt;app&gt;\vulkan\ is loaded first, and
    /// Windows then binds libmpv's import to that already-loaded module. Runs before the first libmpv call.
    /// </summary>
    static MpvNative()
    {
        try
        {
            if (NativeLibrary.TryLoad("vulkan-1.dll", typeof(MpvNative).Assembly, DllImportSearchPath.System32, out _))
                return;
            var fallback = System.IO.Path.Combine(AppContext.BaseDirectory, "vulkan", "vulkan-1.dll");
            if (System.IO.File.Exists(fallback) && NativeLibrary.TryLoad(fallback, out _))
                Services.Log.Info("Vulkan loader not found in System32; using the bundled " + fallback);
        }
        catch (Exception ex)
        {
            Services.Log.Warn("Vulkan loader check failed: " + ex.Message);
        }
    }

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint mpv_client_api_version(); // C 'unsigned long' is 32-bit on Windows

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint mpv_error_string(int error);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void mpv_free(nint data);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint mpv_create();

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int mpv_initialize(nint ctx);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void mpv_terminate_destroy(nint ctx);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int mpv_set_option_string(nint ctx, string name, string data);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int mpv_set_option(nint ctx, string name, MpvFormat format, void* data);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int mpv_command_async(nint ctx, ulong replyUserdata, nint* args);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int mpv_set_property_string(nint ctx, string name, string data);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int mpv_set_property_async(nint ctx, ulong replyUserdata, string name, MpvFormat format, void* data);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int mpv_get_property(nint ctx, string name, MpvFormat format, void* data);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint mpv_get_property_string(nint ctx, string name);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int mpv_observe_property(nint ctx, ulong replyUserdata, string name, MpvFormat format);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int mpv_request_log_messages(nint ctx, string minLevel);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial MpvEvent* mpv_wait_event(nint ctx, double timeout);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void mpv_wakeup(nint ctx);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void mpv_free_node_contents(MpvNode* node);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int mpv_hook_add(nint ctx, ulong replyUserdata, string name, int priority);

    [LibraryImport(Lib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int mpv_hook_continue(nint ctx, ulong id);

    // ---- helpers -------------------------------------------------------

    public static string ErrorString(int error)
    {
        var p = mpv_error_string(error);
        return p == 0 ? $"mpv error {error}" : Marshal.PtrToStringUTF8(p) ?? $"mpv error {error}";
    }

    /// <summary>Allocates a NULL-terminated array of UTF-8 strings. Free with <see cref="FreeUtf8Array"/>.</summary>
    public static nint* AllocUtf8Array(ReadOnlySpan<string> args)
    {
        var arr = (nint*)NativeMemory.Alloc((nuint)(args.Length + 1), (nuint)sizeof(nint));
        for (int i = 0; i < args.Length; i++)
            arr[i] = Marshal.StringToCoTaskMemUTF8(args[i]);
        arr[args.Length] = 0;
        return arr;
    }

    public static void FreeUtf8Array(nint* arr)
    {
        if (arr == null) return;
        for (int i = 0; arr[i] != 0; i++)
            Marshal.FreeCoTaskMem(arr[i]);
        NativeMemory.Free(arr);
    }
}

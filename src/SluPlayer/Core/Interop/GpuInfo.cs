using System.Runtime.InteropServices;

namespace SluPlayer.Core;

public sealed record GpuAdapter(string Name, uint VendorId, ulong DedicatedVideoMemory, bool IsSoftware);

/// <summary>
/// DXGI adapter enumeration. On hybrid systems (iGPU + dGPU) we pick the high-performance adapter for mpv's
/// d3d11 renderer so decoding/rendering run on the discrete GPU.
/// </summary>
public static unsafe class GpuInfo
{
    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(in Guid riid, out nint factory);

    private static readonly Guid IID_IDXGIFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
    private static readonly Guid IID_IDXGIFactory6 = new("c1b6694f-ff09-44a9-b03c-77900a0a1d17");
    private static readonly Guid IID_IDXGIAdapter1 = new("29038f61-3839-4626-91fd-086879011a05");

    private const int DXGI_ERROR_NOT_FOUND = unchecked((int)0x887A0002);
    private const uint DXGI_ADAPTER_FLAG_SOFTWARE = 2;
    private const int DXGI_GPU_PREFERENCE_HIGH_PERFORMANCE = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DXGI_ADAPTER_DESC1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public long AdapterLuid;
        public uint Flags;
    }

    private static readonly Lazy<IReadOnlyList<GpuAdapter>> LazyAdapters = new(Enumerate);
    private static readonly Lazy<string?> LazyPreferred = new(FindPreferred);

    public static IReadOnlyList<GpuAdapter> Adapters => LazyAdapters.Value;

    /// <summary>Name to pass to mpv's d3d11-adapter, or null to let mpv use the default adapter.</summary>
    public static string? PreferredAdapterName => LazyPreferred.Value;

    private static IReadOnlyList<GpuAdapter> Enumerate()
    {
        var list = new List<GpuAdapter>();
        nint factory = 0;
        try
        {
            if (CreateDXGIFactory1(IID_IDXGIFactory1, out factory) < 0 || factory == 0) return list;
            var vtbl = *(nint**)factory;
            // IDXGIFactory1::EnumAdapters1 is vtable slot 12.
            var enumAdapters1 = (delegate* unmanaged<nint, uint, nint*, int>)vtbl[12];
            for (uint i = 0; ; i++)
            {
                nint adapter = 0;
                int hr = enumAdapters1(factory, i, &adapter);
                if (hr == DXGI_ERROR_NOT_FOUND || hr < 0 || adapter == 0) break;
                try
                {
                    if (TryGetDesc(adapter, out var desc))
                        list.Add(new GpuAdapter(desc.Description.Trim(), desc.VendorId,
                            desc.DedicatedVideoMemory, (desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) != 0));
                }
                finally { Release(adapter); }
            }
        }
        catch { }
        finally { if (factory != 0) Release(factory); }
        return list;
    }

    private static string? FindPreferred()
    {
        try
        {
            var hw = Adapters.Where(a => !a.IsSoftware && a.VendorId != 0x1414).ToList();
            if (hw.Count < 2) return null; // single GPU: mpv's default is already right

            // Ask DXGI 1.6 for the high-performance adapter (honours Windows graphics settings).
            if (CreateDXGIFactory1(IID_IDXGIFactory1, out var f1) >= 0 && f1 != 0)
            {
                try
                {
                    nint f6 = 0;
                    var qi = (delegate* unmanaged<nint, Guid*, nint*, int>)(*(nint**)f1)[0];
                    var iid6 = IID_IDXGIFactory6;
                    if (qi(f1, &iid6, &f6) >= 0 && f6 != 0)
                    {
                        try
                        {
                            // IDXGIFactory6::EnumAdapterByGpuPreference is vtable slot 29.
                            var byPref = (delegate* unmanaged<nint, uint, int, Guid*, nint*, int>)(*(nint**)f6)[29];
                            nint adapter = 0;
                            var iidA = IID_IDXGIAdapter1;
                            if (byPref(f6, 0, DXGI_GPU_PREFERENCE_HIGH_PERFORMANCE, &iidA, &adapter) >= 0 && adapter != 0)
                            {
                                try
                                {
                                    if (TryGetDesc(adapter, out var d) && (d.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) == 0)
                                        return d.Description.Trim();
                                }
                                finally { Release(adapter); }
                            }
                        }
                        finally { Release(f6); }
                    }
                }
                finally { Release(f1); }
            }
            return hw.OrderByDescending(a => a.DedicatedVideoMemory).First().Name;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryGetDesc(nint adapter, out DXGI_ADAPTER_DESC1 desc)
    {
        desc = default;
        // IDXGIAdapter1::GetDesc1 is vtable slot 10.
        var getDesc1 = (delegate* unmanaged<nint, nint, int>)(*(nint**)adapter)[10];
        int size = Marshal.SizeOf<DXGI_ADAPTER_DESC1>();
        nint buf = Marshal.AllocHGlobal(size);
        try
        {
            if (getDesc1(adapter, buf) < 0) return false;
            desc = Marshal.PtrToStructure<DXGI_ADAPTER_DESC1>(buf);
            return true;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private static void Release(nint unk)
    {
        var release = (delegate* unmanaged<nint, uint>)(*(nint**)unk)[2];
        release(unk);
    }
}

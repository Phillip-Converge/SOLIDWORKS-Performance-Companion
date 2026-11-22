using System.Runtime.InteropServices;

namespace SWCompanion.Core.Gpu;

public enum GpuKind
{
    Dedicated,   // a separate graphics card with its own video memory
    BuiltIn,     // graphics built into the processor, sharing main memory
    Software,    // Windows' fallback that draws with the CPU (no graphics card)
}

/// <param name="Luid">Windows' internal ID for the adapter, used to match performance counters.</param>
/// <param name="Name">Name as the driver reports it, e.g. "NVIDIA GeForce RTX 4070 Laptop GPU".</param>
/// <param name="DedicatedTotal">Video memory on the card itself, in bytes.</param>
/// <param name="SharedTotal">Main memory the adapter may borrow, in bytes.</param>
public sealed record GpuAdapterInfo(long Luid, string Name, GpuKind Kind, ulong DedicatedTotal, ulong SharedTotal);

/// <summary>
/// Lists the PC's graphics adapters by asking DirectX. READ ONLY.
/// </summary>
public static class GpuAdapters
{
    /// <summary>Dedicated memory below this counts as built-in graphics.</summary>
    private const ulong BuiltInBelow = 1024UL * 1024 * 1024;

    public static IReadOnlyList<GpuAdapterInfo> Read()
    {
        var list = new List<GpuAdapterInfo>();
        try
        {
            var iid = typeof(IDXGIFactory1).GUID;
            if (CreateDXGIFactory1(ref iid, out var factory) != 0) return list;
            try
            {
                for (uint i = 0; factory.EnumAdapters1(i, out var adapter) == 0; i++)
                {
                    try
                    {
                        if (adapter.GetDesc1(out var d) != 0) continue;
                        long luid = ((long)d.LuidHigh << 32) | d.LuidLow;
                        ulong dedicated = (ulong)d.DedicatedVideoMemory;
                        var kind = (d.Flags & 2) != 0 ? GpuKind.Software
                                 : dedicated < BuiltInBelow ? GpuKind.BuiltIn
                                 : GpuKind.Dedicated;
                        list.Add(new GpuAdapterInfo(luid, Clean(d.Description), kind, dedicated, (ulong)d.SharedSystemMemory));
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(adapter);
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(factory);
            }
        }
        catch
        {
            // DirectX unavailable: the app will show what the counters alone can tell.
        }
        return list;
    }

    private static string Clean(string name) =>
        name.Replace("(R)", "").Replace("(TM)", "").Replace("  ", " ").Trim();

    // ---- minimal DirectX (DXGI) definitions: only what's needed to read adapter names ----

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IDXGIFactory1 factory);

    [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIFactory1
    {
        void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
        void EnumAdapters(); void MakeWindowAssociation(); void GetWindowAssociation(); void CreateSwapChain(); void CreateSoftwareAdapter();
        [PreserveSig] int EnumAdapters1(uint index, out IDXGIAdapter1 adapter);
    }

    [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter1
    {
        void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
        void EnumOutputs(); void GetDesc(); void CheckInterfaceSupport();
        [PreserveSig] int GetDesc1(out AdapterDesc1 desc);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct AdapterDesc1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId, DeviceId, SubSysId, Revision;
        public UIntPtr DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public uint LuidLow;
        public int LuidHigh;
        public uint Flags;
    }
}

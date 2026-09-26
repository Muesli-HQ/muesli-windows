using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Muesli.Windows.Services;

/// <summary>
/// A display adapter as Windows itself reports it. Vendor ids are the PCI vendor ids
/// from the adapter description; nothing here is inferred from a marketing name.
/// </summary>
public sealed record GpuAdapterInfo(
    string Name,
    uint VendorId,
    uint DeviceId,
    ulong DedicatedVideoMemoryBytes)
{
    public string Vendor => VendorId switch
    {
        0x10DE => "NVIDIA",
        0x1002 or 0x1022 => "AMD",
        0x8086 => "Intel",
        0x1414 => "Microsoft",
        _ => "Other"
    };

    public bool IsSoftwareAdapter => VendorId == 0x1414;

    public string MemoryLabel => DedicatedVideoMemoryBytes >= 1_073_741_824
        ? $"{DedicatedVideoMemoryBytes / 1_073_741_824.0:0.0} GB"
        : $"{DedicatedVideoMemoryBytes / 1_048_576.0:0} MB";

    public override string ToString() => $"{Name} ({Vendor}, {DeviceId:X4}, {MemoryLabel})";
}

/// <summary>
/// Enumerates real display adapters through DXGI. Adapter presence is evidence that a vendor's
/// driver is installed; it is never treated as evidence that an execution provider works.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class GpuInventory
{
    private const int DxgiErrorNotFound = unchecked((int)0x887A0002);
    private const int DxgiErrorFailure = unchecked((int)0x887A0001);

    private static readonly object Gate = new();
    private static IReadOnlyList<GpuAdapterInfo>? _cached;

    public static IReadOnlyList<GpuAdapterInfo> Adapters
    {
        get
        {
            lock (Gate)
            {
                return _cached ??= Enumerate();
            }
        }
    }

    public static bool HasNvidiaAdapter => Adapters.Any(adapter => adapter.VendorId == 0x10DE);
    public static bool HasNonNvidiaGpu => Adapters.Any(adapter => !adapter.IsSoftwareAdapter && adapter.VendorId != 0x10DE);

    /// <summary>True when the NVIDIA kernel-mode driver exports its CUDA entry points.</summary>
    public static bool IsNvidiaCudaDriverPresent()
    {
        try
        {
            return NativeLibrary.TryLoad("nvcuda.dll", out _);
        }
        catch
        {
            return false;
        }
    }

    private static IReadOnlyList<GpuAdapterInfo> Enumerate()
    {
        var result = new List<GpuAdapterInfo>();
        IDXGIFactory1? factory = null;
        try
        {
            var iid = typeof(IDXGIFactory1).GUID;
            if (CreateDXGIFactory1(ref iid, out factory) < 0 || factory is null)
            {
                return result;
            }

            for (uint index = 0; ; index++)
            {
                IDXGIAdapter1? adapter = null;
                try
                {
                    var hr = factory.EnumAdapters1(index, out adapter);
                    if (hr == DxgiErrorNotFound)
                    {
                        break;
                    }

                    if (hr < 0 || adapter is null)
                    {
                        break;
                    }

                    var status = adapter.GetDesc1(out var description);
                    if (status < 0)
                    {
                        continue;
                    }

                    result.Add(new GpuAdapterInfo(
                        description.Description ?? "",
                        description.VendorId,
                        description.DeviceId,
                        description.DedicatedVideoMemory.ToUInt64()));
                }
                catch
                {
                    break;
                }
                finally
                {
                    if (adapter is not null)
                    {
                        Marshal.ReleaseComObject(adapter);
                    }
                }
            }
        }
        catch
        {
            // Adapter enumeration is diagnostics only. A machine where DXGI cannot be reached
            // still gets CPU transcription; it simply reports no adapters.
        }
        finally
        {
            if (factory is not null)
            {
                Marshal.ReleaseComObject(factory);
            }
        }

        return result;
    }

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IDXGIFactory1 factory);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DXGI_ADAPTER_DESC
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public UIntPtr DedicatedVideoMemory;
        public UIntPtr DedicatedSystemMemory;
        public UIntPtr SharedSystemMemory;
        public long AdapterLuid;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DXGI_ADAPTER_DESC1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public UIntPtr DedicatedVideoMemory;
        public UIntPtr DedicatedSystemMemory;
        public UIntPtr SharedSystemMemory;
        public long AdapterLuid;
        public uint Flags;
    }

    // Vtable order matches DXGI exactly: IDXGIObject members, then IDXGIFactory, then
    // IDXGIFactory1. Built-in COM interop dispatches by this order, so reordering is a bug.
    [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIFactory1
    {
        int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
        int SetPrivateDataInterface(ref Guid name, IntPtr unknown);
        int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
        int GetParent(ref Guid riid, out IntPtr parent);
        int EnumAdapters(uint adapterIndex, out IntPtr adapter);
        int MakeWindowAssociation(IntPtr windowHandle, uint flags);
        int GetWindowAssociation(out IntPtr windowHandle);
        int CreateSwapChain(IntPtr device, IntPtr description, out IntPtr swapChain);
        int CreateSoftwareAdapter(IntPtr module, out IntPtr adapter);
        int EnumAdapters1(uint adapterIndex, [MarshalAs(UnmanagedType.Interface)] out IDXGIAdapter1 adapter);
        int IsCurrent();
    }

    [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter1
    {
        int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
        int SetPrivateDataInterface(ref Guid name, IntPtr unknown);
        int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
        int GetParent(ref Guid riid, out IntPtr parent);
        int EnumOutputs(uint output, out IntPtr outputInterface);
        int GetDesc(out DXGI_ADAPTER_DESC description);
        int CheckInterfaceSupport(ref Guid interfaceName, out long umdVersion);
        int GetDesc1(out DXGI_ADAPTER_DESC1 description);
    }
}

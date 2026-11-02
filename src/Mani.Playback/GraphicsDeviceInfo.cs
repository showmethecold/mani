using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Mani.Playback;

// Windows-only, read-only diagnostic adaptation; docs/m1/graphics-device.md.
// Caller holds a live swapchain lease. Never enumerate unrelated adapters or set budgets.
public static class GraphicsDeviceInfo
{
    internal static readonly Guid DeviceId = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
    internal static readonly Guid Adapter1Id = new("29038f61-3839-4626-91fd-086879011a05");
    internal static readonly Guid Adapter3Id = new("645967a4-1392-4310-a798-8053ce3e93fd");
    private static readonly Guid ChainId = new("310d36a0-d2e7-4c0a-aa04-6a9d23b8886a");
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct NativeDescription
    {
        public fixed char Description[128];
        public uint VendorId, DeviceId, SubSysId, Revision;
        public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public uint LuidLow;
        public int LuidHigh;
        public uint Flags;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeMemoryInfo
    {
        public ulong Budget, CurrentUsage, AvailableForReservation, CurrentReservation;
    }
    public sealed record Adapter(string Description, uint VendorId, uint DeviceId, uint SubSysId, uint Revision,
        ulong DedicatedVideoMemory, ulong DedicatedSystemMemory, ulong SharedSystemMemory, string Luid, uint Flags);
    public sealed record Memory(ulong Budget, ulong CurrentUsage, ulong AvailableForReservation, ulong CurrentReservation);
    public sealed record Result(long BeforeQpc, long AfterQpc, long Frequency, string SwapChainIdentity,
        int ProcessId, uint NodeIndex, IReadOnlyDictionary<string, int> HResults, Adapter? Adapter,
        ulong? DriverVersion, Memory? Local, Memory? NonLocal)
    {
        public bool PerformanceAccepted => false;
        public string Scope => "Process-wide DXGI memory accounting on the swapchain adapter, node 0; not component ownership, GPU utilization or power.";
    }
    public static unsafe Result Read(nint swapChain)
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
            throw new PlatformNotSupportedException("Graphics diagnostics require Windows x64.");
        if (swapChain == 0) throw new ArgumentException("A live swapchain is required.", nameof(swapChain));
        long before = Stopwatch.GetTimestamp();
        var hrs = new Dictionary<string, int>();
        nint chain = 0, device = 0, adapter = 0, adapter1 = 0, adapter3 = 0;
        Adapter? description = null;
        ulong? version = null;
        Memory? local = null, nonLocal = null;
        Result Finish() => new(before, Stopwatch.GetTimestamp(), Stopwatch.Frequency, $"0x{swapChain:X}",
            Environment.ProcessId, 0, hrs, description, version, local, nonLocal);
        bool Query(nint source, Guid iid, out nint result, string stage)
        {
            int hr = Marshal.QueryInterface(source, in iid, out result);
            hrs.Add(stage, hr);
            return hr >= 0;
        }
        try
        {
            if (!Query(swapChain, ChainId, out chain, "swapchain")) return Finish();
            Guid deviceId = DeviceId;
            nint value = 0;
            int hr = ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)(*(nint**)chain)[7])(chain, &deviceId, &value);
            hrs.Add("device", hr);
            if (hr < 0) return Finish();
            device = value;
            value = 0;
            hr = ((delegate* unmanaged[Stdcall]<nint, nint*, int>)(*(nint**)device)[7])(device, &value);
            hrs.Add("adapter", hr);
            if (hr < 0) return Finish();
            adapter = value;
            if (Query(adapter, Adapter1Id, out adapter1, "adapter1"))
            {
                NativeDescription desc = default;
                hr = ((delegate* unmanaged[Stdcall]<nint, NativeDescription*, int>)(*(nint**)adapter1)[10])(adapter1, &desc);
                hrs.Add("description", hr);
                if (hr >= 0)
                {
                    int length = 0;
                    while (length < 128 && desc.Description[length] != '\0') length++;
                    description = new(new string(desc.Description, 0, length), desc.VendorId, desc.DeviceId, desc.SubSysId, desc.Revision,
                        desc.DedicatedVideoMemory, desc.DedicatedSystemMemory, desc.SharedSystemMemory,
                        $"{unchecked((uint)desc.LuidHigh):X8}{desc.LuidLow:X8}", desc.Flags);
                }
            }
            // IDXGIDevice is Microsoft's documented driver-version query; not ID3D11Device.
            long driver = 0;
            hr = ((delegate* unmanaged[Stdcall]<nint, Guid*, long*, int>)(*(nint**)adapter)[9])(adapter, &deviceId, &driver);
            hrs.Add("driver", hr);
            if (hr >= 0) version = unchecked((ulong)driver);
            if (Query(adapter, Adapter3Id, out adapter3, "adapter3"))
            {
                Memory? ReadMemory(int segment, string stage)
                {
                    NativeMemoryInfo info = default;
                    int result = ((delegate* unmanaged[Stdcall]<nint, uint, int, NativeMemoryInfo*, int>)(*(nint**)adapter3)[14])
                        (adapter3, 0, segment, &info);
                    hrs.Add(stage, result);
                    return result < 0 ? null : new(info.Budget, info.CurrentUsage, info.AvailableForReservation, info.CurrentReservation);
                }
                local = ReadMemory(0, "local");
                nonLocal = ReadMemory(1, "nonLocal");
            }
            return Finish();
        }
        finally
        {
            if (adapter3 != 0) Marshal.Release(adapter3);
            if (adapter1 != 0) Marshal.Release(adapter1);
            if (adapter != 0) Marshal.Release(adapter);
            if (device != 0) Marshal.Release(device);
            if (chain != 0) Marshal.Release(chain);
        }
    }
}

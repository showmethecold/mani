using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Mani.Playback;

// Read-only DXGI adaptation. Caller owns a live swapchain lease.
// SDK layout: 10.0.26100.0 shared/dxgi.h and dxgi1_3.h.
// No Present, DwmFlush, frame-latency, color, or swapchain-setting changes.
public static class PresentationStatistics
{
    private static readonly Guid SwapChainId = new("310d36a0-d2e7-4c0a-aa04-6a9d23b8886a");
    private static readonly Guid MediaId = new("dd95b90b-f05f-4f6a-bd65-25bfb264bd84");

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeFrame
    {
        public uint PresentCount, PresentRefreshCount, SyncRefreshCount;
        public long SyncQPCTime, SyncGPUTime;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeMedia
    {
        public uint PresentCount, PresentRefreshCount, SyncRefreshCount;
        public long SyncQPCTime, SyncGPUTime;
        public int CompositionMode;
        public uint ApprovedPresentDuration;
    }
    public sealed record Frame(uint PresentCount, uint PresentRefreshCount, uint SyncRefreshCount,
        long SyncQPCTime, long SyncGPUTime, int? CompositionMode = null, uint? ApprovedPresentDuration = null);
    public sealed record ReadResult(long BeforeQpc, long AfterQpc, long Frequency,
        string SwapChainIdentity, int FrameHResult, Frame? FrameStatistics,
        int LastPresentHResult, uint? LastPresentCount, int MediaQueryHResult,
        int? MediaHResult, Frame? MediaStatistics)
    {
        public bool FirstMediaFrameIdentified => false;
        public string Scope => "Raw swapchain statistics; no media-frame correlation or first-presentation latency.";
    }
    public static unsafe ReadResult Read(nint swapChain)
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
            throw new PlatformNotSupportedException("DXGI timing requires Windows x64.");
        if (swapChain == 0) throw new ArgumentException("A live swapchain is required.", nameof(swapChain));
        long before = Stopwatch.GetTimestamp();
        var iid = SwapChainId;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(swapChain, in iid, out var native));
        try
        {
            NativeFrame frame = default;
            var table = *(nint**)native;
            int frameHr = ((delegate* unmanaged[Stdcall]<nint, NativeFrame*, int>)table[16])(native, &frame);
            uint count = 0;
            int countHr = ((delegate* unmanaged[Stdcall]<nint, uint*, int>)table[17])(native, &count);
            var mediaIid = MediaId;
            int mediaQueryHr = Marshal.QueryInterface(native, in mediaIid, out var media);
            int? mediaHr = null;
            Frame? mediaFrame = null;
            if (mediaQueryHr >= 0)
            {
                try
                {
                    NativeMedia value = default;
                    mediaHr = ((delegate* unmanaged[Stdcall]<nint, NativeMedia*, int>)(*(nint**)media)[3])(media, &value);
                    if (mediaHr >= 0) mediaFrame = new(value.PresentCount, value.PresentRefreshCount,
                        value.SyncRefreshCount, value.SyncQPCTime, value.SyncGPUTime,
                        value.CompositionMode, value.ApprovedPresentDuration);
                }
                finally { Marshal.Release(media); }
            }
            return new(before, Stopwatch.GetTimestamp(), Stopwatch.Frequency, $"0x{native:X}",
                frameHr, frameHr < 0 ? null : new(frame.PresentCount, frame.PresentRefreshCount,
                    frame.SyncRefreshCount, frame.SyncQPCTime, frame.SyncGPUTime),
                countHr, countHr < 0 ? null : count, mediaQueryHr, mediaHr, mediaFrame);
        }
        finally { Marshal.Release(native); }
    }

    public sealed record Progress(string FrameStatus, string MediaStatus, string LastPresentStatus)
    {
        public bool FirstMediaFrameIdentified => false;
        public bool PerformanceAccepted => false;
    }

    // Classify availability/progress only. Never derive media latency from a sync-clock sample.
    public static Progress Compare(ReadResult before, ReadResult after)
    {
        bool comparable = before.Frequency > 0 && before.Frequency == after.Frequency &&
            before.SwapChainIdentity == after.SwapChainIdentity &&
            before.BeforeQpc > 0 && before.AfterQpc >= before.BeforeQpc &&
            after.BeforeQpc >= before.AfterQpc && after.AfterQpc >= after.BeforeQpc;
        if (!comparable) return new("incomparable", "incomparable", "incomparable");
        const int disjoint = unchecked((int)0x887A000B);
        string Frames(int? firstHr, Frame? first, int? lastHr, Frame? last)
        {
            if (firstHr == disjoint || lastHr == disjoint) return "disjoint";
            if (firstHr is null or < 0 || lastHr is null or < 0 || first is null || last is null) return "unavailable";
            if (first.SyncQPCTime <= 0 || last.SyncQPCTime <= 0 ||
                first.PresentCount == 0 || last.PresentCount == 0) return "empty";
            if (first.SyncQPCTime > before.AfterQpc || last.SyncQPCTime > after.AfterQpc) return "invalid-clock";
            if (last.PresentCount < first.PresentCount || last.SyncQPCTime < first.SyncQPCTime)
                return "counter-reset"; // Also conservatively excludes counter wrap-around.
            if (last.PresentCount == first.PresentCount && last.SyncQPCTime == first.SyncQPCTime) return "stale";
            return last.PresentCount > first.PresentCount && last.SyncQPCTime > first.SyncQPCTime ? "advanced" : "partial-advance";
        }
        string present = before.LastPresentHResult < 0 || after.LastPresentHResult < 0 ||
            before.LastPresentCount is null || after.LastPresentCount is null ? "unavailable" :
            after.LastPresentCount < before.LastPresentCount ? "counter-reset" :
            after.LastPresentCount == before.LastPresentCount ? "stale" : "advanced";
        return new(Frames(before.FrameHResult, before.FrameStatistics, after.FrameHResult, after.FrameStatistics),
            Frames(before.MediaQueryHResult < 0 ? null : before.MediaHResult, before.MediaStatistics,
                   after.MediaQueryHResult < 0 ? null : after.MediaHResult, after.MediaStatistics), present);
    }


}


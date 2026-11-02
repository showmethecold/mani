// IINA 1.5.1/build 180 PlayerWindowController mouseUp/scrollWheel and AppData.
// Copyright (c) 2016-2026 lhc and contributors. GPL-3.0; licenses/IINA-GPL-3.0.txt.
namespace Mani_App;

internal enum PointerClick { None, PendingSingle, Double }
internal sealed class VideoClickState
{
    private long _last = long.MinValue, _deadline;
    private double _x, _y;
    private int _count, _crossings;
    private bool _inside = true;
    public bool Pending { get; private set; }
    public long MillisecondsUntilExpiration(long now) => Math.Max(1, _deadline - now);
    public PointerClick Release(long now, double x, double y, long interval, double width, double height)
    {
        bool continued = _last != long.MinValue && now >= _last && now - _last <= interval && Math.Abs(x - _x) <= width / 2 && Math.Abs(y - _y) <= height / 2;
        _count = continued ? _count + 1 : 1;
        _last = now; _x = x; _y = y;
        Pending = _count == 1;
        if (Pending) { _deadline = now + interval; _crossings = 0; _inside = true; }
        return _count == 1 ? PointerClick.PendingSingle : _count == 2 ? PointerClick.Double : PointerClick.None;
    }
    public void ObserveInside(bool inside)
    {
        if (_inside == inside) return;
        _inside = inside;
        if (Pending && ++_crossings >= 2) Pending = false;
    }
    public bool Expire(long now)
    {
        if (!Pending || now < _deadline) return false;
        Pending = false;
        return true;
    }
    public void Cancel() { Pending = false; _last = long.MinValue; _count = 0; }
}

internal enum WheelTarget { None, Video, Timeline, Volume }
internal static class WheelZoom
{
    // Windows touchpad compatibility zoom is delivered as Ctrl+vertical wheel.
    // Ten percent per notch is an adapter sensitivity, not an IINA constant.
    public static double? Magnification(WheelTarget target, bool horizontal, bool control, int delta) =>
        target == WheelTarget.Video && control && !horizontal ? delta / 1200d : null;
}
// WinUI can replay the last wheel PointerPoint on pointer reentry with a new
// FrameId but the original Timestamp. A replay is not another physical notch.
internal sealed class WheelEventSequence
{
    private ulong? _last;
    public bool Accept(ulong timestamp)
    {
        if (_last is { } last && timestamp <= last) return false;
        _last = timestamp;
        return true;
    }
}
internal readonly record struct WheelChange(double Seek, double Volume)
{
    public static WheelChange From(WheelTarget target, bool horizontal, int delta)
    {
        double amount = delta / 120d * (horizontal ? 1 : 2);
        return target switch
        {
            WheelTarget.Timeline => new(amount * 2, 0),
            WheelTarget.Volume => new(0, amount),
            WheelTarget.Video when horizontal => new(amount * 2, 0),
            WheelTarget.Video => new(0, amount),
            _ => default
        };
    }
}
internal sealed class PendingWheelInput
{
    private (long Request, long Entry)? _media;
    private WheelChange _change;
    public void Add(long request, long entry, WheelChange change)
    {
        if (_media != (request, entry)) Clear();
        _media = (request, entry);
        _change = new(_change.Seek + change.Seek, _change.Volume + change.Volume);
    }
    public WheelChange Take(long request, long entry)
    {
        var result = _media == (request, entry) ? _change : default;
        Clear(); return result;
    }
    public void Clear() { _media = null; _change = default; }
}

// IINA MainWindowController hideUIAndCursor/createTimer/mouseMoved policy.
// Copyright (c) 2016-2026 lhc and contributors. GPL-3.0; licenses/IINA-GPL-3.0.txt.
namespace Mani_App;

internal sealed class CursorIdleState
{
    private long _deadline, _delay = 2500;
    private bool _eligible;
    private (int X, int Y)? _position;
    public bool Hidden { get; private set; }
    public bool Waiting => _eligible && !Hidden;
    public long Remaining(long now) => Math.Max(1, _deadline - now);
    public void Configure(long delay, long now)
    {
        delay = Math.Clamp(delay, 0, int.MaxValue);
        if (delay == _delay) return;
        _delay = delay; Wake(now);
    }
    public void Eligible(bool value, long now)
    {
        if (_eligible == value) return;
        _eligible = value; Wake(now);
    }
    public void Move(int x, int y, long now)
    {
        if (_position == (x, y)) return;
        _position = (x, y); Wake(now);
    }
    public void Wake(long now) { Hidden = false; _deadline = now + _delay; }
    public void HideNow() { if (_eligible) Hidden = true; }
    public void Tick(long now) { if (_eligible && now >= _deadline) Hidden = true; }
    public void Reset() { _eligible = Hidden = false; _position = null; }
}

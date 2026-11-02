// Ported from IINA 1.5.1/build 180 MainWindowController showUI/hideUI/updateTimer.
// Copyright (c) 2016-2026 lhc and contributors. GPL-3.0; licenses/IINA-GPL-3.0.txt.
namespace Mani_App;

[Flags]
internal enum OscHold { None = 0, Hover = 1, Menu = 2, Capture = 4, Keyboard = 8, Drag = 16, Dialog = 32, Diagnostics = 64, Docked = 128 }

// UI-thread policy with an injected monotonic clock. A reversal starts at the current
// opacity; no old completion callback can collapse a newly shown control bar.
internal sealed class FloatingOscState
{
    internal const long HideDelay = 2500;
    internal const long FadeDuration = 250;
    private long _deadline, _started;
    private double _from, _target;
    private bool _autoHide = true;
    private long _hideDelay = HideDelay;
    public bool Active { get; private set; }
    public OscHold Holds { get; private set; }
    public double Opacity { get; private set; }
    public bool Visible => Active && (Opacity > 0 || _target > 0);
    public bool HitTestVisible => Active && _target > 0;
    public bool Animating => Opacity != _target;
    public bool NeedsTick => Active && (Animating || (_autoHide && _target > 0 && Holds == OscHold.None));
    public long MillisecondsUntilHide(long now) => Math.Max(1, _deadline - now);

    public void Configure(bool autoHide, long delay, long now, bool animate)
    {
        delay = Math.Clamp(delay, 0, int.MaxValue);
        if (_autoHide == autoHide && _hideDelay == delay) return;
        _autoHide = autoHide; _hideDelay = delay;
        Wake(now, animate);
    }

    public void SetActive(bool active, long now, bool animate)
    {
        if (Active == active) return;
        Active = active;
        if (active) Wake(now, animate);
        else { Opacity = _target = _from = 0; Holds = OscHold.None; }
    }
    public void Wake(long now, bool animate)
    {
        if (!Active) return;
        _deadline = now + _hideDelay;
        // A zero timeout expires in this same UI event. Do not reverse an
        // in-progress hide twice on every mouse move: that restarts the fade
        // indefinitely and keeps the native blur updates running. Interaction
        // holds (keyboard, menu, capture, etc.) still reveal the controls.
        Target(_autoHide && _hideDelay == 0 && Holds == OscHold.None ? 0 : 1, now, animate);
    }
    public void Hold(OscHold reason, bool enabled, long now, bool animate)
    {
        var next = enabled ? Holds | reason : Holds & ~reason;
        if (next == Holds) return;
        Holds = next;
        // Releasing the last hold always grants a fresh complete timeout.
        Wake(now, animate);
    }
    public void Leave(long now, bool animate)
    {
        if (_autoHide && Holds == OscHold.None) Target(0, now, animate);
    }
    public void Tick(long now, bool animate)
    {
        Sample(now, animate);
        if (Active && _autoHide && Holds == OscHold.None && now >= _deadline) Target(0, now, animate);
    }
    private void Target(double value, long now, bool animate)
    {
        Sample(now, animate);
        if (_target == value) return;
        _from = Opacity;
        _target = value;
        _started = now;
        if (!animate) Opacity = value;
    }
    private void Sample(long now, bool animate)
    {
        double progress = animate ? Math.Clamp((now - _started) / (double)FadeDuration, 0, 1) : 1;
        // Ease-in/ease-out is the Windows approximation of NSAnimationContext's default curve.
        progress = progress * progress * (3 - 2 * progress);
        Opacity = _from + (_target - _from) * progress;
    }
}

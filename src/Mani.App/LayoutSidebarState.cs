// Ported from IINA 1.5.1/build 180 SidebarController, SideBar and Preference.
// Copyright (c) 2016-2026 lhc and contributors. GPL-3.0; licenses/IINA-GPL-3.0.txt.
namespace Mani_App;

internal enum SidebarPhase { Hidden, Showing, Shown, Hiding }

internal sealed class LayoutSidebarState
{
    public const long Duration = 240;
    private long _started;
    private double _from, _target;
    public double Expansion { get; private set; }
    public SidebarPhase Phase { get; private set; }
    public bool WantsVisible => _target == 1;
    public bool Visible => Phase != SidebarPhase.Hidden;
    public bool Animating => Phase is SidebarPhase.Showing or SidebarPhase.Hiding;
    public bool Toggle(long now, bool animate)
    {
        Tick(now);
        if (Animating) return false; // IINA ignores toggle requests during transition.
        SetVisible(!WantsVisible, now, animate);
        return true;
    }
    public void SetVisible(bool visible, long now, bool animate)
    {
        Tick(now);
        _from = Expansion; _target = visible ? 1 : 0; _started = now;
        Phase = visible ? SidebarPhase.Showing : SidebarPhase.Hiding;
        if (!animate || _from == _target) Finish();
    }
    public void Tick(long now, bool animate = true)
    {
        if (!Animating) return;
        if (!animate) { Finish(); return; }
        double t = Math.Clamp((now - _started) / (double)Duration, 0, 1);
        Expansion = _from + (_target - _from) * t * t;
        if (t >= 1) Finish();
    }
    private void Finish()
    {
        Expansion = _target;
        Phase = WantsVisible ? SidebarPhase.Shown : SidebarPhase.Hidden;
    }
}

internal readonly record struct SidebarPlan(double Width, double OccupiedLeft, double OccupiedRight,
    double VideoLeft, double VideoRight, double Translation, double GripX)
{
    public double Occupied => OccupiedLeft + OccupiedRight;
}

internal static class SidebarLayout
{
    public static double MaximumWidth(double hostWidth) => Math.Min(500, Math.Max(0, hostWidth) * .8);
    public static double ClampWidth(double desired, double hostWidth)
    {
        double max = MaximumWidth(hostWidth);
        return Math.Clamp(desired, Math.Min(300, max), max);
    }
    public static SidebarPlan Create(double hostWidth, double desired, double expansion, bool leading, bool edgeToEdge)
    {
        double width = ClampWidth(desired, hostWidth);
        double occupied = width * Math.Clamp(expansion, 0, 1);
        double left = leading ? occupied : 0, right = leading ? 0 : occupied;
        return new(width, left, right, edgeToEdge ? 0 : left, edgeToEdge ? 0 : right,
            (leading ? -1 : 1) * (width - occupied), leading ? occupied : hostWidth - occupied - 4);
    }
    public static double DragWidth(double x, double hostWidth, bool leading) =>
        ClampWidth((leading ? x : hostWidth - x) - 2, hostWidth);
    public static int StoredWidth(double width) => (int)Math.Clamp(Math.Round(width), 300, 500);
}

// Ported from IINA 1.5.1/build 180 Preference.isDocked, MainWindowController
// setupVideoContainerConstraints and fadeable registration. Copyright (c) 2016-2026
// lhc and contributors. GPL-3.0; licenses/IINA-GPL-3.0.txt.
using Mani.Playback;

namespace Mani_App;

internal readonly record struct OscDockingPlan(bool Docked, bool AlwaysShown, double Top, double Bottom);

internal static class OscDockingLayout
{
    public static OscDockingPlan Create(OscPosition position, bool edgeToEdge, bool dockControls,
        bool hasMedia, bool fullscreen, double surfaceHeight, double controlHeight)
    {
        if (!hasMedia || edgeToEdge || !dockControls || position == OscPosition.Floating) return default;
        // Keep a nonnegative viewport during transient measurement or a tiny host.
        double inset = Math.Clamp(controlHeight, 0, Math.Max(0, surfaceHeight - 1));
        bool top = position == OscPosition.Top;
        // IINA retains the constraint even while its fullscreen top bar fades.
        return new(true, !top || !fullscreen, top ? inset : 0, top ? 0 : inset);
    }
}

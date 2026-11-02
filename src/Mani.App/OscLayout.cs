// Adapted from IINA 1.5.1/build 180 setupOnScreenController, Titlebar and OSCBottomView.
// Copyright (c) 2016-2026 lhc and contributors. GPL-3.0; licenses/IINA-GPL-3.0.txt.
using Mani.Playback;

namespace Mani_App;

internal readonly record struct OscInsets(double Left, double Top, double Right, double Bottom);
internal readonly record struct OscLayoutPlan(OscBounds Bounds, OscInsets Padding, bool Floating,
    bool VolumeGroup, bool VolumeSlider, bool SeekButtons, bool FullscreenButton, bool TimeLabels);

internal static class OscLayout
{
    // Fullscreen + PiP + settings + More at normal widths. More preserves every command
    // when native Windows hit targets require the direct tools to detach.
    public static bool DirectTools(OscLayoutPlan plan) => plan.Floating ? plan.Bounds.Width >= 440 : plan.Bounds.Width >= 680;
    public static OscLayoutPlan Create(OscPosition position, double width, double height, double measuredHeight,
        double horizontal, double bottom, bool fullscreen)
    {
        if (position == OscPosition.Floating)
        {
            var bounds = FloatingOscLayout.Place(width, height, measuredHeight, horizontal, bottom);
            return new(bounds, new(8, 0, 8, 8), true, true, bounds.Width >= 400,
                bounds.Width >= 320, bounds.Width >= 280, true);
        }
        width = Math.Max(0, width); height = Math.Max(0, height);
        double barHeight = Math.Min(Math.Max(0, measuredHeight), height);
        var padding = position == OscPosition.Top ? new OscInsets(6, fullscreen ? 8 : 0, 6, 10) : new(16, 12, 12, 12);
        var edge = new OscBounds(0, position == OscPosition.Bottom ? height - barHeight : 0, width, barHeight);
        // Native control budgets: play=3*32+2*16, time/slider=160, volume=32+4+70,
        // toolbar=32+4+32. Keep the timeline; detach toolbar before volume as in IINA.
        double required = padding.Left + padding.Right + 3 * 8 + 128 + 160 + 106 + 68;
        bool tools = width >= required;
        if (!tools) required -= 36; // More remains the accessible mode/menu entry.
        bool volume = width >= required;
        if (!volume) required -= 106 + 8;
        bool seek = width >= required;
        if (!seek) required -= 96;
        bool labels = width >= required;
        return new(edge, padding, false, volume, volume, seek, tools, labels);
    }
}

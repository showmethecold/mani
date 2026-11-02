// IINA 1.5.1/build 180 MainWindowController PIP aspect/return rules and
// VideoPIPViewController, adapted to a resizable Windows system-caption window.
// Copyright (c) 2016-2026 lhc and contributors. GPL-3.0; licenses/IINA-GPL-3.0.txt.
namespace Mani_App;

internal static class PictureInPictureLayout
{
    public static bool ShouldReturn(PlayerSurface surface) => surface is
        PlayerSurface.Welcome or PlayerSurface.Stopped or PlayerSurface.Failed;

    public static WindowFrame Initial(VideoSize video, WindowFrame work, VideoSize chrome, double scale)
    {
        var size = WindowSizing.Bound(new(320 * scale, 320 * scale / video.Aspect), work, chrome, scale);
        // PIPViewController owns macOS placement. Windows uses the work area's
        // upper-right corner and the existing physical-pixel constraint rules.
        return WindowSizing.Constrain(new(work.Right - size.Width - chrome.Width - 16 * scale,
            work.Y + 16 * scale, size.Width + chrome.Width, size.Height + chrome.Height), work);
    }

    public static OscLayoutPlan Controls(double width, double height, double measuredHeight)
    {
        double w = Math.Min(360, Math.Max(0, width - 16));
        double h = Math.Min(Math.Max(0, height), Math.Max(0, measuredHeight));
        return new(new(Math.Max(0, (width - w) / 2), Math.Max(0, height - h - 8), w, h),
            new(8, 4, 8, 4), true, false, false, w >= 240, false, w >= 280);
    }
}

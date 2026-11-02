// Ported from IINA 1.5.1/build 180 MainWindowController, PlayerCore,
// Preference and Extensions. Copyright (c) 2016-2026 lhc and contributors.
// GPL-3.0; see licenses/IINA-GPL-3.0.txt. Windows frames use physical pixels.
using System.Globalization;
using Mani.Playback;

namespace Mani_App;

internal readonly record struct VideoSize(double Width, double Height)
{
    public double Aspect => Width / Height;
    public static VideoSize? FromProperties(IReadOnlyDictionary<string, string> properties)
    {
        double Read(string key) => properties.TryGetValue(key, out var text) &&
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : 0;
        // The pinned Windows VO can report a virtual 960x540 canvas for audio.
        // IINA's no-video fallback must precede those otherwise valid dimensions.
        if (properties.GetValueOrDefault("vid") is "no" or "0") return new(640, 360);
        double width = Read("dwidth"), height = Read("dheight");
        // Do not adopt a transient video-reconfig zero as the final media size.
        if (width <= 0 || height <= 0)
            return null;
        // Preserve both IINA steps: onVideoReconfig removes the user rotation,
        // then videoSizeForDisplay applies the remaining rotation.
        double rotation = Read("video-rotate");
        static bool QuarterTurn(double angle) => (angle % 360 + 360) % 360 is 90 or 270;
        if (QuarterTurn(rotation)) (width, height) = (height, width);
        if (QuarterTurn(Read("video-params/rotate") - rotation)) (width, height) = (height, width);
        return new(width, height);
    }
}

internal readonly record struct WindowFrame(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
}

internal static class WindowSizing
{
    // MainWindowMenuActions.menuChangeWindowSize cases 10/11: a 25-point
    // width step, proportional height and centered origin. Windows adds its
    // non-client/docked regions back after bounding the video area.
    public static WindowFrame Step(VideoSize video, WindowFrame frame, WindowFrame work,
        VideoSize chrome, double dpiScale, bool aspectLocked, int direction)
    {
        double currentWidth = Math.Max(1, frame.Width - chrome.Width);
        double currentHeight = Math.Max(1, frame.Height - chrome.Height);
        // IINA's currentWindowAspectRatio supplies the free-window fallback;
        // its raw window.aspectRatio is zero when aspect locking is disabled.
        double aspect = aspectLocked ? video.Aspect : currentWidth / currentHeight;
        double width = Math.Max(1, currentWidth + Math.Sign(direction) * 25 * dpiScale);
        var size = Bound(new(width, width / aspect), work, chrome, dpiScale);
        width = size.Width + chrome.Width;
        double height = size.Height + chrome.Height;
        return Constrain(new(frame.X + (frame.Width - width) / 2, frame.Y + (frame.Height - height) / 2, width, height), work);
    }

    // MainWindowController.removeVideoViewBlackBars: crop the available video
    // rectangle, apply its delta to the outer frame and preserve the center.
    public static WindowFrame FitVideo(WindowFrame frame, VideoSize video, VideoSize chrome, double sidebarWidth = 0)
    {
        double scale = Math.Min(Math.Max(1, frame.Width - chrome.Width) / video.Width,
            Math.Max(1, frame.Height - chrome.Height) / video.Height);
        // A shrinking client can reduce its sidebar to the 80% cap. Solve that
        // cap against the resulting video width, instead of retaining stale chrome.
        double sidebar = Math.Min(sidebarWidth, 4 * video.Width * scale);
        double width = video.Width * scale + chrome.Width - sidebarWidth + sidebar;
        double height = video.Height * scale + chrome.Height;
        return new(frame.X + (frame.Width - width) / 2, frame.Y + (frame.Height - height) / 2, width, height);
    }

    public static bool AspectLocked(PlayerSettings settings) => !settings.UnlockWindowAspectRatio && settings.EdgeToEdgeVideo;
    // IINA handleMagnifyGesture: relative width, preserved aspect/center, and
    // rejected out-of-bounds increments. Windows chrome remains unscaled.
    public static WindowFrame? Zoom(WindowFrame frame, WindowFrame work, VideoSize chrome, double dpiScale, double magnification, WindowFrame? anchor = null)
    {
        var origin = anchor ?? frame;
        double factor = 1 + magnification;
        double width = (frame.Width - chrome.Width) * factor;
        double height = width * (origin.Height - chrome.Height) / (origin.Width - chrome.Width);
        if (!double.IsFinite(factor) || factor <= 0 || magnification == 0 ||
            !double.IsFinite(width) || !double.IsFinite(height) ||
            width <= 285 * dpiScale || height <= 120 * dpiScale ||
            width + chrome.Width >= work.Width || height + chrome.Height >= work.Height) return null;
        width += chrome.Width; height += chrome.Height;
        return Constrain(new(origin.X + (origin.Width - width) / 2, origin.Y + (origin.Height - height) / 2, width, height), work);
    }
    public static bool NeedsResize(ResizeWindowTiming timing, bool initial, bool started, bool opened, bool keepSize) =>
        started ? initial || timing == ResizeWindowTiming.Always || timing == ResizeWindowTiming.OnlyWhenOpen && opened : !keepSize;

    public static WindowFrame Calculate(VideoSize video, WindowFrame frame, WindowFrame work,
        VideoSize chrome, double dpiScale, PlayerSettings settings, bool initial, bool started,
        bool opened, bool keepSize = false, ResizeWindowOption? explicitSize = null)
    {
        var option = explicitSize ?? settings.ResizeWindowOption;
        bool resize = explicitSize is not null || NeedsResize(settings.ResizeWindowTiming, initial, started, opened, keepSize);
        double width, height;
        if (resize)
        {
            double scale = settings.UsePhysicalResolution ? 1 : dpiScale;
            double ratio = started || explicitSize is not null ? option switch
            { ResizeWindowOption.Half => .5, ResizeWindowOption.OneAndHalf => 1.5, ResizeWindowOption.Double => 2, _ => 1 } : 1;
            width = video.Width * scale * ratio;
            height = video.Height * scale * ratio;
            // IINA's explicit fit/oversized scale command fits the CURRENT frame,
            // including a freely resized aspect. Automatic opening fits the video.
            bool fitCurrent = explicitSize is not null && (option == ResizeWindowOption.FitScreen ||
                width > work.Width - chrome.Width || height > work.Height - chrome.Height);
            if (fitCurrent)
            {
                width = Math.Max(1, frame.Width - chrome.Width);
                height = Math.Max(1, frame.Height - chrome.Height);
            }
            if (fitCurrent || (started || explicitSize is not null) && option == ResizeWindowOption.FitScreen)
            {
                double fit = Math.Min(Math.Max(1, work.Width - chrome.Width) / width, Math.Max(1, work.Height - chrome.Height) / height);
                width *= fit; height *= fit;
            }
        }
        else
        {
            double area = Math.Max(1, frame.Width - chrome.Width) * Math.Max(1, frame.Height - chrome.Height);
            width = Math.Sqrt(area * video.Aspect); height = area / width;
        }
        var bounded = Bound(new(width, height), work, chrome, dpiScale);
        width = bounded.Width + chrome.Width; height = bounded.Height + chrome.Height;
        var center = initial || resize && (started || explicitSize is not null) && option == ResizeWindowOption.FitScreen ? work : frame;
        return Constrain(new(center.X + (center.Width - width) / 2, center.Y + (center.Height - height) / 2, width, height), work);
    }

    internal static VideoSize Bound(VideoSize size, WindowFrame work, VideoSize chrome, double dpiScale)
    {
        // IINA's grow-to-minimum then final constrain: the usable screen wins if
        // both bounds cannot hold (portrait/extreme aspect ratios on small screens).
        double minimum = Math.Max(285 * dpiScale / size.Width, 120 * dpiScale / size.Height);
        if (minimum > 1) size = new(size.Width * minimum, size.Height * minimum);
        double maximum = Math.Min(Math.Max(1, work.Width - chrome.Width) / size.Width, Math.Max(1, work.Height - chrome.Height) / size.Height);
        return maximum < 1 ? new(size.Width * maximum, size.Height * maximum) : size;
    }
    public static WindowFrame Constrain(WindowFrame frame, WindowFrame work) => frame with
    {
        X = Math.Clamp(frame.X, work.X, Math.Max(work.X, work.Right - frame.Width)),
        Y = Math.Clamp(frame.Y, work.Y, Math.Max(work.Y, work.Bottom - frame.Height))
    };

    // WM_SIZING edge values: keep the opposite corner/edge fixed during native drag.
    public static WindowFrame Drag(WindowFrame frame, int edge, VideoSize video, VideoSize chrome, WindowFrame work, double dpiScale)
    {
        bool vertical = edge is 3 or 6;
        double width = vertical ? Math.Max(1, frame.Height - chrome.Height) * video.Aspect : Math.Max(1, frame.Width - chrome.Width);
        var size = Bound(new(width, width / video.Aspect), work, chrome, dpiScale);
        double w = size.Width + chrome.Width, h = size.Height + chrome.Height;
        double x = edge is 1 or 4 or 7 ? frame.Right - w : vertical ? frame.X + (frame.Width - w) / 2 : frame.X;
        double y = edge is 3 or 4 or 5 ? frame.Bottom - h : edge is 1 or 2 ? frame.Y + (frame.Height - h) / 2 : frame.Y;
        return new(x, y, w, h);
    }
}

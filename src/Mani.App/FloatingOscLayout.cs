// Adapted from IINA 1.5.1/build 180 OSCFloatingView position and drag constraints.
// Copyright (c) 2026 lhc. GPL-3.0; see licenses/IINA-GPL-3.0.txt.
namespace Mani_App;

internal readonly record struct OscBounds(double X, double Y, double Width, double Height);
internal static class FloatingOscLayout
{
    public static OscBounds Place(double width, double height, double measuredHeight, double horizontal, double bottom)
    {
        width = Math.Max(0, width); height = Math.Max(0, height);
        double barWidth = Math.Min(460, Math.Max(0, width - 2));
        double barHeight = Math.Min(Math.Max(0, measuredHeight), height);
        double x = width < 460 ? (width - barWidth) / 2 :
            Math.Clamp(width * horizontal - barWidth / 2, 0, width - barWidth);
        double y = Math.Clamp(height * (1 - bottom) - barHeight, Math.Min(25, height - barHeight), height - barHeight);
        return new(x, y, barWidth, barHeight);
    }
    public static OscBounds Drag(OscBounds bounds, double width, double height, double x, double y)
    {
        double center = (width - bounds.Width) / 2;
        if (Math.Abs(x - center) <= 5) x = center;
        double maxX = Math.Max(0, width - bounds.Width);
        double margin = Math.Min(10, maxX / 2);
        return bounds with { X = Math.Clamp(x, margin, maxX - margin),
            Y = Math.Clamp(y, Math.Min(25, Math.Max(0, height - bounds.Height)), Math.Max(0, height - bounds.Height)) };
    }
    public static (double Horizontal, double Bottom) Normalize(OscBounds bounds, double width, double height) =>
        (width > 0 ? Math.Clamp((bounds.X + bounds.Width / 2) / width, 0, 1) : .5,
         height > 0 ? Math.Clamp((height - bounds.Y - bounds.Height) / height, 0, 1) : .1);
}

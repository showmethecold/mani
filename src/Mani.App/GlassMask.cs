// Windows GPU-mask adapter for IINA TranslucentView glass surfaces.
// Copyright (c) 2016-2026 lhc and contributors. GPL-3.0; licenses/IINA-GPL-3.0.txt.
namespace Mani_App;

internal readonly record struct GlassMask(double X, double Y, double Width, double Height, double Corner, double Amount)
{
    // Bounds are measured relative to the existing video host. Keep off-video coordinates:
    // clamping them would move the mask across letterboxing or a docked sidebar.
    public static GlassMask Map(OscBounds bounds, double scaleX, double scaleY, double videoWidth, double videoHeight,
        double marginLeft, double marginTop, double corner, double amount)
    {
        if (!double.IsFinite(bounds.X + bounds.Y + bounds.Width + bounds.Height + scaleX + scaleY +
            videoWidth + videoHeight + marginLeft + marginTop + corner + amount) ||
            videoWidth <= 0 || videoHeight <= 0 || scaleX <= 0 || scaleY <= 0 || bounds.Width <= 0 || bounds.Height <= 0)
            return default;
        return new((bounds.X * scaleX - marginLeft) / videoWidth, (bounds.Y * scaleY - marginTop) / videoHeight,
            bounds.Width * scaleX / videoWidth, bounds.Height * scaleY / videoHeight,
            Math.Max(0, corner) * Math.Min(scaleX, scaleY), Math.Clamp(amount, 0, 1));
    }
    public string Parameters(string prefix) => FormattableString.Invariant(
        $"m1-blur/{prefix}x={X:F6},m1-blur/{prefix}y={Y:F6},m1-blur/{prefix}w={Width:F6},m1-blur/{prefix}h={Height:F6},m1-blur/{prefix}corner={Corner:F2},m1-blur/{prefix}amount={Amount:F3}");
}

// IINA 1.5.1/build 180 PlaySliderCell.drawBar chapter positions.
// Copyright (c) 2016-2026 lhc and contributors. GPL-3.0; licenses/IINA-GPL-3.0.txt.
using Mani.Playback;

namespace Mani_App;

internal static class ChapterMarkerLayout
{
    public static double[] Fractions(double duration, IReadOnlyList<MediaChapter> chapters)
    {
        if (!double.IsFinite(duration) || duration <= 0 || chapters.Count < 2) return [];
        // Like IINA, the first chapter is the start, not a separator. Reject
        // malformed/out-of-range native metadata before creating XAML geometry.
        return chapters.Skip(1).Select(chapter => chapter.StartTime / duration)
            .Where(position => double.IsFinite(position) && position > 0 && position < 1)
            .Distinct().ToArray();
    }

    // WinUI's seek range runs between the thumb centers at each end of the track.
    public static double Center(double fraction, double width, double thumbWidth)
        => Math.Min(width, thumbWidth) / 2 + Math.Clamp(fraction, 0, 1) * Math.Max(0, width - thumbWidth);

    public static double Fraction(double position, double width, double thumbWidth)
        => width > thumbWidth ? Math.Clamp((position - thumbWidth / 2) / (width - thumbWidth), 0, 1) : 0;
}

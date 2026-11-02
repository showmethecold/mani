// IINA MainWindowController minimumInitialDragDistance / Extensions CGPoint.distance.
// Copyright (c) 2016-2026 lhc and contributors. GPL-3.0; licenses/IINA-GPL-3.0.txt.
namespace Mani_App;

internal static class WindowDragPolicy
{
    // XAML pointer positions are DIPs, corresponding to IINA's logical points.
    public static bool BeyondThreshold(double dx, double dy) => dx * dx + dy * dy > 3 * 3;

    // Screen coordinates avoid feedback from the moving XAML coordinate origin.
    public static (int X, int Y) Position(double x, double y, int startX, int startY, int currentX, int currentY)
        => ((int)Math.Round(x + currentX - startX), (int)Math.Round(y + currentY - startY));
}

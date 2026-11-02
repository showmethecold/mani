using Mani_App;
namespace Mani.UnitTests;

public sealed class WindowDragTests
{
    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(3, 0, false)]
    [InlineData(0, -3, false)]
    [InlineData(2, 2, false)]
    [InlineData(3.001, 0, true)]
    [InlineData(-3.001, 0, true)]
    [InlineData(0, -3.001, true)]
    [InlineData(2.5, 2, true)]
    [InlineData(-2.5, -2, true)]
    public void InitialDragUsesStrictThreePointEuclideanDistance(double dx, double dy, bool drag)
        => Assert.Equal(drag, WindowDragPolicy.BeyondThreshold(dx, dy));

    [Theory]
    [InlineData(0, 0)] [InlineData(3840, 2160)] [InlineData(-1920, 400)]
    [InlineData(200, -1080)] [InlineData(-32768, 32767)]
    public void MovementUsesStableSignedScreenCoordinates(int x, int y)
    {
        Assert.Equal((x+80, y-40), WindowDragPolicy.Position(x, y, 200, 300, 280, 260));
        Assert.Equal((x, y), WindowDragPolicy.Position(x, y, 200, 300, 200, 300));
    }
}

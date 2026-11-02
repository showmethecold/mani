using Mani_App;
using Mani.Playback;

namespace Mani.UnitTests;

public sealed class GlassMaskTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(1.5)]
    [InlineData(1.75)]
    [InlineData(2.25)]
    public void LetterboxingAndDpiAreRemovedOnce(double scale)
    {
        var mask = GlassMask.Map(new(20, 60, 200, 80), scale, scale, 1000 * scale, 500 * scale,
            10 * scale, 40 * scale, 12, .75);
        Assert.Equal(.01, mask.X, 6); Assert.Equal(.04, mask.Y, 6);
        Assert.Equal(.2, mask.Width, 6); Assert.Equal(.16, mask.Height, 6);
        Assert.Equal(12 * scale, mask.Corner); Assert.Equal(.75, mask.Amount);
    }
    [Fact]
    public void DockedOrOffVideoSurfacesDoNotSlideIntoVideo()
    {
        var mask = GlassMask.Map(new(-300, 0, 300, 500), 1, 1, 1000, 500, 0, 0, 0, 0);
        Assert.Equal(-.3, mask.X); Assert.Equal(0, mask.X + mask.Width); Assert.Equal(0, mask.Amount);
    }
    [Theory]
    [InlineData(double.NaN, 500, 1)]
    [InlineData(1000, 0, 1)]
    [InlineData(1000, 500, 0)]
    [InlineData(double.PositiveInfinity, 500, 1)]
    public void InvalidHostGeometryDisablesMask(double width, double height, double scale)
        => Assert.Equal(default, GlassMask.Map(new(0, 0, 100, 50), scale, scale, width, height, 0, 0, 12, 1));
    [Theory]
    [InlineData(-1, 0)]
    [InlineData(2, 1)]
    public void TransitionAmountCannotEscapeBounds(double input, double expected)
        => Assert.Equal(expected, GlassMask.Map(new(0, 0, 100, 50), 1, 1, 1000, 500, 0, 0, 12, input).Amount);
    [Theory]
    [InlineData(OscPosition.Floating, 441, false)]
    [InlineData(OscPosition.Floating, 442, true)]
    [InlineData(OscPosition.Top, 679, false)]
    [InlineData(OscPosition.Top, 680, true)]
    [InlineData(OscPosition.Bottom, 679, false)]
    [InlineData(OscPosition.Bottom, 680, true)]
    public void DirectToolsDetachBeforeTheyConsumeTimelineSpace(OscPosition position, double width, bool visible)
        => Assert.Equal(visible, OscLayout.DirectTools(OscLayout.Create(position, width, 500, 90, .5, .1, false)));
}

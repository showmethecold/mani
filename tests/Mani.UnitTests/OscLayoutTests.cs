using Mani.Playback;
using Mani_App;

namespace Mani.UnitTests;

public sealed class OscLayoutTests
{
    [Theory]
    [InlineData(OscPosition.Top, 306, 220)]
    [InlineData(OscPosition.Bottom, 306, 220)]
    [InlineData(OscPosition.Top, 946, 500)]
    [InlineData(OscPosition.Bottom, 946, 500)]
    [InlineData(OscPosition.Top, 1920, 1080)]
    [InlineData(OscPosition.Bottom, 1920, 1080)]
    [InlineData(OscPosition.Top, 0, 0)]
    [InlineData(OscPosition.Bottom, 40, 20)]
    public void EdgeModesSpanVideoAndRemainAtTheirEdge(OscPosition position, double width, double height)
    {
        var plan = OscLayout.Create(position, width, height, 60, .9, .8, false);
        Assert.False(plan.Floating);
        Assert.Equal(0, plan.Bounds.X);
        Assert.Equal(width, plan.Bounds.Width);
        Assert.InRange(plan.Bounds.Height, 0, height);
        if (position == OscPosition.Top) Assert.Equal(0, plan.Bounds.Y);
        else Assert.Equal(height, plan.Bounds.Y + plan.Bounds.Height);
    }

    [Theory]
    [InlineData(OscPosition.Top)]
    [InlineData(OscPosition.Bottom)]
    public void NarrowingKeepsTimelineAndDetachesToolsBeforeVolumeThenSeek(OscPosition position)
    {
        bool toolsRemoved = false, volumeRemoved = false, seekRemoved = false;
        for (int width = 800; width >= 280; width--)
        {
            var plan = OscLayout.Create(position, width, 500, 60, .5, .1, false);
            if (!plan.FullscreenButton) toolsRemoved = true;
            if (!plan.VolumeGroup) { Assert.True(toolsRemoved); volumeRemoved = true; }
            if (!plan.SeekButtons) { Assert.True(volumeRemoved); seekRemoved = true; }
            Assert.True(plan.TimeLabels);
            Assert.True(plan.Bounds.Width > 0);
        }
        Assert.True(toolsRemoved && volumeRemoved && seekRemoved);
    }

    [Fact]
    public void FullscreenAddsOnlyTheTopInsetAndDoesNotMoveTheBottomEdge()
    {
        var top = OscLayout.Create(OscPosition.Top, 960, 540, 46, .5, .1, false);
        var full = OscLayout.Create(OscPosition.Top, 960, 540, 54, .5, .1, true);
        Assert.Equal(top.Padding.Top + 8, full.Padding.Top);
        Assert.Equal(10, full.Padding.Bottom);
        var bottom = OscLayout.Create(OscPosition.Bottom, 960, 540, 60, .5, .1, true);
        Assert.Equal(new OscInsets(16, 12, 12, 12), bottom.Padding);
        Assert.Equal(540, bottom.Bounds.Y + bottom.Bounds.Height);
    }

    [Fact]
    public void EdgeModesIgnoreFloatingCoordinatesAndReturningRestoresThem()
    {
        var initial = OscLayout.Create(OscPosition.Floating, 960, 540, 93, .72, .45, false);
        foreach (var mode in new[] { OscPosition.Top, OscPosition.Bottom })
        {
            Assert.Equal(OscLayout.Create(mode, 960, 540, 60, 0, 0, false),
                OscLayout.Create(mode, 960, 540, 60, 1, 1, false));
        }
        var restored = OscLayout.Create(OscPosition.Floating, 960, 540, 93, .72, .45, true);
        Assert.Equal(initial, restored);
        Assert.Equal(FloatingOscLayout.Place(960, 540, 93, .72, .45), restored.Bounds);
    }
}

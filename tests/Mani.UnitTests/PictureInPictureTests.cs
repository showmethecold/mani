using Mani_App;

namespace Mani.UnitTests;

public sealed class PictureInPictureTests
{
    [Theory]
    [InlineData(1920, 1080, 1)]
    [InlineData(1920, 1080, 1.75)]
    [InlineData(1080, 1920, 1.75)]
    [InlineData(1920, 800, 2)]
    [InlineData(640, 360, 1.25)]
    [InlineData(20000, 100, 1)]
    [InlineData(100, 20000, 1)]
    public void InitialWindowUsesVideoAspectAndFitsWorkArea(double width, double height, double scale)
    {
        var work = new WindowFrame(-1920, 48, 1920, 1032);
        var chrome = new VideoSize(16 * scale, 39 * scale);
        var frame = PictureInPictureLayout.Initial(new(width, height), work, chrome, scale);
        Assert.InRange(frame.X, work.X, work.Right);
        Assert.InRange(frame.Y, work.Y, work.Bottom);
        Assert.InRange(frame.Right, frame.X, work.Right + .001);
        Assert.InRange(frame.Bottom, frame.Y, work.Bottom + .001);
        Assert.Equal(width / height, (frame.Width - chrome.Width) / (frame.Height - chrome.Height), 6);
    }

    [Fact]
    public void InitialSizeUsesLogicalPixelsAndLeavesRoomForSystemCaption()
    {
        var frame = PictureInPictureLayout.Initial(new(1920, 1080), new(0, 0, 3840, 2076), new(28, 68), 1.75);
        Assert.Equal(588, frame.Width);
        Assert.Equal(383, frame.Height);
        Assert.Equal(28, frame.Y);
        Assert.Equal(3812, frame.Right);
    }

    [Theory]
    [InlineData(285, 160, true, false)]
    [InlineData(320, 180, true, true)]
    [InlineData(240, 150, false, false)]
    [InlineData(640, 360, true, true)]
    public void CompactControlsPreservePlayReturnAndProgressWithoutMainWindowTools(double width, double height, bool seek, bool labels)
    {
        var plan = PictureInPictureLayout.Controls(width, height, 72);
        Assert.True(plan.Floating);
        Assert.False(plan.VolumeGroup);
        Assert.False(plan.FullscreenButton);
        Assert.Equal(seek, plan.SeekButtons);
        Assert.Equal(labels, plan.TimeLabels);
        Assert.InRange(plan.Bounds.Width, 0, Math.Min(360, width));
        Assert.InRange(plan.Bounds.X + plan.Bounds.Width, 0, width);
        Assert.InRange(plan.Bounds.Y + plan.Bounds.Height, 0, height);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(100, 30)]
    [InlineData(-10, -10)]
    public void TransientEmptyLayoutDoesNotProduceNegativeBounds(double width, double height)
    {
        var plan = PictureInPictureLayout.Controls(width, height, 72);
        Assert.True(plan.Bounds.X >= 0 && plan.Bounds.Y >= 0 && plan.Bounds.Width >= 0 && plan.Bounds.Height >= 0);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(4, false)]
    public void LoadingReplacementRetainsPipButTerminalStatesReturnToMainWindow(int surface, bool returns)
        => Assert.Equal(returns, PictureInPictureLayout.ShouldReturn((PlayerSurface)surface));
}

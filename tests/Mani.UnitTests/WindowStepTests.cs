using Mani_App;

namespace Mani.UnitTests;

public sealed class WindowStepTests
{
    private static readonly WindowFrame Work = new(0, 0, 3840, 2076);
    private static readonly VideoSize Video = new(1920, 1080);
    [Theory]
    [InlineData(1, -1)] [InlineData(1, 1)]
    [InlineData(1.25, -1)] [InlineData(1.25, 1)]
    [InlineData(1.75, -1)] [InlineData(1.75, 1)]
    [InlineData(2, -1)] [InlineData(2, 1)]
    public void StepUsesTwentyFiveDipsAndKeepsVideoAspectAndCenter(double dpi, int direction)
    {
        var frame = new WindowFrame(500, 400, 1000, 613);
        var chrome = new VideoSize(40, 73);
        var result = WindowSizing.Step(Video, frame, Work, chrome, dpi, true, direction);
        Assert.Equal(frame.Width + direction * 25 * dpi, result.Width, 8);
        Assert.Equal(Video.Aspect, (result.Width - chrome.Width) / (result.Height - chrome.Height), 8);
        Assert.Equal(frame.X + frame.Width / 2, result.X + result.Width / 2, 8);
        Assert.Equal(frame.Y + frame.Height / 2, result.Y + result.Height / 2, 8);
    }
    [Theory]
    [InlineData(-1)] [InlineData(1)]
    public void UnlockedFrameKeepsItsFreeVideoAreaAspect(int direction)
    {
        var frame = new WindowFrame(800, 400, 924, 964);
        var result = WindowSizing.Step(Video, frame, Work, new(24, 64), 1.75, false, direction);
        Assert.Equal(result.Width - 24, result.Height - 64, 8);
        Assert.True(double.IsFinite(result.Height));
    }
    [Fact]
    public void DockedControlsAndSidebarStayOutsideTheVideoAspect()
    {
        var chrome = new VideoSize(654, 220);
        var frame = new WindowFrame(600, 400, 1614, 760);
        var result = WindowSizing.Step(Video, frame, Work, chrome, 1.75, true, 1);
        Assert.Equal(1657.75, result.Width, 8);
        Assert.Equal(Video.Aspect, (result.Width - chrome.Width) / (result.Height - chrome.Height), 8);
    }
    [Theory]
    [InlineData(1920, 1080)] [InlineData(1080, 1920)]
    public void RepeatedStepsSaturateAtMinimumAndWorkArea(double width, double height)
    {
        var video = new VideoSize(width, height);
        var chrome = new VideoSize(24, 64);
        var frame = new WindowFrame(300, 200, 1200, 900);
        for (int i = 0; i < 200; i++) frame = WindowSizing.Step(video, frame, Work, chrome, 1.75, true, -1);
        Assert.True(frame.Width - chrome.Width >= 285 * 1.75 - .001);
        Assert.True(frame.Height - chrome.Height >= 120 * 1.75 - .001);
        Assert.Equal(frame, WindowSizing.Step(video, frame, Work, chrome, 1.75, true, -1));
        for (int i = 0; i < 200; i++) frame = WindowSizing.Step(video, frame, Work, chrome, 1.75, true, 1);
        Assert.True(frame.X >= Work.X && frame.Y >= Work.Y);
        Assert.True(frame.Right <= Work.Right + .001 && frame.Bottom <= Work.Bottom + .001);
        Assert.Equal(video.Aspect, (frame.Width - chrome.Width) / (frame.Height - chrome.Height), 8);
    }
    [Fact]
    public void WorkingAreaWinsWhenExtremeAspectCannotMeetBothMinimums()
    {
        var result = WindowSizing.Step(new(1, 100), new(100, 100, 300, 600), new(0, 0, 800, 600), new(24, 64), 2, true, 1);
        Assert.True(result.Right <= 800 && result.Bottom <= 600);
        Assert.Equal(.01, (result.Width - 24) / (result.Height - 64), 8);
    }
    [Fact]
    public void RepeatedStepsReadLatestFrameInsteadOfStaleOriginal()
    {
        var frame = new WindowFrame(500, 400, 984, 604);
        var current = frame;
        for (int i = 0; i < 4; i++) current = WindowSizing.Step(Video, current, Work, new(24, 64), 1.75, true, 1);
        Assert.Equal(frame.Width + 175, current.Width, 8);
        for (int i = 0; i < 4; i++) current = WindowSizing.Step(Video, current, Work, new(24, 64), 1.75, true, -1);
        Assert.Equal(frame.Width, current.Width, 8);
        Assert.Equal(frame.Height, current.Height, 8);
    }
}

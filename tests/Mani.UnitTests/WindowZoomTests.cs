using Mani_App;

namespace Mani.UnitTests;

public sealed class WindowZoomTests
{
    private static readonly WindowFrame Work = new(-1920, -200, 3840, 2080);
    [Theory]
    [InlineData(1, .1)] [InlineData(1, -.1)]
    [InlineData(1.25, .025)] [InlineData(1.75, -.025)] [InlineData(2, .001)]
    public void ZoomKeepsCenterAndCurrentContentAspectWithUnscaledChrome(double dpi, double delta)
    {
        var frame = new WindowFrame(-1000, 100, 1500, 1000);
        var chrome = new VideoSize(350, 180);
        var result = Assert.IsType<WindowFrame>(WindowSizing.Zoom(frame, Work, chrome, dpi, delta));
        Assert.Equal((frame.Width - chrome.Width) * (1 + delta), result.Width - chrome.Width, 8);
        Assert.Equal((frame.Height - chrome.Height) * (1 + delta), result.Height - chrome.Height, 8);
        Assert.Equal(frame.X + frame.Width / 2, result.X + result.Width / 2, 8);
        Assert.Equal(frame.Y + frame.Height / 2, result.Y + result.Height / 2, 8);
    }
    [Theory]
    [InlineData(-1)] [InlineData(-2)] [InlineData(0)]
    [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)] [InlineData(double.NegativeInfinity)]
    public void InvalidOrEmptyIncrementDoesNotProduceAFrame(double delta) =>
        Assert.Null(WindowSizing.Zoom(new(0, 0, 1000, 600), Work, new(24, 64), 1, delta));

    [Theory]
    [InlineData(1)] [InlineData(1.25)] [InlineData(1.75)] [InlineData(2)]
    public void BothMinimumDimensionsAreStrict(double dpi)
    {
        Assert.Null(WindowSizing.Zoom(new(0, 0, 570 * dpi + 24, 500 * dpi + 64), Work, new(24, 64), dpi, -.5));
        Assert.Null(WindowSizing.Zoom(new(0, 0, 1000 * dpi + 24, 240 * dpi + 64), Work, new(24, 64), dpi, -.5));
    }
    [Fact]
    public void BothWorkAreaDimensionsAreStrictAndRejectedDeltaDoesNotAccumulate()
    {
        var frame = new WindowFrame(0, 0, 984, 604);
        var chrome = new VideoSize(24, 64);
        Assert.Null(WindowSizing.Zoom(frame, new(0, 0, 1080, 2000), chrome, 1, .1));
        Assert.Null(WindowSizing.Zoom(frame, new(0, 0, 2000, 658), chrome, 1, .1));
        var next = Assert.IsType<WindowFrame>(WindowSizing.Zoom(frame, Work, chrome, 1, -.1));
        Assert.Equal(888, next.Width, 8);
    }
    [Fact]
    public void NearMonitorEdgeKeepsWindowInsideWorkArea()
    {
        var work = new WindowFrame(-1920, 40, 1920, 1040);
        var result = Assert.IsType<WindowFrame>(WindowSizing.Zoom(new(-1920, 40, 1000, 600), work, new(24, 64), 1, .1));
        Assert.Equal(work.X, result.X);
        Assert.Equal(work.Y, result.Y);
        Assert.True(result.Right <= work.Right && result.Bottom <= work.Bottom);
    }
    [Fact]
    public void SuccessiveRelativeIncrementsUseLatestFrame()
    {
        var frame = new WindowFrame(-1200, 200, 1000, 600);
        var chrome = new VideoSize(24, 64);
        for (int i = 0; i < 10; i++) frame = WindowSizing.Zoom(frame, Work, chrome, 1, .01)!.Value;
        Assert.Equal(976 * Math.Pow(1.01, 10) + 24, frame.Width, 8);
        Assert.Equal(536 * Math.Pow(1.01, 10) + 64, frame.Height, 8);
    }
    [Theory]
    [InlineData(120, .1)] [InlineData(-120, -.1)] [InlineData(30, .025)] [InlineData(1, 1d / 1200)] [InlineData(0, 0)]
    public void WindowsZoomPreservesFractionalInput(int delta, double expected) =>
        Assert.Equal(expected, WheelZoom.Magnification(WheelTarget.Video, false, true, delta));

    [Theory]
    [InlineData(WheelTarget.Video, true, true)]
    [InlineData(WheelTarget.Video, false, false)]
    [InlineData(WheelTarget.Volume, false, true)]
    [InlineData(WheelTarget.Timeline, false, true)]
    [InlineData(WheelTarget.None, false, true)]
    internal void OtherInputRetainsItsOwner(WheelTarget target, bool horizontal, bool control) =>
        Assert.Null(WheelZoom.Magnification(target, horizontal, control, 120));
}

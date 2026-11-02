using Mani_App;
using Mani.Playback;

namespace Mani.UnitTests;

public sealed class WindowSizingTests
{
    private static readonly WindowFrame Work = new(0, 0, 3840, 2076);
    private static readonly WindowFrame Frame = new(500, 400, 1100, 760);
    private static readonly VideoSize Chrome = new(24, 64);
    private static WindowFrame Calculate(VideoSize video, PlayerSettings? settings = null, bool initial = false,
        bool started = true, bool opened = true, WindowFrame? frame = null, VideoSize? chrome = null) =>
        WindowSizing.Calculate(video, frame ?? Frame, Work, chrome ?? Chrome, 1.75, settings ?? new(), initial, started, opened);

    [Theory]
    [InlineData(ResizeWindowOption.Half, 960, 540)]
    [InlineData(ResizeWindowOption.Normal, 1920, 1080)]
    [InlineData(ResizeWindowOption.OneAndHalf, 2880, 1620)]
    [InlineData(ResizeWindowOption.Double, 3576.888888888889, 2012)]
    [InlineData(ResizeWindowOption.FitScreen, 3576.888888888889, 2012)]
    public void PhysicalSizeAndScreenBoundIncludeSystemCaption(ResizeWindowOption option, double width, double height)
    {
        var result = Calculate(new(1920, 1080), new(ResizeWindowOption: option));
        Assert.Equal(width + Chrome.Width, result.Width, 6);
        Assert.Equal(height + Chrome.Height, result.Height, 6);
        AssertContained(result);
    }

    [Fact]
    public void InitialCentersOnScreenAndLaterOpeningPreservesWindowCenter()
    {
        var initial = Calculate(new(1280, 720), initial: true);
        Assert.Equal(Work.Width / 2, initial.X + initial.Width / 2);
        Assert.Equal(Work.Height / 2, initial.Y + initial.Height / 2);
        var next = Calculate(new(640, 360));
        Assert.Equal(Frame.X + Frame.Width / 2, next.X + next.Width / 2);
        Assert.Equal(Frame.Y + Frame.Height / 2, next.Y + next.Height / 2);
    }

    [Fact]
    public void LogicalResolutionUsesDpiWhilePhysicalDoesNot()
    {
        var physical = Calculate(new(640, 360));
        var logical = Calculate(new(640, 360), new(UsePhysicalResolution: false));
        Assert.Equal((physical.Width - Chrome.Width) * 1.75, logical.Width - Chrome.Width);
        Assert.Equal((physical.Height - Chrome.Height) * 1.75, logical.Height - Chrome.Height);
    }

    [Fact]
    public void DockedControlsAreAddedOutsideVideoAndStillFitWorkArea()
    {
        var result = Calculate(new(3840, 2160), chrome: new(24, 164));
        Assert.Equal(16.0 / 9, (result.Width - 24) / (result.Height - 164), 8);
        Assert.Equal(2076, result.Height);
        AssertContained(result);
    }

    [Theory]
    [InlineData(ResizeWindowTiming.Always, false, true, false, false, true)]
    [InlineData(ResizeWindowTiming.OnlyWhenOpen, false, true, true, false, true)]
    [InlineData(ResizeWindowTiming.OnlyWhenOpen, false, true, false, false, false)]
    [InlineData(ResizeWindowTiming.Never, false, true, true, false, false)]
    [InlineData(ResizeWindowTiming.Never, true, true, true, false, true)]
    [InlineData(ResizeWindowTiming.Never, false, false, false, false, true)]
    [InlineData(ResizeWindowTiming.Always, false, false, false, true, false)]
    public void TimingMatchesIina(ResizeWindowTiming timing, bool initial, bool started, bool opened, bool keep, bool expected) =>
        Assert.Equal(expected, WindowSizing.NeedsResize(timing, initial, started, opened, keep));

    [Fact]
    public void PlaylistAndNeverPreserveVideoAreaWhenAspectChanges()
    {
        var playlist = Calculate(new(4, 3), opened: false);
        var never = Calculate(new(4, 3), new(ResizeWindowTiming: ResizeWindowTiming.Never));
        Assert.Equal(playlist, never);
        Assert.Equal((Frame.Width - 24) * (Frame.Height - 64), (playlist.Width - 24) * (playlist.Height - 64), 5);
        Assert.Equal(4.0 / 3, (playlist.Width - 24) / (playlist.Height - 64), 8);
    }

    [Theory]
    [InlineData(9, 16)]
    [InlineData(100, 1)]
    [InlineData(1, 100)]
    public void ExtremeAspectAndMinimumNeverOverflowScreen(double width, double height)
    {
        var result = Calculate(new(width, height));
        AssertContained(result);
        Assert.Equal(width / height, (result.Width - 24) / (result.Height - 64), 7);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)]
    public void NativeDragKeepsAspectAndOppositeAnchor(int edge)
    {
        var result = WindowSizing.Drag(Frame, edge, new(16, 9), Chrome, Work, 1.75);
        Assert.Equal(16.0 / 9, (result.Width - 24) / (result.Height - 64), 8);
        if (edge is 1 or 4 or 7) Assert.Equal(Frame.Right, result.Right);
        if (edge is 3 or 4 or 5) Assert.Equal(Frame.Bottom, result.Bottom);
        if (edge is 2 or 5 or 8) Assert.Equal(Frame.X, result.X);
        if (edge is 6 or 7 or 8) Assert.Equal(Frame.Y, result.Y);
    }

    [Theory]
    [InlineData(0, 0, 1920, 1080)]
    [InlineData(90, 0, 1080, 1920)]
    [InlineData(270, 0, 1080, 1920)]
    [InlineData(180, 0, 1920, 1080)]
    [InlineData(90, 90, 1080, 1920)]
    [InlineData(0, 90, 1920, 1080)]
    [InlineData(-90, 0, 1080, 1920)]
    public void RotationPortsBothSourceStages(int metadata, int user, double width, double height)
    {
        var values = new Dictionary<string, string> { ["dwidth"] = "1920", ["dheight"] = "1080", ["video-params/rotate"] = metadata.ToString(), ["video-rotate"] = user.ToString() };
        Assert.Equal(new VideoSize(width, height), VideoSize.FromProperties(values));
    }

    [Fact]
    public void ExplicitFitKeepsFreeAspectAndCentersWhileOversizedScaleKeepsCurrentCenter()
    {
        var free = new WindowFrame(1200, 600, 1024, 1064);
        var fit = WindowSizing.Calculate(new(3840, 2160), free, Work, Chrome, 1.75, new(UnlockWindowAspectRatio: true),
            false, false, false, explicitSize: ResizeWindowOption.FitScreen);
        Assert.Equal(fit.Width - Chrome.Width, fit.Height - Chrome.Height);
        Assert.Equal(Work.Width / 2, fit.X + fit.Width / 2);
        var oversized = WindowSizing.Calculate(new(3840, 2160), free, Work, Chrome, 1.75, new(UnlockWindowAspectRatio: true),
            false, false, false, explicitSize: ResizeWindowOption.Double);
        Assert.Equal(oversized.Width - Chrome.Width, oversized.Height - Chrome.Height);
        AssertContained(oversized);
    }

    [Fact]
    public void AudioUsesFallbackButUnreadyVideoDoesNotResize()
    {
        Assert.Equal(new VideoSize(640, 360), VideoSize.FromProperties(new Dictionary<string, string> { ["vid"] = "no" }));
        Assert.Equal(new VideoSize(640, 360), VideoSize.FromProperties(new Dictionary<string, string> { ["vid"] = "no", ["dwidth"] = "960", ["dheight"] = "540" }));
        Assert.Null(VideoSize.FromProperties(new Dictionary<string, string> { ["vid"] = "1", ["dwidth"] = "0" }));
        Assert.Null(VideoSize.FromProperties(new Dictionary<string, string> { ["dwidth"] = "NaN", ["dheight"] = "1080" }));
    }

    [Fact]
    public void EdgeToEdgeOffImplicitlyUnlocksRegardlessOfExplicitPreference()
    {
        Assert.True(WindowSizing.AspectLocked(new()));
        Assert.False(WindowSizing.AspectLocked(new(EdgeToEdgeVideo: false)));
        Assert.False(WindowSizing.AspectLocked(new(UnlockWindowAspectRatio: true)));
    }

    private static void AssertContained(WindowFrame frame)
    {
        Assert.InRange(frame.X, Work.X, Work.Right); Assert.InRange(frame.Y, Work.Y, Work.Bottom);
        Assert.True(frame.Right <= Work.Right + .001); Assert.True(frame.Bottom <= Work.Bottom + .001);
    }
}

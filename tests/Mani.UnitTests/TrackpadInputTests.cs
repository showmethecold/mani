using Mani_App;
namespace Mani.UnitTests;

public sealed class TrackpadInputTests
{
    [Theory]
    [InlineData(20, 0, 5, 0)] [InlineData(-20, 0, -5, 0)]
    [InlineData(0, -20, 0, 15)] [InlineData(0, 20, 0, -15)]
    [InlineData(.2, 0, .05, 0)] [InlineData(0, -.2, 0, .15)]
    public void DirectPanUsesPinnedPreciseMaps(double x, double y, double seek, double volume)
    {
        var gesture = new TrackpadGesture(); gesture.Begin(WheelTarget.Video, 0, 0, 1);
        var change = gesture.Update(x, y, 1);
        Assert.Equal(seek, change.Seek, 8); Assert.Equal(volume, change.Volume, 8);
        Assert.Equal(seek != 0, change.BeginSeek);
    }
    [Fact]
    public void DirectionRemainsLockedUntilFingerRelease()
    {
        var gesture = new TrackpadGesture(); gesture.Begin(WheelTarget.Video, 0, 0, 1);
        Assert.Equal(3, gesture.Update(12, 20, 1).Seek);
        var next = gesture.Update(16, -100, 1);
        Assert.Equal(1, next.Seek); Assert.Equal(0, next.Volume); Assert.False(next.BeginSeek);
        Assert.True(gesture.BeginInertia().EndSeek);
        Assert.False(gesture.BeginInertia().EndSeek);
        Assert.Equal(15, gesture.Update(16, -115, 1).Volume);
    }
    [Theory]
    [InlineData(WheelTarget.Timeline, 0, -8, 2, 0)]
    [InlineData(WheelTarget.Timeline, 8, 0, 2, 0)]
    [InlineData(WheelTarget.Volume, 0, -8, 0, 6)]
    [InlineData(WheelTarget.Volume, 8, 0, 0, 6)]
    [InlineData(WheelTarget.None, 8, 0, 0, 0)]
    internal void ControlOverridesRetainTheirOwnAction(WheelTarget target, double x, double y, double seek, double volume)
    {
        var gesture = new TrackpadGesture(); gesture.Begin(target, 0, 0, 1);
        var change = gesture.Update(x, y, 1);
        Assert.Equal(seek, change.Seek); Assert.Equal(volume, change.Volume);
    }
    [Fact]
    public void OrphanInertiaAndValuesAfterEndDoNothing()
    {
        var gesture = new TrackpadGesture();
        Assert.Equal(default, gesture.BeginInertia()); Assert.Equal(default, gesture.Update(100, 20, 1));
        gesture.Begin(WheelTarget.Video, 0, 0, 1); gesture.Update(10, 0, 1);
        Assert.True(gesture.End().EndSeek);
        Assert.Equal(default, gesture.Update(200, 100, 1)); Assert.False(gesture.End().EndSeek);
    }
    [Fact]
    public void InertiaUsesIinaEmptyPhaseMouseMapWithoutPausingAgain()
    {
        var gesture = new TrackpadGesture(); gesture.Begin(WheelTarget.Video, 0, 0, 1);
        gesture.Update(8, 0, 1); Assert.True(gesture.BeginInertia().EndSeek);
        var change = gesture.Update(10, 0, 1);
        Assert.Equal(4, change.Seek); Assert.False(change.BeginSeek); Assert.False(gesture.End().EndSeek);
    }
    [Fact]
    public void PinchOwnsTranslationAndKeepsAdjacentMagnificationDeltas()
    {
        var gesture = new TrackpadGesture(); gesture.Begin(WheelTarget.Video, 100, 200, 1);
        var first = gesture.Update(120, 180, 1.1);
        Assert.True(first.BeginZoom); Assert.Equal(.1, first.Zoom, 8); Assert.Equal(0, first.Seek); Assert.Equal(0, first.Volume);
        var second = gesture.Update(150, 250, 1.15);
        Assert.False(second.BeginZoom); Assert.Equal(.05, second.Zoom, 8);
        Assert.Equal(0, gesture.Update(200, 300, 1.15).Seek);
        gesture.BeginInertia(); Assert.Equal(0, gesture.Update(300, 500, 1.2).Zoom);
    }
    [Fact]
    public void ZoomRecognitionEndsAnEarlierSeekAndNewGestureResetsItsAxis()
    {
        var gesture = new TrackpadGesture(); gesture.Begin(WheelTarget.Video, 0, 0, 1);
        Assert.True(gesture.Update(4, 0, 1).BeginSeek);
        var pinch = gesture.Update(8, 0, 1.1);
        Assert.True(pinch.BeginZoom); Assert.True(pinch.EndSeek); Assert.False(gesture.End().EndSeek);
        gesture.Begin(WheelTarget.Video, 0, 0, 1);
        Assert.Equal(6, gesture.Update(0, -8, 1).Volume);
    }
    [Theory]
    [InlineData(double.NaN, 0, 1)] [InlineData(0, double.PositiveInfinity, 1)]
    [InlineData(0, 0, double.NaN)] [InlineData(0, 0, 0)] [InlineData(0, 0, -1)]
    public void InvalidNativeSamplesDoNotPoisonTheNextDelta(double x, double y, double scale)
    {
        var gesture = new TrackpadGesture(); gesture.Begin(WheelTarget.Video, 0, 0, 1);
        Assert.Equal(default, gesture.Update(x, y, scale)); Assert.Equal(2, gesture.Update(8, 0, 1).Seek);
    }
    [Fact]
    public void QueueCoalescesMotionButPreservesPauseMotionResumeOrderAndMediaIdentity()
    {
        var queue = new TrackpadCommandQueue(); var media = new TrackpadMedia(1, 2, 3);
        queue.Add(new(1, media, TrackpadCommandKind.PauseForSeek));
        for (int i = 0; i < 30; i++) queue.Add(new(1, media, TrackpadCommandKind.Seek, .5));
        queue.Add(new(1, media, TrackpadCommandKind.Resume));
        queue.Add(new(2, media with { Request = 4 }, TrackpadCommandKind.Seek, 5));
        var commands = queue.Take();
        Assert.Equal(4, commands.Length); Assert.Equal(TrackpadCommandKind.PauseForSeek, commands[0].Kind);
        Assert.Equal(15, commands[1].Amount); Assert.Equal(TrackpadCommandKind.Resume, commands[2].Kind);
        Assert.NotEqual(commands[1].Media, commands[3].Media); Assert.Empty(queue.Take());
    }
    [Fact]
    public void CancellationDiscardsOnlyThatGesturesMotionAndKeepsItsResume()
    {
        var queue = new TrackpadCommandQueue(); var media = new TrackpadMedia(1, 2, 3);
        queue.Add(new(1, media, TrackpadCommandKind.Seek, 10)); queue.Add(new(1, media, TrackpadCommandKind.Resume));
        queue.Add(new(2, media, TrackpadCommandKind.Volume, 5)); queue.DiscardMotion(1);
        var commands = queue.Take(); Assert.Equal(2, commands.Length); Assert.Equal(TrackpadCommandKind.Resume, commands[0].Kind);
        Assert.Equal(5, commands[1].Amount);
        queue.Add(commands[0]); queue.Clear(); Assert.Empty(queue.Take());
    }
    [Fact]
    public void PinchAnchorPreservesOriginalCenterAndAspectAfterRoundedFrames()
    {
        var anchor = new WindowFrame(500, 200, 1400, 860); var chrome = new VideoSize(24, 64);
        var current = new WindowFrame(487, 192, 1427, 876);
        var result = WindowSizing.Zoom(current, new(0, 0, 3840, 2080), chrome, 1.75, .025, anchor)!.Value;
        Assert.Equal(anchor.X + anchor.Width / 2, result.X + result.Width / 2, 8);
        Assert.Equal(anchor.Y + anchor.Height / 2, result.Y + result.Height / 2, 8);
        Assert.Equal((anchor.Width - chrome.Width) / (anchor.Height - chrome.Height), (result.Width - chrome.Width) / (result.Height - chrome.Height), 8);
    }
}

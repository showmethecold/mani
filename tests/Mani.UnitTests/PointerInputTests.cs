using Mani_App;
namespace Mani.UnitTests;

public sealed class PointerInputTests
{
    [Fact]
    public void ReplayedOrOlderWheelDoesNotRepeatAfterPointerReentry()
    {
        var events = new WheelEventSequence();
        Assert.True(events.Accept(100)); Assert.False(events.Accept(100));
        Assert.True(events.Accept(101)); Assert.False(events.Accept(100));
        Assert.False(events.Accept(101)); Assert.True(events.Accept(102));
    }
    [Fact]
    public void FreshHighFrequencyWheelIsNotTimeDebounced()
    {
        var events = new WheelEventSequence();
        for (ulong i = 0; i < 100; i++) Assert.True(events.Accept(i));
    }
    [Theory]
    [InlineData(0, false, 120, 0, 0)]
    [InlineData(1, false, 120, 0, 2)]
    [InlineData(1, false, -120, 0, -2)]
    [InlineData(1, true, 120, 2, 0)]
    [InlineData(1, true, -120, -2, 0)]
    [InlineData(2, false, 120, 4, 0)]
    [InlineData(2, false, -120, -4, 0)]
    [InlineData(2, true, 120, 2, 0)]
    [InlineData(2, true, -120, -2, 0)]
    [InlineData(3, false, 120, 0, 2)]
    [InlineData(3, true, 120, 0, 1)]
    [InlineData(1, false, 30, 0, .5)]
    [InlineData(1, true, 30, .5, 0)]
    [InlineData(2, false, 30, 1, 0)]
    [InlineData(1, false, 360, 0, 6)]
    [InlineData(1, true, 0, 0, 0)]
    public void WheelUsesIinaMouseStepsAndSliderOverrides(int target, bool horizontal, int delta, double seek, double volume)
        => Assert.Equal(new WheelChange(seek, volume), WheelChange.From((WheelTarget)target, horizontal, delta));

    [Fact]
    public void FastWheelEventsAccumulateInsteadOfUsingStaleUiValues()
    {
        var pending = new PendingWheelInput();
        for (int i = 0; i < 10; i++) pending.Add(1, 9, WheelChange.From(WheelTarget.Video, false, 120));
        Assert.Equal(new(0, 20), pending.Take(1, 9));
        Assert.Equal(default, pending.Take(1, 9));
    }
    [Fact]
    public void IndependentDirectionsAndOppositeDeltasCoalesce()
    {
        var pending = new PendingWheelInput();
        pending.Add(1, 1, new(2, 0)); pending.Add(1, 1, new(0, 2)); pending.Add(1, 1, new(-2, 0));
        Assert.Equal(new(0, 2), pending.Take(1, 1));
    }
    [Theory]
    [InlineData(2, 1)] [InlineData(1, 2)] [InlineData(2, 2)]
    public void OldMediaInputNeverAffectsReplacement(long request, long entry)
    {
        var pending = new PendingWheelInput(); pending.Add(1, 1, new(4, 2));
        Assert.Equal(default, pending.Take(request, entry));
        Assert.Equal(default, pending.Take(1, 1));
    }
    [Fact]
    public void FirstInputOnReplacementDiscardsOldAccumulation()
    {
        var pending = new PendingWheelInput(); pending.Add(1, 1, new(100, 100)); pending.Add(2, 2, new(-2, 0));
        Assert.Equal(new(-2, 0), pending.Take(2, 2));
        pending.Add(2, 2, new(10, 10)); pending.Clear(); Assert.Equal(default, pending.Take(2, 2));
    }
    [Fact]
    public void SingleClickWaitsForSystemIntervalAndFiresOnlyOnce()
    {
        var clicks = new VideoClickState();
        Assert.Equal(PointerClick.PendingSingle, clicks.Release(100, 10, 20, 500, 4, 4));
        Assert.False(clicks.Expire(599)); Assert.Equal(1, clicks.MillisecondsUntilExpiration(599));
        Assert.True(clicks.Pending); Assert.True(clicks.Expire(600)); Assert.False(clicks.Expire(601));
    }
    [Theory]
    [InlineData(101)] [InlineData(350)] [InlineData(600)]
    public void DoubleClickCancelsDelayedSingle(long second)
    {
        var clicks = new VideoClickState(); clicks.Release(100, 10, 20, 500, 4, 4);
        Assert.Equal(PointerClick.Double, clicks.Release(second, 10, 20, 500, 4, 4));
        Assert.False(clicks.Pending); Assert.False(clicks.Expire(1200));
    }
    [Theory]
    [InlineData(601, 10, 20)] [InlineData(200, 13, 20)] [InlineData(200, 10, 23)]
    public void TimeOrPositionOutsideSystemThresholdStartsAnotherSingle(long second, double x, double y)
    {
        var clicks = new VideoClickState(); clicks.Release(100, 10, 20, 500, 4, 4);
        Assert.Equal(PointerClick.PendingSingle, clicks.Release(second, x, y, 500, 4, 4));
        Assert.True(clicks.Expire(second+500));
    }
    [Fact]
    public void ThirdClickDoesNotRepeatFullscreenOrScheduleHide()
    {
        var clicks = new VideoClickState(); clicks.Release(100, 10, 20, 500, 4, 4); clicks.Release(200, 10, 20, 500, 4, 4);
        Assert.Equal(PointerClick.None, clicks.Release(300, 10, 20, 500, 4, 4)); Assert.False(clicks.Pending);
        Assert.Equal(PointerClick.PendingSingle, clicks.Release(900, 10, 20, 500, 4, 4));
    }
    [Fact]
    public void LeavingAndReenteringCancelsOldHideButChildTransitionsDoNot()
    {
        var clicks = new VideoClickState(); clicks.Release(100, 10, 20, 500, 4, 4);
        clicks.ObserveInside(true); clicks.ObserveInside(true); Assert.True(clicks.Pending);
        clicks.ObserveInside(false); Assert.True(clicks.Pending);
        clicks.ObserveInside(true); Assert.False(clicks.Expire(600));
    }
    [Fact]
    public void CancelRemovesGestureHistoryBeforeNextMediaOrKeyboardAction()
    {
        var clicks = new VideoClickState(); clicks.Release(100, 10, 20, 500, 4, 4); clicks.Cancel();
        Assert.False(clicks.Expire(600)); Assert.Equal(PointerClick.PendingSingle, clicks.Release(200, 10, 20, 500, 4, 4));
    }
}

using Mani_App;

namespace Mani.UnitTests;

public class CursorIdleTests
{
    [Fact]
    public void FirstEntryHidesAtDefaultDeadlineWithoutReentry()
    {
        var state = new CursorIdleState();
        state.Move(300, 200, 100);
        state.Eligible(true, 100);
        state.Tick(2599);
        Assert.False(state.Hidden);
        Assert.Equal(1, state.Remaining(2599));
        state.Tick(2600);
        Assert.True(state.Hidden);
        Assert.False(state.Waiting);
    }
    [Fact]
    public void StationaryLayoutEventsDoNotRestartInactivityOrRevealCursor()
    {
        var state = new CursorIdleState();
        state.Move(300, 200, 0);
        state.Eligible(true, 0);
        state.Move(300, 200, 2400);
        state.Tick(2500);
        Assert.True(state.Hidden);
        state.Move(300, 200, 3000);
        Assert.True(state.Hidden);
        state.Move(301, 200, 3100);
        Assert.False(state.Hidden);
        state.Tick(5599);
        Assert.False(state.Hidden);
        state.Tick(5600);
        Assert.True(state.Hidden);
    }
    [Fact]
    public void LeavingOrProtectedInputRecallsAndReentryGetsFullDelay()
    {
        var state = new CursorIdleState();
        state.Eligible(true, 0);
        state.Tick(2500);
        state.Eligible(false, 2600);
        Assert.False(state.Hidden);
        Assert.False(state.Waiting);
        state.Tick(10000);
        Assert.False(state.Hidden);
        state.Eligible(true, 11000);
        state.Tick(13499);
        Assert.False(state.Hidden);
        state.Tick(13500);
        Assert.True(state.Hidden);
    }
    [Fact]
    public void PollingEligibilityDoesNotPostponeDeadline()
    {
        var state = new CursorIdleState();
        state.Eligible(true, 0);
        for (int now = 200; now <= 2600; now += 200)
        { state.Eligible(true, now); state.Configure(2500, now); state.Tick(now); }
        Assert.True(state.Hidden);
    }
    [Theory]
    [InlineData(0, 0)]
    [InlineData(-1, 0)]
    [InlineData(1000, 1000)]
    [InlineData(long.MaxValue, int.MaxValue)]
    public void ConfiguredDelayIsBounded(long requested, long effective)
    {
        var state = new CursorIdleState();
        state.Configure(requested, 100);
        state.Eligible(true, 100);
        if (effective > 0) { state.Tick(100 + effective - 1); Assert.False(state.Hidden); }
        state.Tick(100 + effective);
        Assert.True(state.Hidden);
    }
    [Fact]
    public void DelayChangeRecallsAndStartsNewInterval()
    {
        var state = new CursorIdleState();
        state.Eligible(true, 0);
        state.Tick(2500);
        state.Configure(5000, 3000);
        Assert.False(state.Hidden);
        state.Tick(7999);
        Assert.False(state.Hidden);
        state.Tick(8000);
        Assert.True(state.Hidden);
    }
    [Fact]
    public void SingleClickHidesOnlyWhenEligible()
    {
        var state = new CursorIdleState();
        state.HideNow();
        Assert.False(state.Hidden);
        state.Eligible(true, 0);
        state.HideNow();
        Assert.True(state.Hidden);
        state.Eligible(false, 10);
        state.HideNow();
        Assert.False(state.Hidden);
    }
    [Fact]
    public void KeyboardRecallRestartsTimer()
    {
        var state = new CursorIdleState();
        state.Eligible(true, 0);
        state.HideNow();
        state.Wake(1000);
        Assert.False(state.Hidden);
        state.Tick(3499);
        Assert.False(state.Hidden);
        state.Tick(3500);
        Assert.True(state.Hidden);
    }
    [Fact]
    public void ResetDisarmsTimerAndClearsOldPointerPosition()
    {
        var state = new CursorIdleState();
        state.Move(300, 200, 0);
        state.Eligible(true, 0);
        state.HideNow();
        state.Reset();
        state.Tick(10000);
        Assert.False(state.Hidden);
        Assert.False(state.Waiting);
        state.Eligible(true, 10000);
        state.Move(300, 200, 11000);
        state.Tick(13499);
        Assert.False(state.Hidden);
        state.Tick(13500);
        Assert.True(state.Hidden);
    }
}

using Mani.Playback;
using Mani_App;

namespace Mani.UnitTests;

public sealed class OscDockingTests
{
    [Theory]
    [InlineData(OscPosition.Top, false, true, true)]
    [InlineData(OscPosition.Top, true, true, false)]
    [InlineData(OscPosition.Bottom, false, true, true)]
    [InlineData(OscPosition.Bottom, true, true, true)]
    [InlineData(OscPosition.Floating, false, false, false)]
    [InlineData(OscPosition.Floating, true, false, false)]
    public void PositionAndFullscreenFollowFixedIinaVisibilityRules(OscPosition position,
        bool fullscreen, bool docked, bool alwaysShown)
    {
        var plan = OscDockingLayout.Create(position, false, true, true, fullscreen, 500, 60);
        Assert.Equal(docked, plan.Docked);
        Assert.Equal(alwaysShown, plan.AlwaysShown);
        Assert.Equal(docked ? 60 : 0, plan.Top + plan.Bottom);
        Assert.Equal(position == OscPosition.Top && docked ? 60 : 0, plan.Top);
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, true)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    public void EdgeToEdgeOffDockPreferenceAndMediaAreAllRequired(bool edgeToEdge, bool dock, bool media)
    {
        foreach (var position in new[] { OscPosition.Top, OscPosition.Bottom })
            Assert.Equal(default, OscDockingLayout.Create(position, edgeToEdge, dock, media, false, 500, 60));
    }

    [Theory]
    [InlineData(OscPosition.Top, 0, 60)]
    [InlineData(OscPosition.Bottom, 0, 60)]
    [InlineData(OscPosition.Top, 20, 60)]
    [InlineData(OscPosition.Bottom, 20, 60)]
    [InlineData(OscPosition.Top, 500, -1)]
    [InlineData(OscPosition.Bottom, 500, -1)]
    public void TransientMeasurementNeverProducesANegativeViewport(OscPosition position, double height, double measured)
    {
        var plan = OscDockingLayout.Create(position, false, true, true, false, height, measured);
        Assert.InRange(plan.Top + plan.Bottom, 0, Math.Max(0, height - 1));
    }

    [Fact]
    public void UndockingRestartsTheIdleTimeoutAfterAnExtendedHold()
    {
        var state = new FloatingOscState();
        state.SetActive(true, 0, false);
        state.Hold(OscHold.Docked, true, 0, false);
        state.Leave(20, false);
        state.Tick(100_000, false);
        Assert.True(state.Visible && state.HitTestVisible);
        Assert.False(state.NeedsTick);
        state.Hold(OscHold.Docked, false, 100_000, false);
        state.Tick(102_499, false);
        Assert.True(state.Visible);
        state.Tick(102_500, false);
        Assert.False(state.Visible || state.HitTestVisible);
    }

    [Fact]
    public void FullscreenTopKeepsItsInsetAndRespectsOtherHoldsWhenAutomaticHidingResumes()
    {
        var windowed = OscDockingLayout.Create(OscPosition.Top, false, true, true, false, 500, 44);
        var full = OscDockingLayout.Create(OscPosition.Top, false, true, true, true, 1200, 52);
        Assert.True(windowed.AlwaysShown);
        Assert.False(full.AlwaysShown);
        Assert.Equal(52, full.Top);
        var state = new FloatingOscState();
        state.SetActive(true, 0, false);
        state.Hold(OscHold.Docked, true, 0, false);
        state.Hold(OscHold.Keyboard, true, 100, false);
        state.Hold(OscHold.Docked, false, 200, false);
        state.Tick(10_000, false);
        Assert.True(state.Visible);
        state.Hold(OscHold.Keyboard, false, 10_000, false);
        state.Tick(12_500, false);
        Assert.False(state.Visible);
        Assert.Equal(52, full.Top);
    }
}

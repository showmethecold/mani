using Mani_App;

namespace Mani.UnitTests;

public sealed class FloatingOscTests
{
    [Theory]
    [InlineData(OscHold.Keyboard)]
    [InlineData(OscHold.Menu)]
    internal void ZeroDelayPointerTrafficCannotKeepRestartingTheFadeAfterInteraction(OscHold interaction)
    {
        var osc = new FloatingOscState();
        osc.Configure(true, 0, 0, true);
        osc.SetActive(true, 0, false);
        osc.Hold(interaction, true, 0, false);
        Assert.Equal(1, osc.Opacity);
        osc.Hold(interaction, false, 500, true);
        osc.Tick(500, true);
        // Real movement while leaving a sidebar/menu must not turn a 250 ms
        // fade into an unbounded sequence of show/hide reversals.
        for (long now = 508; now <= 1004; now += 8)
        {
            osc.Wake(now, true);
            osc.Tick(now, true);
            if (now >= 750)
            {
                Assert.False(osc.Visible);
                Assert.False(osc.NeedsTick);
            }
        }
        // Keyboard/menu interaction remains accessible even with delay zero.
        osc.Hold(interaction, true, 1100, true);
        osc.Tick(1350, true);
        Assert.Equal(1, osc.Opacity);
        Assert.True(osc.HitTestVisible);
    }

    [Fact]
    public void IdleDeadlineAndFadeHaveSeparateBoundaries()
    {
        var osc = new FloatingOscState();
        osc.SetActive(true, 1000, true);
        osc.Tick(1250, true);
        Assert.Equal(1, osc.Opacity);
        osc.Tick(3499, true);
        Assert.True(osc.HitTestVisible);
        osc.Tick(3500, true);
        Assert.False(osc.HitTestVisible);
        Assert.True(osc.Visible);
        osc.Tick(3750, true);
        Assert.False(osc.Visible);
        Assert.False(osc.NeedsTick);
    }
    [Fact]
    public void ReversingFadeStartsAtItsCurrentOpacityAndCannotCollapseLater()
    {
        var osc = new FloatingOscState();
        osc.SetActive(true, 0, false);
        osc.Tick(2500, true);
        osc.Tick(2625, true);
        Assert.Equal(.5, osc.Opacity, 8);
        osc.Wake(2625, true);
        Assert.Equal(.5, osc.Opacity, 8);
        osc.Tick(2875, true);
        Assert.Equal(1, osc.Opacity);
        Assert.True(osc.Visible);
        Assert.True(osc.HitTestVisible);
    }
    [Fact]
    public void HidingDuringShowAlsoReversesWithoutAJump()
    {
        var osc = new FloatingOscState();
        osc.SetActive(true, 0, true);
        osc.Tick(125, true);
        osc.Leave(125, true);
        Assert.Equal(.5, osc.Opacity, 8);
        osc.Tick(375, true);
        Assert.False(osc.Visible);
    }
    [Theory]
    [InlineData(OscHold.Hover)]
    [InlineData(OscHold.Menu)]
    [InlineData(OscHold.Capture)]
    [InlineData(OscHold.Keyboard)]
    [InlineData(OscHold.Drag)]
    [InlineData(OscHold.Dialog)]
    [InlineData(OscHold.Diagnostics)]
    internal void EachInteractionHoldsVisibilityAndReleaseRestartsTimeout(OscHold reason)
    {
        var osc = new FloatingOscState();
        osc.SetActive(true, 0, false);
        osc.Hold(reason, true, 100, false);
        osc.Leave(200, false);
        osc.Tick(10000, false);
        Assert.True(osc.Visible);
        Assert.False(osc.NeedsTick);
        osc.Hold(reason, false, 10000, false);
        osc.Tick(12499, false);
        Assert.True(osc.Visible);
        osc.Tick(12500, false);
        Assert.False(osc.Visible);
    }
    [Fact]
    public void ReleasingCaptureCannotCancelAnOpenMenuHold()
    {
        var osc = new FloatingOscState();
        osc.SetActive(true, 0, false);
        osc.Hold(OscHold.Capture | OscHold.Menu, true, 0, false);
        osc.Hold(OscHold.Capture, false, 100, false);
        osc.Tick(5000, false);
        Assert.True(osc.Visible);
        osc.Hold(OscHold.Menu, false, 5100, false);
        osc.Tick(7600, false);
        Assert.False(osc.Visible);
    }
    [Fact]
    public void DisablingAnimationsSettlesTheCurrentTransitionImmediately()
    {
        var osc = new FloatingOscState();
        osc.SetActive(true, 0, true);
        osc.Tick(50, false);
        Assert.Equal(1, osc.Opacity);
        osc.Leave(100, false);
        Assert.Equal(0, osc.Opacity);
        Assert.False(osc.Visible);
        Assert.False(osc.HitTestVisible);
    }
    [Fact]
    public void DeactivationCancelsAnimationAndInteractionState()
    {
        var osc = new FloatingOscState();
        osc.SetActive(true, 0, false);
        osc.Hold(OscHold.Drag, true, 10, false);
        osc.SetActive(false, 100, true);
        osc.Wake(150, false);
        Assert.False(osc.Visible);
        Assert.Equal(OscHold.None, osc.Holds);
        osc.SetActive(true, 200, false);
        osc.Tick(2700, false);
        Assert.False(osc.Visible);
    }
    [Fact]
    public void DefaultPositionIsCenteredAndTenPercentFromBottom()
    {
        var bounds = FloatingOscLayout.Place(960, 540, 102, .5, .1);
        Assert.Equal(250, bounds.X);
        Assert.Equal(384, bounds.Y);
        Assert.Equal(460, bounds.Width);
    }
    [Theory]
    [InlineData(200, 120)]
    [InlineData(320, 240)]
    [InlineData(460, 260)]
    [InlineData(960, 540)]
    [InlineData(1280, 720)]
    [InlineData(40, 20)]
    [InlineData(0, 0)]
    public void ResizeAndExtremeSavedPositionsStayInsideVideo(double width, double height)
    {
        foreach (double horizontal in new[] { 0d, .5, 1 })
        foreach (double bottom in new[] { 0d, .1, 1 })
        {
            var b = FloatingOscLayout.Place(width, height, 102, horizontal, bottom);
            Assert.InRange(b.X, 0, width);
            Assert.InRange(b.Y, 0, height);
            Assert.True(b.X + b.Width <= width + .001);
            Assert.True(b.Y + b.Height <= height + .001);
            var drag = FloatingOscLayout.Drag(b, width, height, -9999, 9999);
            Assert.InRange(drag.X, 0, width);
            Assert.True(drag.Y + drag.Height <= height + .001);
        }
    }
    [Fact]
    public void DragSnapsToCenterAndNormalizedPositionSurvivesResize()
    {
        var initial = FloatingOscLayout.Place(960, 540, 102, .5, .1);
        var moved = FloatingOscLayout.Drag(initial, 960, 540, 254, 150);
        Assert.Equal(250, moved.X);
        var saved = FloatingOscLayout.Normalize(moved, 960, 540);
        var restored = FloatingOscLayout.Place(960, 540, 102, saved.Horizontal, saved.Bottom);
        Assert.Equal(moved.Y, restored.Y, 8);
        Assert.Equal(moved.X, restored.X);
        var narrow = FloatingOscLayout.Place(320, 240, 102, saved.Horizontal, saved.Bottom);
        Assert.Equal(1, narrow.X);
    }
}

using Mani_App;
using Mani.Playback;

namespace Mani.UnitTests;

public sealed class LayoutSidebarTests
{
    [Fact]
    public void FittingAShortPortraitWindowRecalculatesTheSidebarEightyPercentCap()
    {
        var frame = new WindowFrame(100, 200, 1200, 264);
        var result = WindowSizing.FitVideo(frame, new(9, 16), new(654, 64), 630);
        double sidebar = (result.Width - 24) * .8;
        Assert.Equal(450, sidebar, 8);
        Assert.Equal(9.0 / 16, (result.Width - 24 - sidebar) / (result.Height - 64), 8);
        Assert.Equal(700, result.X + result.Width / 2, 8);
    }
    [Fact]
    public void ToggleIgnoresRequestsUntilTheIinaTransitionCompletes()
    {
        var state = new LayoutSidebarState();
        Assert.True(state.Toggle(0, true));
        Assert.True(state.Visible); Assert.True(state.WantsVisible);
        Assert.False(state.Toggle(120, true));
        Assert.Equal(.25, state.Expansion, 8);
        Assert.True(state.Toggle(240, true));
        Assert.False(state.WantsVisible); Assert.True(state.Visible);
        state.Tick(480);
        Assert.False(state.Visible); Assert.Equal(0, state.Expansion);
    }
    [Fact]
    public void ANewShowCannotBeCollapsedByAnOlderHideDeadline()
    {
        var state = new LayoutSidebarState();
        state.SetVisible(true, 0, false);
        state.SetVisible(false, 10, true); state.Tick(130);
        Assert.Equal(.75, state.Expansion, 8);
        state.SetVisible(true, 130, true); state.Tick(250);
        Assert.True(state.WantsVisible); Assert.True(state.Visible);
        Assert.Equal(SidebarPhase.Showing, state.Phase);
        state.Tick(370); Assert.Equal(SidebarPhase.Shown, state.Phase);
    }
    [Fact]
    public void DisabledAnimationsAndForcedRepositionSettleImmediately()
    {
        var state = new LayoutSidebarState();
        state.Toggle(0, false); Assert.Equal(1, state.Expansion);
        state.SetVisible(false, 10, false); Assert.False(state.Visible);
        state.SetVisible(true, 10, false); state.Tick(10000);
        Assert.Equal(SidebarPhase.Shown, state.Phase);
    }
    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void TurningOffSystemAnimationsFinishesTheCurrentTransition(bool opening)
    {
        var state = new LayoutSidebarState();
        state.SetVisible(!opening, 0, false);
        state.SetVisible(opening, 10, true); state.Tick(70);
        Assert.True(state.Animating);
        state.Tick(80, false);
        Assert.False(state.Animating); Assert.Equal(opening, state.Visible);
        Assert.Equal(opening ? 1 : 0, state.Expansion);
        state.Tick(500); Assert.Equal(opening, state.Visible);
    }
    [Fact]
    public void ClosingDuringRevealStartsAtCurrentPosition()
    {
        var state = new LayoutSidebarState();
        state.SetVisible(true, 0, true); state.Tick(120);
        state.SetVisible(false, 120, true);
        Assert.Equal(.25, state.Expansion, 8);
        state.Tick(360); Assert.Equal(SidebarPhase.Hidden, state.Phase);
    }
    [Theory]
    [InlineData(1000, 360, 360)]
    [InlineData(1000, 100, 300)]
    [InlineData(1000, 900, 500)]
    [InlineData(400, 360, 320)]
    [InlineData(285, 360, 228)]
    [InlineData(100, 500, 80)]
    [InlineData(0, 360, 0)]
    public void WidthFitsTheAvailableWindowWithoutDestroyingTheSavedValue(double host, double requested, double expected) =>
        Assert.Equal(expected, SidebarLayout.ClampWidth(requested, host), 8);

    [Theory]
    [InlineData(true, true)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(false, false)]
    public void VideoIsInsetOnlyWhenEdgeToEdgeIsOff(bool left, bool edge)
    {
        var plan = SidebarLayout.Create(1000, 360, 1, left, edge);
        Assert.Equal(360, plan.Occupied);
        Assert.Equal(left ? 360 : 0, plan.OccupiedLeft);
        Assert.Equal(left ? 0 : 360, plan.OccupiedRight);
        Assert.Equal(edge ? 0 : 360, plan.VideoLeft + plan.VideoRight);
        Assert.Equal(0, plan.Translation);
        Assert.Equal(left ? 360 : 636, plan.GripX);
    }
    [Fact]
    public void ClosingReturnsAllReservedSpaceAndNoStaleStripRemains()
    {
        var partial = SidebarLayout.Create(1000, 400, .5, false, false);
        Assert.Equal(200, partial.VideoRight); Assert.Equal(200, partial.Translation);
        var hidden = SidebarLayout.Create(1000, 400, 0, true, false);
        Assert.Equal(0, hidden.Occupied); Assert.Equal(0, hidden.VideoLeft);
        Assert.Equal(-400, hidden.Translation);
    }
    [Theory]
    [InlineData(402, true, 400)] [InlineData(598, false, 400)]
    [InlineData(40, true, 300)] [InlineData(50, false, 500)]
    public void DragUsesTheInnerEdgeAndTwoPointOffset(double pointerX, bool left, double expected) =>
        Assert.Equal(expected, SidebarLayout.DragWidth(pointerX, 1000, left));

    [Theory]
    [InlineData(OscPosition.Floating)] [InlineData(OscPosition.Top)] [InlineData(OscPosition.Bottom)]
    public void ControlsRemainInsideTheRegionNotCoveredByTheSidebar(OscPosition position)
    {
        foreach (bool left in new[] { false, true })
        {
            var sidebar = SidebarLayout.Create(1097, 360, 1, left, true);
            var osc = OscLayout.Create(position, 1097 - sidebar.Occupied, 617, 94, .5, .1, false);
            double x = osc.Bounds.X + sidebar.OccupiedLeft;
            Assert.True(x >= sidebar.OccupiedLeft);
            Assert.True(x + osc.Bounds.Width <= 1097 - sidebar.OccupiedRight + .001);
        }
    }
    [Theory]
    [InlineData(16, 9)] [InlineData(9, 16)] [InlineData(4, 3)]
    public void FitVideoCropsOnlyTheVideoRectangleAndRetainsSidebarAndCaption(double width, double height)
    {
        var frame = new WindowFrame(400, 200, 1800, 1300);
        var chrome = new VideoSize(654, 164);
        var result = WindowSizing.FitVideo(frame, new(width, height), chrome);
        Assert.Equal(width / height, (result.Width - chrome.Width) / (result.Height - chrome.Height), 8);
        Assert.True(result.Width <= frame.Width && result.Height <= frame.Height);
        Assert.Equal(frame.X + frame.Width / 2, result.X + result.Width / 2, 8);
        Assert.Equal(frame.Y + frame.Height / 2, result.Y + result.Height / 2, 8);
    }
}

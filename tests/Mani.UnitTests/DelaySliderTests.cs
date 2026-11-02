using Mani_App;

namespace Mani.UnitTests;

public sealed class DelaySliderTests
{
    private static readonly DelayMedia Media = new(3, 8, 2);

    [Theory]
    [InlineData(1.5, "1.5s")]
    [InlineData(-.05, "-0.05s")]
    [InlineData(-.00000001, "0s")]
    [InlineData(.12345601, "0.123456s")]
    public void ThumbLabelPreservesFractionalSeconds(double value, string expected)
        => Assert.Equal(expected, DelayEdit.FormatSeconds(value));

    [Theory]
    [InlineData(.025, true, .05)]
    [InlineData(-.025, true, -.05)]
    [InlineData(1.123, true, 1.1)]
    [InlineData(-1.127, true, -1.15)]
    [InlineData(.25, false, .5)]
    [InlineData(-.25, false, -.5)]
    [InlineData(1.24, false, 1)]
    [InlineData(1.26, false, 1.5)]
    [InlineData(7, true, 5)]
    [InlineData(-7, false, -5)]
    public void PointerPreviewUsesIinaAudioVirtualStepsAndSubtitleTickMarks(double input, bool audio, double expected)
        => Assert.Equal(expected, DelayEdit.Snap(input, audio), 8);

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void NonFinitePreviewIsRejected(double value)
        => Assert.Throws<ArgumentOutOfRangeException>(() => DelayEdit.Snap(value, true));

    [Fact]
    public void ManyPreviewsProduceOnlyOneFinalValueOnRelease()
    {
        var edit = new DelayEdit();
        edit.Begin(Media, 0, true);
        foreach (double value in new[] { .16, .62, 1.11 }) edit.Preview(value);
        Assert.True(edit.Active);
        Assert.Equal(1.1, edit.Finish(Media, true));
        Assert.Null(edit.Finish(Media, true));
    }

    [Theory]
    [InlineData(4, 8, 2)]
    [InlineData(3, 9, 2)]
    [InlineData(3, 8, 3)]
    public void ChangedMediaOrUiRevisionCannotReceiveAnOldDrag(long request, long entry, int revision)
    {
        var edit = new DelayEdit();
        edit.Begin(Media, 0, true);
        edit.Preview(1);
        Assert.Null(edit.Finish(new(request, entry, revision), true));
        Assert.False(edit.Active);
    }

    [Fact]
    public void CancelDoesNotApplyAndNextGestureCanCommit()
    {
        var edit = new DelayEdit();
        edit.Begin(Media, 0, false);
        edit.Preview(2);
        Assert.Null(edit.Finish(Media, false));
        edit.Begin(Media, 0, false);
        edit.Preview(-1.3);
        Assert.Equal(-1.5, edit.Finish(Media, true));
    }

    [Fact]
    public void RangeClampingOnlyProjectsTheDisplayedPosition()
    {
        Assert.Equal(5, DelayEdit.Position(12.345678));
        Assert.Equal(-5, DelayEdit.Position(-12.345678));
        Assert.Equal(.123456, DelayEdit.Position(.123456));
        var edit = new DelayEdit();
        edit.Begin(Media, 12.345678, true);
        Assert.Null(edit.Finish(Media, false));
    }

    [Fact]
    public void PrimaryAndSecondaryGesturesKeepIndependentValues()
    {
        var primary = new DelayEdit();
        var secondary = new DelayEdit();
        primary.Begin(Media, 0, false);
        secondary.Begin(Media, 0, false);
        primary.Preview(1.2);
        secondary.Preview(-2.2);
        Assert.Equal(1, primary.Finish(Media, true));
        Assert.Equal(-2, secondary.Finish(Media, true));
    }
}

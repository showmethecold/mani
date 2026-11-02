using Mani.Playback;
using Mani_App;

namespace Mani.UnitTests;

public sealed class QuickSettingsTests
{
    [Theory]
    [InlineData("vid", "video")]
    [InlineData("aid", "audio")]
    [InlineData("sid", "sub")]
    public void CycleRequiresAnExistingTypeButAllowsDeselectedTracks(string property, string type)
    {
        Assert.False(QuickSettings.CanCycleTrack(Info(), property));
        Assert.False(QuickSettings.CanCycleTrack(Info(Track(91, "unrelated")), property));
        var info = Info(Track(91, type)) with { VideoTrack = "no", AudioTrack = "no", SubtitleTrack = "no" };
        Assert.True(QuickSettings.CanCycleTrack(info, property));
    }
    private static MediaTrack Track(long id, string type, string? title = null, string? language = null) =>
        new(id, type, title, language, true, false, false, false, false, false,
            null, null, "test", null, 1920, 1080, 24, 2, null, 48000, new Dictionary<string, object?>());
    private static MediaInfo Info(params MediaTrack[] tracks) => new(5, 9, "video.mkv", "Video", 30,
        tracks, [], -1, "7", "12", "34", "56", new Dictionary<string, object?>());

    [Theory]
    [InlineData("vid", "video")]
    [InlineData("aid", "audio")]
    [InlineData("sid", "sub")]
    [InlineData("secondary-sid", "sub")]
    public void TrackRowsPreserveEngineOrderAndIdsRatherThanUsingRowNumbers(string property, string type)
    {
        var rows = QuickSettings.Tracks(Info(Track(91, type), Track(7, type)), property, "None", "No Title", "Default");
        Assert.Equal(new long[] { 0, 91, 7 }, rows.Select(t => t.Id));
        Assert.Equal(2, QuickSettings.SelectedIndex(rows, "7"));
        Assert.Equal(0, QuickSettings.SelectedIndex(rows, "no"));
        Assert.Equal(-1, QuickSettings.SelectedIndex(rows, "22"));
        Assert.Equal(-1, QuickSettings.SelectedIndex(rows, "auto"));
    }
    [Fact]
    public void PrimaryAndSecondaryShareChoicesButKeepIndependentSelection()
    {
        var info = Info(Track(7, "video"), Track(12, "audio"), Track(34, "sub"), Track(56, "sub"));
        var primary = QuickSettings.Tracks(info, "sid", "None", "No Title", "Default");
        var secondary = QuickSettings.Tracks(info, "secondary-sid", "None", "No Title", "Default");
        Assert.Equal(primary, secondary);
        Assert.Equal(1, QuickSettings.SelectedIndex(primary, info.SubtitleTrack));
        Assert.Equal(2, QuickSettings.SelectedIndex(secondary, info.SecondarySubtitleTrack));
    }
    [Fact]
    public void LabelsPreserveUnicodeAndFallbackWithoutInventingLanguage()
    {
        var rows = QuickSettings.Tracks(Info(Track(1, "audio", "日本語 / 中文", "jpn"), Track(2, "audio", null, "und")), "aid", "None", "No Title", "Default");
        Assert.Contains("[jpn] 日本語 / 中文", rows[1].Label);
        Assert.Contains("2ch, 48kHz, Default", rows[1].Label);
        Assert.Contains("No Title", rows[2].Label);
        Assert.DoesNotContain("und", rows[2].Label);
    }

    [Theory]
    [InlineData("sid", 56, "34", "56", true)]
    [InlineData("secondary-sid", 34, "34", "56", true)]
    [InlineData("sid", 56, "no", "56", true)]
    [InlineData("secondary-sid", 34, "34", "no", true)]
    [InlineData("sid", 56, "56", "no", false)]
    [InlineData("secondary-sid", 34, "no", "34", false)]
    [InlineData("sid", 0, "no", "0", false)]
    [InlineData("secondary-sid", 0, "0", "no", false)]
    [InlineData("sid", 34, "34", "56", false)]
    [InlineData("secondary-sid", 56, "34", "56", false)]
    [InlineData("aid", 56, "34", "56", false)]
    [InlineData("vid", 34, "34", "56", false)]
    [InlineData("sid", 56, "34", "auto", false)]
    [InlineData("sid", 56, "56", "56", false)]
    public void ConflictExplainsOnlyRejectedSubtitleSelection(string property, long requested,
        string primary, string secondary, bool expected)
    {
        var actual = Info() with { SubtitleTrack = primary, SecondarySubtitleTrack = secondary };
        Assert.Equal(expected, QuickSettings.SubtitleConflict(actual, property, requested));
    }
    [Fact]
    public void EmptyTrackGroupOffersOnlyNoneAndUnknownPropertyIsRejected()
    {
        Assert.Single(QuickSettings.Tracks(Info(Track(1, "video")), "aid", "None", "No Title", "Default"));
        Assert.Throws<ArgumentOutOfRangeException>(() => QuickSettings.TrackType("volume"));
    }
    [Theory]
    [InlineData(0, .25)] [InlineData(8, 1)] [InlineData(16, 4)] [InlineData(24, 16)]
    public void SpeedSliderFollowsIinaLogarithmicScale(double step, double speed)
    {
        Assert.Equal(speed, QuickSettings.SpeedFromStep(step));
        Assert.Equal(step, QuickSettings.StepFromSpeed(speed), 8);
    }
    [Theory]
    [InlineData(-10, .01)] [InlineData(1.1234565, 1.123456)] [InlineData(1.1234566, 1.123457)]
    [InlineData(1.1234575, 1.123457)] [InlineData(32, 32)]
    public void TextSpeedPreservesSixDigitsHalfDownAndAllowsBeyondSliderRange(double input, double expected) =>
        Assert.Equal(expected, QuickSettings.ConstrainSpeed(input), 6);
    [Fact]
    public void InvalidSpeedCannotBeSentToEngine()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => QuickSettings.ConstrainSpeed(double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => QuickSettings.ConstrainSpeed(double.PositiveInfinity));
    }
    [Fact]
    public void SamePathReopenAndLoadingInvalidateOldTrackModel()
    {
        var info = Info();
        var state = PlaybackFixtures.Playing() with { Path = info.Path, RequestId = 5, PlaylistEntryId = 9 };
        Assert.True(QuickSettings.Matches(info, state));
        Assert.False(QuickSettings.Matches(info, state with { RequestId = 6 }));
        Assert.False(QuickSettings.Matches(info, state with { PlaylistEntryId = 10 }));
        Assert.False(QuickSettings.Matches(info, state with { Loading = true }));
        Assert.False(QuickSettings.Matches(info, state with { Idle = true }));
    }
    [Fact]
    public void DifferentTabChangesInPlaceButSameTabTogglesOff()
    {
        Assert.True(QuickSettings.ToggleOff(true, QuickPane.Video, QuickPane.Video));
        Assert.False(QuickSettings.ToggleOff(true, QuickPane.Layout, QuickPane.Audio));
        Assert.False(QuickSettings.ToggleOff(false, QuickPane.Video, QuickPane.Video));
    }
}

using Mani.Playback;
using Mani_App;

namespace Mani.UnitTests;

public sealed class QuickOsdFeedbackTests
{
    private static MediaTrack Track(long id, string type, string? title = null) =>
        new(id, type, title, "jpn", false, false, true, false, false, false,
            null, null, "test", null, null, null, null, null, null, null, new Dictionary<string, object?>());
    private static MediaInfo Info() => new(7, 13, "日本語.mkv", "日本語", 40,
        [Track(91, "video"), Track(91, "audio", "音声"), Track(91, "sub", "字幕"), Track(92, "sub", "副字幕")],
        [], -1, "1", "2", "3", "4", new Dictionary<string, object?>());
    private static Dictionary<string, string> Values(string property, string value) => new() { [property] = value };
    private static OsdMessage? Complete(QuickOsdFeedback request, MediaInfo info, IReadOnlyDictionary<string, string>? values = null) =>
        request.Complete(info, values ?? new Dictionary<string, string>(), "<None>", "No Title", "Default");

    [Theory]
    [InlineData("vid", nameof(OsdKind.VideoTrack))]
    [InlineData("aid", nameof(OsdKind.AudioTrack))]
    [InlineData("sid", nameof(OsdKind.SubtitleTrack))]
    [InlineData("secondary-sid", nameof(OsdKind.SubtitleTrack))]
    public void TrackFeedbackUsesConfirmedIdAndCorrectTrackGroup(string property, string expected)
    {
        var info = Info();
        var request = QuickOsdFeedback.Begin(property, info, Values("unused", ""))!;
        var actual = info with { VideoTrack = "91", AudioTrack = "91", SubtitleTrack = "91", SecondarySubtitleTrack = "91" };
        var message = Complete(request, actual)!;
        Assert.Equal(expected, message.Kind.ToString());
        Assert.StartsWith("#91 [jpn] ", message.Title);
        Assert.Contains(property switch { "vid" => "No Title", "aid" => "音声", _ => "字幕" }, message.Title);
        Assert.Null(message.Progress);
    }
    [Theory]
    [InlineData("vid")][InlineData("aid")][InlineData("sid")][InlineData("secondary-sid")]
    public void NoneUsesIinaZeroIdAndNoneTitle(string property)
    {
        var info = Info();
        var request = QuickOsdFeedback.Begin(property, info, Values("unused", ""))!;
        Assert.Equal("#0 <None>", Complete(request, info with { VideoTrack = "no", AudioTrack = "no", SubtitleTrack = "no", SecondarySubtitleTrack = "no" })!.Title);
    }
    [Theory]
    [InlineData("sid")][InlineData("secondary-sid")]
    public void RejectedDuplicateTrackNeverProducesSuccessMessage(string property)
    {
        var info = Info();
        var request = QuickOsdFeedback.Begin(property, info, Values("unused", ""))!;
        Assert.Null(Complete(request, info));
    }
    [Fact]
    public void UnknownTrackOrNonNumericSelectionDoesNotInventTrackFeedback()
    {
        var info = Info();
        var request = QuickOsdFeedback.Begin("aid", info, Values("unused", ""))!;
        Assert.Null(Complete(request, info with { AudioTrack = "777" }));
        Assert.Null(Complete(request, info with { AudioTrack = "auto" }));
        Assert.Null(Complete(request, info with { AudioTrack = "-1" }));
    }
    [Theory]
    [InlineData("audio-delay", nameof(OsdKind.AudioDelay), -15, 0)]
    [InlineData("audio-delay", nameof(OsdKind.AudioDelay), -5, .25)]
    [InlineData("sub-delay", nameof(OsdKind.SubDelay), 5, .75)]
    [InlineData("sub-delay", nameof(OsdKind.SubDelay), 0, .5)]
    [InlineData("secondary-sub-delay", nameof(OsdKind.SecondarySubDelay), 10, 1)]
    [InlineData("secondary-sub-delay", nameof(OsdKind.SecondarySubDelay), 15, 1)]
    public void DelayProgressUsesIinaTenSecondBoundInsteadOfSliderFive(string property, string kind, double value, double progress)
    {
        var info = Info();
        var request = QuickOsdFeedback.Begin(property, info, Values(property, ".1"))!;
        var message = Complete(request, info, Values(property, value.ToString(System.Globalization.CultureInfo.InvariantCulture)))!;
        Assert.Equal(kind, message.Kind.ToString());
        Assert.Equal(value, message.Value);
        Assert.Equal(progress, message.Progress);
    }
    [Theory]
    [InlineData("0", "-0.000000")][InlineData(".25", "0.250000")]
    [InlineData("NaN", "1")][InlineData("0", "Infinity")][InlineData("0", "invalid")]
    public void MissingInvalidOrEquivalentValuesDoNotCreateFeedback(string before, string after)
    {
        var info = Info();
        var request = QuickOsdFeedback.Begin("audio-delay", info, Values("audio-delay", before))!;
        Assert.Null(Complete(request, info, Values("audio-delay", after)));
        Assert.Null(Complete(request, info));
    }
    [Theory]
    [InlineData("sub-visibility", "yes", "no", nameof(OsdKind.SubHidden))]
    [InlineData("sub-visibility", "no", "yes", nameof(OsdKind.SubVisible))]
    [InlineData("secondary-sub-visibility", "yes", "no", nameof(OsdKind.SecondarySubHidden))]
    [InlineData("secondary-sub-visibility", "no", "yes", nameof(OsdKind.SecondarySubVisible))]
    public void PrimaryAndSecondaryVisibilityHaveDistinctMessages(string property, string before, string after, string kind)
    {
        var info = Info();
        var request = QuickOsdFeedback.Begin(property, info, Values(property, before))!;
        Assert.Equal(kind, Complete(request, info, Values(property, after))!.Kind.ToString());
        Assert.Null(Complete(request, info, Values(property, before)));
        Assert.Null(Complete(request, info, Values(property, "unknown")));
    }
    [Fact]
    public void LoadingAndNewMediaCannotReuseFeedback()
    {
        var info = Info();
        var values = Values("sub-delay", "0");
        var request = QuickOsdFeedback.Begin("sub-delay", info, values)!;
        values["sub-delay"] = "2";
        Assert.NotNull(Complete(request, info, values)); // Before is a value, not a mutable dictionary reference.
        Assert.Null(Complete(request, info with { RequestId = 8 }, values));
        Assert.Null(Complete(request, info with { PlaylistEntryId = 14 }, values));
        Assert.Null(Complete(request, info with { Path = "other.mkv" }, values));
    }
    [Fact]
    public void UnsupportedOrUnobservedControlsDoNotQueueFeedback()
    {
        Assert.Null(QuickOsdFeedback.Begin("speed", Info(), Values("speed", "1")));
        Assert.Null(QuickOsdFeedback.Begin("volume", Info(), Values("volume", "50")));
        Assert.Null(QuickOsdFeedback.Begin("sub-delay", Info(), Values("unused", "")));
    }
    [Theory]
    [InlineData(-.123456, "0.123456")][InlineData(1.5, "1.5")]
    [InlineData(1000.25, "1,000.25")][InlineData(0, "0")]
    public void DelayTextKeepsSixDigitPrecisionAndGrouping(double value, string expected) =>
        Assert.Equal(expected, QuickOsdFeedback.FormatDelay(value));
    [Fact]
    public void InitializationGuardAlsoAppliesToQuickSettingFeedback()
    {
        var observer = new OsdPlaybackObserver();
        var state = PlaybackFixtures.Playing();
        Assert.False(observer.CanShowFeedback(state, 1000));
        observer.Observe(state, 1000);
        Assert.False(observer.CanShowFeedback(state, 1199));
        Assert.True(observer.CanShowFeedback(state, 1200));
        Assert.False(observer.CanShowFeedback(state with { RequestId = state.RequestId + 1 }, 1200));
        Assert.False(observer.CanShowFeedback(state with { Loading = true }, 1200));
        observer.Reset();
        Assert.False(observer.CanShowFeedback(state, 1200));
    }
}

using Mani.Playback;
using Mani_App;

namespace Mani.UnitTests;

public sealed class OsdTests
{
    [Theory]
    [InlineData(0, "00:00.000")]
    [InlineData(2.125, "00:02.125")]
    [InlineData(59.9994, "00:59.999")]
    [InlineData(59.9996, "01:00.000")]
    [InlineData(3599.9996, "1:00:00.000")]
    [InlineData(3600.125, "1:00:00.125")]
    [InlineData(360000, "100:00:00.000")]
    [InlineData(-1, "--:--:--")]
    [InlineData(double.NaN, "--:--:--")]
    [InlineData(double.PositiveInfinity, "--:--:--")]
    public void PreciseTimePortsMillisecondsAndMinuteHourCarry(double value, string expected) =>
        Assert.Equal(expected, OsdMessage.PreciseTime(value));

    [Fact]
    public void ExplicitProgressBypassesSeekFilterButNotOsdMasterSwitch()
    {
        var settings = new PlayerSettings { DisableOsdSeek = true };
        Assert.False(OsdPreferences.Allows(OsdKind.Seek, settings));
        Assert.True(OsdPreferences.Allows(OsdKind.ShowTime, settings));
        Assert.False(OsdPreferences.Allows(OsdKind.ShowTime, settings with { EnableOsd = false }));
    }

    [Fact]
    public void ExplicitProgressRefreshesTimeWithoutExtendingItsLifetime()
    {
        var osd = new OsdState(); osd.Show(OsdMessage.From(OsdKind.ShowTime, Media), 0);
        osd.RefreshTime(Media with { Position = 30.125 });
        Assert.Equal(30.125, osd.Message!.Position);
        Assert.Equal(30.125 / 120, osd.Message.Progress);
        osd.Tick(1500, true); Assert.False(osd.Visible);
    }

    [Theory]
    [InlineData(0)] [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)]
    public void UnknownDurationOmitsExplicitProgressBar(double duration) =>
        Assert.Null(new OsdMessage(OsdKind.ShowTime, Duration: duration).Progress);

    private static PlaybackSnapshot Media => PlaybackFixtures.Playing() with { RequestId = 7, FileLoadedCount = 3 };
    private static OsdPlaybackObserver Ready(PlaybackSnapshot? media = null)
    {
        var observer = new OsdPlaybackObserver();
        observer.Observe(media ?? Media, 0);
        observer.Observe(media ?? Media, 200);
        return observer;
    }
    [Fact]
    public void FileStartPreservesUnicodeAndDoesNotAnnounceInitialControlValues()
    {
        var observer = new OsdPlaybackObserver();
        var media = Media with { Title = "中文 # [日本語].mkv", Paused = true, Volume = 89, Speed = 2 };
        var first = observer.Observe(media, 0);
        Assert.True(first.Clear);
        Assert.Equal(OsdKind.File, first.Message!.Kind);
        Assert.Equal(media.Title, first.Message.Title);
        Assert.Null(observer.Observe(media with { Paused = false, Volume = 88 }, 199).Message);
        Assert.Null(observer.Observe(media with { Paused = false, Volume = 88 }, 200).Message);
    }
    [Fact]
    public void FailedAndLoadingSnapshotsClearPriorFeedbackWithoutAFileMessage()
    {
        foreach (var state in new[] { PlaybackState.Loading, PlaybackState.Failed })
        {
            var observer = Ready();
            var change = observer.Observe(Media with { State = state, Loading = state == PlaybackState.Loading }, 400);
            Assert.True(change.Clear);
            Assert.Null(change.Message);
        }
    }
    [Fact]
    public void StopIsAnnouncedOnceAndCannotKeepOldTime()
    {
        var observer = Ready();
        var stopped = Media with { State = PlaybackState.Stopped, Idle = true };
        var change = observer.Observe(stopped, 400);
        Assert.True(change.Clear);
        Assert.Equal(new OsdMessage(OsdKind.Stop), change.Message);
        Assert.Null(observer.Observe(stopped, 600).Message);
        Assert.False(observer.Observe(stopped, 800).Clear);
    }
    [Theory]
    [InlineData(true, OsdKind.Pause)]
    [InlineData(false, OsdKind.Resume)]
    internal void PauseMessagesFollowObservedState(bool paused, OsdKind expected)
    {
        var before = Media with { Paused = !paused };
        var observer = Ready(before);
        var change = observer.Observe(before with { Paused = paused, Position = 17.5 }, 400);
        Assert.Equal(expected, change.Message!.Kind);
        Assert.Equal(17.5, change.Message.Position);
        Assert.True(change.Message.HasTime);
        Assert.Null(observer.Observe(before with { Paused = paused, Position = 18 }, 600).Message);
    }
    [Theory]
    [InlineData(true, OsdKind.Mute)]
    [InlineData(false, OsdKind.Unmute)]
    internal void MuteMessagesFollowObservedState(bool muted, OsdKind expected)
    {
        var observer = Ready(Media with { Muted = !muted });
        Assert.Equal(expected, observer.Observe(Media with { Muted = muted }, 400).Message!.Kind);
    }
    [Fact]
    public void VolumeAndSpeedUseAcceptedValues()
    {
        var observer = Ready();
        var state = Media with { Volume = 63 };
        Assert.Equal(.63, observer.Observe(state, 400).Message!.Progress);
        state = state with { Speed = 1.234567 };
        Assert.Equal(1.234567, observer.Observe(state, 600).Message!.Value);
    }
    [Fact]
    public void OrdinaryClockChangesNeverGuessASeek()
    {
        var observer = Ready();
        Assert.Null(observer.Observe(Media with { Position = 90 }, 400).Message);
        Assert.Null(observer.Observe(Media with { Position = 1 }, 600).Message);
    }
    [Fact]
    public void CompletedSeekWaitsForSettledEnginePositionAndDoesNotUseRequestedTarget()
    {
        var observer = Ready();
        observer.SeekCompleted(Media);
        Assert.Null(observer.Observe(Media with { Seeking = true, Position = 15 }, 400).Message);
        var result = observer.Observe(Media with { Position = 29.75, Paused = true }, 600).Message!;
        Assert.Equal(OsdKind.Seek, result.Kind);
        Assert.Equal(29.75, result.Position);
        Assert.Equal(29.75 / 120, result.Progress);
        Assert.Null(observer.Observe(Media with { Position = 29.75, Paused = true }, 800).Message);
    }
    [Fact]
    public void FastSeekBetweenPollsStillHasOneConfirmedMessage()
    {
        var observer = Ready();
        observer.SeekCompleted(Media);
        Assert.Equal(OsdKind.Seek, observer.Observe(Media with { Position = 48 }, 400).Message!.Kind);
        Assert.Null(observer.Observe(Media with { Position = 48 }, 600).Message);
    }
    [Fact]
    public void ObservedExternalSeekAlsoReportsSettledPosition()
    {
        var observer = Ready();
        observer.Observe(Media with { Seeking = true }, 400);
        Assert.Equal(OsdKind.Seek, observer.Observe(Media with { Position = 70 }, 600).Message!.Kind);
    }
    [Fact]
    public void DelayedSeekFromOldRequestCannotAppearOnTheNewFile()
    {
        var observer = Ready();
        var next = Media with { RequestId = 8, FileLoadedCount = 4, Title = "B" };
        Assert.Equal(OsdKind.File, observer.Observe(next, 400).Message!.Kind);
        observer.SeekCompleted(Media);
        Assert.Null(observer.Observe(next, 600).Message);
    }
    [Fact]
    public void ReopeningTheSamePathStillStartsANewFileMessage()
    {
        var observer = Ready();
        var change = observer.Observe(Media with { FileLoadedCount = 4 }, 400);
        Assert.True(change.Clear);
        Assert.Equal(OsdKind.File, change.Message!.Kind);
    }
    [Fact]
    public void ResetBeforeAsyncOpenDiscardsOldSeekAndSnapshot()
    {
        var observer = Ready();
        observer.SeekCompleted(Media);
        observer.Reset();
        Assert.Equal(OsdKind.File, observer.Observe(Media, 400).Message!.Kind);
    }
    [Theory]
    [InlineData(-12, 0)]
    [InlineData(150, 1)]
    [InlineData(double.NaN, 0)]
    internal void ProgressIsBounded(double value, double expected) =>
        Assert.Equal(expected, new OsdMessage(OsdKind.Volume, Value: value).Progress);
    [Theory]
    [InlineData(0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    internal void UnknownDurationOmitsSeekProgress(double duration) =>
        Assert.Null(new OsdMessage(OsdKind.Seek, Duration: duration).Progress);
    [Fact]
    public void TimeoutHasOneSecondHoldAndHalfSecondFade()
    {
        var osd = new OsdState(); osd.Show(new(OsdKind.Pause), 100);
        osd.Tick(1099, true); Assert.Equal(1, osd.Opacity); Assert.Equal(1, osd.NextTick(1099));
        osd.Tick(1100, true); Assert.True(osd.Visible);
        osd.Tick(1350, true); Assert.Equal(.5, osd.Opacity);
        osd.Tick(1600, true); Assert.False(osd.Visible); Assert.Null(osd.Message);
    }
    [Fact]
    public void ReplacementDuringFadeCancelsTheOldDeadline()
    {
        var osd = new OsdState(); osd.Show(new(OsdKind.Pause), 0);
        osd.Tick(1250, true); Assert.Equal(.5, osd.Opacity);
        osd.Show(new(OsdKind.Volume, Value: 80), 1250);
        osd.Tick(1500, true); Assert.Equal(1, osd.Opacity);
        Assert.Equal(OsdKind.Volume, osd.Message!.Kind);
        osd.Tick(2750, true); Assert.False(osd.Visible);
    }
    [Fact]
    public void LiveTimeRefreshDoesNotExtendTimeout()
    {
        var osd = new OsdState(); osd.Show(OsdMessage.From(OsdKind.Resume, Media), 0);
        osd.RefreshTime(Media with { Position = 19 });
        Assert.Equal(19, osd.Message!.Position);
        osd.Tick(1500, true); Assert.False(osd.Visible);
    }
    [Fact]
    public void DisablingAnimationDuringFadeHidesImmediately()
    {
        var osd = new OsdState(); osd.Show(new(OsdKind.Mute), 0);
        osd.Tick(1200, true); Assert.True(osd.Visible);
        osd.Tick(1201, false); Assert.False(osd.Visible);
    }
    [Fact]
    public void ClearPreventsLateTimerWorkFromRestoringOldMessage()
    {
        var osd = new OsdState(); osd.Show(new(OsdKind.Seek), 0);
        osd.Clear(); osd.Tick(1250, true);
        Assert.False(osd.Visible); Assert.Null(osd.Message);
        osd.Show(new(OsdKind.File, "New"), 1500); Assert.Equal(1, osd.Opacity);
    }
}

using Mani.Playback;
using Mani_App;

namespace Mani.UnitTests;

public sealed class LoopFeedbackTests
{
    private static PlaybackSnapshot Media => PlaybackFixtures.Playing() with { RequestId = 7, FileLoadedCount = 3 };
    [Theory]
    [InlineData("0", "no", OsdKind.AbLoopA)]
    [InlineData("0", "2.5", OsdKind.AbLoopB)]
    [InlineData("no", "2.5", OsdKind.AbLoopA)]
    [InlineData("no", "no", OsdKind.AbLoopClear)]
    internal void AbLoopDistinguishesZeroFromUnset(string a, string b, OsdKind expected)
    {
        var before = Media with { AbLoopA = "1", AbLoopB = "3" };
        var after = Media with { AbLoopA = a, AbLoopB = b };
        Assert.Equal(expected, LoopFeedback.Changed(before, after));
    }
    [Theory]
    [InlineData("inf", "inf", OsdKind.FileLoop)]
    [InlineData("2", "force", OsdKind.FileLoop)]
    [InlineData("no", "force", OsdKind.PlaylistLoop)]
    [InlineData("0", "3", OsdKind.PlaylistLoop)]
    [InlineData("no", "no", OsdKind.LoopOff)]
    internal void LoopModeKeepsFilePrecedenceAndFiniteCounts(string file, string playlist, OsdKind expected)
    {
        Assert.Equal(expected, LoopFeedback.Changed(Media with { FileLoop = "1" }, Media with { FileLoop = file, PlaylistLoop = playlist }));
    }
    [Fact]
    public void AcceptedLoopChangeWinsOverItsSeekAndAnnouncesOnce()
    {
        var observer = new OsdPlaybackObserver();
        var before = Media with { AbLoopA = "1" };
        observer.Observe(before, 0);
        observer.Observe(before, 200);
        observer.SeekCompleted(before);
        var after = before with { AbLoopB = "3", Position = 1 };
        Assert.Equal(OsdKind.AbLoopB, observer.Observe(after, 400).Message!.Kind);
        Assert.Null(observer.Observe(after, 600).Message);
    }
    [Fact]
    public void NewMediaAndLoadingNeverLeakOldLoopFeedback()
    {
        var observer = new OsdPlaybackObserver();
        observer.Observe(Media with { AbLoopA = "1", AbLoopB = "3", FileLoop = "inf" }, 0);
        var replacement = Media with { RequestId = 8 };
        Assert.Equal(OsdKind.File, observer.Observe(replacement, 400).Message!.Kind);
        Assert.Null(observer.Observe(replacement, 600).Message);
        Assert.Null(observer.Observe(replacement with { State = PlaybackState.Loading, Loading = true, AbLoopA = "0" }, 800).Message);
    }
    [Fact]
    public void LoopFeedbackUsesExistingOsdSwitchAndTimeRules()
    {
        foreach (var kind in new[] { OsdKind.AbLoopA, OsdKind.AbLoopB, OsdKind.AbLoopClear, OsdKind.FileLoop, OsdKind.PlaylistLoop, OsdKind.LoopOff, OsdKind.Screenshot })
        {
            Assert.True(OsdPreferences.Allows(kind, new()));
            Assert.False(OsdPreferences.Allows(kind, new() { EnableOsd = false }));
            Assert.Equal(kind is OsdKind.AbLoopA or OsdKind.AbLoopB, new OsdMessage(kind).HasTime);
            Assert.Null(new OsdMessage(kind).Progress);
        }
    }
}

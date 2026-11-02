using Mani_App;

namespace Mani.UnitTests;

public sealed class PlaybackUiCacheTests
{
    [Fact]
    public void FirstSnapshotPopulatesAllControls()
    {
        Assert.Equal(PlaybackUiChanges.All, new PlaybackUiCache().Update(PlaybackFixtures.Playing(), "", true).Changes);
    }

    [Fact]
    public void PlaybackClockAndDiagnosticsDoNotRewriteStaticControls()
    {
        var cache = new PlaybackUiCache();
        var snapshot = PlaybackFixtures.Playing();
        cache.Update(snapshot, "", true);
        for (int i = 1; i <= 100; i++)
        {
            var next = snapshot with { Position = snapshot.Position + i * .2, AvSync = "0.001", DroppedFrames = "1" };
            Assert.Equal(PlaybackUiChanges.None, cache.Update(next, "", true).Changes);
        }
    }

    [Fact]
    public void PauseAndResumeUpdateOnlyPlaybackAndStatus()
    {
        var cache = new PlaybackUiCache();
        var snapshot = PlaybackFixtures.Playing();
        cache.Update(snapshot, "", true);
        var paused = snapshot with { Paused = true };
        Assert.Equal(PlaybackUiChanges.Playback | PlaybackUiChanges.Status, cache.Update(paused, "", true).Changes);
        Assert.Equal(PlaybackUiChanges.None, cache.Update(paused with { Position = 24 }, "", true).Changes);
        Assert.Equal(PlaybackUiChanges.Playback | PlaybackUiChanges.Status, cache.Update(snapshot, "", true).Changes);
    }

    [Fact]
    public void AReplacementLoadUpdatesTheTitleAndDisablesPlayAndSeeking()
    {
        var cache = new PlaybackUiCache();
        var snapshot = PlaybackFixtures.Playing();
        cache.Update(snapshot, "", true);
        var result = cache.Update(snapshot with { Loading = true, Title = "Next movie" }, "", true);
        Assert.Equal(PlaybackUiChanges.Title | PlaybackUiChanges.Playback | PlaybackUiChanges.Seeking | PlaybackUiChanges.Status, result.Changes);
        Assert.False(result.State.CanPlay);
        Assert.False(result.State.CanSeek);
    }

    [Fact]
    public void DiagnosticOnlyErrorsDoNotInvalidateTheCache()
    {
        var cache = new PlaybackUiCache();
        var snapshot = PlaybackFixtures.Playing();
        cache.Update(snapshot, "", true);
        Assert.Equal(PlaybackUiChanges.None, cache.Update(snapshot with { Error = "Native diagnostic" }, "", true).Changes);
    }

    [Fact]
    public void ClearingANoticeRestoresTheEngineStatus()
    {
        var cache = new PlaybackUiCache();
        var snapshot = PlaybackFixtures.Playing();
        cache.Update(snapshot, "", true);
        Assert.Equal("Screenshot saved", cache.Update(snapshot, "Screenshot saved", true).State.Status);
        var cleared = cache.Update(snapshot, "", true);
        Assert.Equal("Playing", cleared.State.Status);
        Assert.Equal(PlaybackUiChanges.Status, cleared.Changes);
    }

    [Fact]
    public void VolumeGesturesDeferEngineValuesUntilRelease()
    {
        var cache = new PlaybackUiCache();
        var snapshot = PlaybackFixtures.Playing();
        cache.Update(snapshot, "", true);
        var changed = snapshot with { Volume = 75 };
        Assert.Equal(PlaybackUiChanges.None, cache.Update(changed, "", false).Changes);
        Assert.Equal(PlaybackUiChanges.Volume, cache.Update(changed, "", true).Changes);
        Assert.Equal(PlaybackUiChanges.None, cache.Update(changed, "", true).Changes);
    }

    [Fact]
    public void InvalidatingVolumeReconcilesEvenAnUnchangedEngineValue()
    {
        var cache = new PlaybackUiCache();
        var snapshot = PlaybackFixtures.Playing();
        cache.Update(snapshot, "", true);
        cache.InvalidateVolume();
        Assert.Equal(PlaybackUiChanges.Volume, cache.Update(snapshot, "", true).Changes);
    }

    [Fact]
    public void InvalidatingTheCacheForcesACompleteResynchronization()
    {
        var cache = new PlaybackUiCache();
        var snapshot = PlaybackFixtures.Playing();
        cache.Update(snapshot, "", true);
        cache.Invalidate();
        Assert.Equal(PlaybackUiChanges.All, cache.Update(snapshot, "", true).Changes);
    }

    [Fact]
    public void RecoveryFromEmptyMediaDoesNotRewriteUnchangedVolume()
    {
        var cache = new PlaybackUiCache();
        var snapshot = PlaybackFixtures.Playing();
        cache.Update(snapshot, "", true);
        cache.Update(snapshot with { Path = "", Title = "", Idle = true, Duration = 0 }, "", true);
        Assert.Equal(PlaybackUiChanges.All & ~PlaybackUiChanges.Volume, cache.Update(snapshot, "", true).Changes);
    }
}

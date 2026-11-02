using Mani.Playback;
using Mani_App;

namespace Mani.UnitTests;

public sealed class PlayerSurfaceTests
{
    [Fact]
    public void PendingUiRequestHidesThePreviousMediaBeforeTheNextEnginePoll()
    {
        var surface = PlayerSurfaceState.From(PlaybackFixtures.Playing(), opening: true);
        Assert.Equal(PlayerSurface.Loading, surface.Surface);
        Assert.Equal("", surface.Title);
        Assert.Equal(0, surface.Position);
        Assert.Equal(0, surface.Duration);
    }
    [Theory]
    [InlineData(PlaybackState.Loading, true, PlayerSurface.Loading)]
    [InlineData(PlaybackState.Stopped, false, PlayerSurface.Stopped)]
    [InlineData(PlaybackState.Failed, false, PlayerSurface.Failed)]
    internal void TerminalOrLoadingStatesNeverExposeStaleMedia(PlaybackState state, bool loading, PlayerSurface expected)
    {
        var surface = PlayerSurfaceState.From(PlaybackFixtures.Playing() with { State = state, Loading = loading });
        Assert.Equal(expected, surface.Surface);
        Assert.False(surface.HasMedia);
        Assert.Equal("", surface.Title);
        Assert.Equal(0, surface.Position);
    }
    [Fact]
    public void RecoveryPreservesUnicodeTitleAndEngineProgress()
    {
        var snapshot = PlaybackFixtures.Playing() with { Title = "中文 空格 # [B] — 日本語", Position = 12.5 };
        var surface = PlayerSurfaceState.From(snapshot);
        Assert.True(surface.HasMedia);
        Assert.Equal(snapshot.Title, surface.Title);
        Assert.Equal(12.5, surface.Position);
        Assert.Equal(snapshot.Duration, surface.Duration);
    }
    [Fact]
    public void ColdStartAndIdleReturnToWelcome()
    {
        Assert.Equal(PlayerSurface.Welcome, PlayerSurfaceState.From(null).Surface);
        Assert.Equal(PlayerSurface.Welcome, PlayerSurfaceState.From(PlaybackFixtures.Playing() with { Idle = true }).Surface);
    }
}

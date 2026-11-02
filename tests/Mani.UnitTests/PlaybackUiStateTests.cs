using Mani_App;

namespace Mani.UnitTests;

// Migrated from UiRefreshProbes: exercise the production projection, not WinUI controls.
public sealed class PlaybackUiStateTests
{
    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    public void PlayAvailabilityRequiresAnActiveLoadedFile(bool idle, bool loading, bool expected)
    {
        var snapshot = PlaybackFixtures.Playing() with { Idle = idle, Loading = loading };
        Assert.Equal(expected, PlaybackUiState.From(snapshot, "").CanPlay);
    }

    [Theory]
    [InlineData(false, false, "Pause")]
    [InlineData(true, false, "Play")]
    [InlineData(false, true, "Play")]
    [InlineData(true, true, "Play")]
    public void PausedOrEndedPlaybackOffersPlay(bool paused, bool ended, string expected)
    {
        var snapshot = PlaybackFixtures.Playing() with { Paused = paused, Ended = ended };
        Assert.Equal(expected, PlaybackUiState.From(snapshot, "").PlayLabel);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(120, true)]
    public void SeekingRequiresAPositiveDuration(double duration, bool expected)
    {
        var snapshot = PlaybackFixtures.Playing() with { Duration = duration };
        Assert.Equal(expected, PlaybackUiState.From(snapshot, "").CanSeek);
    }

    [Fact]
    public void EmptyMediaUsesTheEnglishPlaceholder()
    {
        var snapshot = PlaybackFixtures.Playing() with { Path = "", Title = "", Idle = true, Duration = 0 };
        var state = PlaybackUiState.From(snapshot, "");
        Assert.True(state.Empty);
        Assert.Equal("No file open", state.Title);
        Assert.Equal("Open a local video to start playback", state.Status);
        Assert.False(state.CanPlay);
        Assert.False(state.CanSeek);
    }

    [Fact]
    public void UserProvidedTitlesKeepTheirLanguage()
    {
        var snapshot = PlaybackFixtures.Playing() with { Title = "Movie \u4e2d\u6587 #1" };
        Assert.Equal(snapshot.Title, PlaybackUiState.From(snapshot, "").Title);
    }

    [Fact]
    public void HostNoticeTakesPrecedenceOverAnEngineFailure()
    {
        var snapshot = PlaybackFixtures.Playing() with { LoadFailure = "Decoder failed" };
        Assert.Equal("Operation failed", PlaybackUiState.From(snapshot, "Operation failed").Status);
    }

    [Fact]
    public void StructuredLoadFailureTakesPrecedenceOverLoading()
    {
        var snapshot = PlaybackFixtures.Playing() with { LoadFailure = "Decoder failed", Loading = true };
        Assert.Equal("Decoder failed", PlaybackUiState.From(snapshot, "").Status);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("An arbitrary native error log")]
    [InlineData("vo/gpu-next/d3d11: Failed to query swap chain's output information")]
    public void DiagnosticLogsDoNotBecomePlaybackFailures(string diagnostic)
    {
        var snapshot = PlaybackFixtures.Playing() with { Error = diagnostic };
        Assert.Equal("Playing", PlaybackUiState.From(snapshot, "").Status);
        Assert.Equal("Open a local video to start playback",
            PlaybackUiState.From(snapshot with { Idle = true, Path = "" }, "").Status);
    }

    [Fact]
    public void WhitespaceLoadFailureDoesNotReplacePlaybackStatus()
    {
        Assert.Equal("Playing", PlaybackUiState.From(
            PlaybackFixtures.Playing() with { LoadFailure = " \t" }, "").Status);
    }

    [Fact]
    public void LoadingTakesPrecedenceOverBuffering()
    {
        var snapshot = PlaybackFixtures.Playing() with { Loading = true, PausedForCache = true };
        Assert.Equal("Loading...", PlaybackUiState.From(snapshot, "").Status);
    }

    [Fact]
    public void BufferingTakesPrecedenceOverPause()
    {
        var snapshot = PlaybackFixtures.Playing() with { PausedForCache = true, Paused = true };
        Assert.Equal("Buffering...", PlaybackUiState.From(snapshot, "").Status);
    }

    [Fact]
    public void EndOfFileShowsTheReplayState()
    {
        var state = PlaybackUiState.From(PlaybackFixtures.Playing() with { Ended = true }, "");
        Assert.Equal("Playback ended", state.Status);
        Assert.Equal("Play", state.PlayLabel);
    }

    [Fact]
    public void PauseShowsThePausedState()
    {
        Assert.Equal("Paused", PlaybackUiState.From(PlaybackFixtures.Playing() with { Paused = true }, "").Status);
    }
}

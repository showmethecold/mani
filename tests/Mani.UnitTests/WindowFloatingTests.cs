using Mani_App;
using Mani.Playback;
using System.Text.Json;

namespace Mani.UnitTests;

public sealed class WindowFloatingTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ManualChoiceSurvivesDuplicateSnapshotsAndPauseWithoutAutomaticMode(bool initial)
    {
        var state = new WindowFloatingState();
        state.Observe(FloatingPlayback.Playing, 1, 5);
        state.SetManual(initial);
        for (int i = 0; i < 10; i++) state.Observe(FloatingPlayback.Playing, 1, 5);
        state.Observe(FloatingPlayback.Paused, 1, 5);
        Assert.Equal(initial, state.Applied);
        Assert.Equal(initial, state.Requested);
    }
    [Fact]
    public void AutomaticPauseTransitionsDoNotOverrideManualChangesBetweenEvents()
    {
        var state = new WindowFloatingState(); state.SetAutomatic(true);
        state.Observe(FloatingPlayback.Playing, 1, 1); Assert.True(state.Applied);
        state.SetManual(false);
        state.Observe(FloatingPlayback.Playing, 1, 1); Assert.False(state.Applied);
        state.Observe(FloatingPlayback.Paused, 1, 1); Assert.False(state.Applied);
        state.SetManual(true);
        state.Observe(FloatingPlayback.Paused, 1, 1); Assert.True(state.Applied);
        state.Observe(FloatingPlayback.Playing, 1, 1); Assert.True(state.Applied);
        state.Observe(FloatingPlayback.Paused, 1, 1); Assert.False(state.Applied);
    }
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void PreferenceChangeAppliesImmediatelyOnlyWhilePlaying(int value)
    {
        var playback = (FloatingPlayback)value;
        var state = new WindowFloatingState(); state.Observe(playback, 1, 1);
        state.SetAutomatic(true);
        Assert.Equal(playback == FloatingPlayback.Playing, state.Applied);
        state.SetAutomatic(false);
        Assert.False(state.Applied);
    }
    [Fact]
    public void DisablingAutomaticWhilePausedPreservesAManualPin()
    {
        var state = new WindowFloatingState(); state.SetAutomatic(true);
        state.Observe(FloatingPlayback.Paused, 1, 1); state.SetManual(true);
        state.SetAutomatic(false); Assert.True(state.Applied);
        state.Observe(FloatingPlayback.Playing, 1, 1); Assert.True(state.Applied);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void FullscreenSuspendsTheActualLevelAndRestoresOnlyWhilePlaying(bool paused)
    {
        var state = new WindowFloatingState();
        state.Observe(paused ? FloatingPlayback.Paused : FloatingPlayback.Playing, 1, 1);
        state.SetManual(true); state.EnterFullscreen();
        Assert.True(state.Requested); Assert.False(state.Applied); Assert.False(state.CanSetManual);
        state.SetManual(false); Assert.True(state.Requested);
        state.ExitFullscreen();
        Assert.True(state.Requested); Assert.Equal(!paused, state.Applied);
    }
    [Fact]
    public void PauseInsideFullscreenDoesNotRewriteTheRequestedState()
    {
        var state = new WindowFloatingState(); state.SetAutomatic(true);
        state.Observe(FloatingPlayback.Playing, 1, 1); state.EnterFullscreen();
        state.Observe(FloatingPlayback.Paused, 1, 1);
        Assert.True(state.Requested); state.ExitFullscreen(); Assert.False(state.Applied);
        state.Observe(FloatingPlayback.Paused, 1, 1); Assert.False(state.Applied);
        state.Observe(FloatingPlayback.Playing, 1, 1); Assert.True(state.Applied);
    }
    [Fact]
    public void ResumingInsideFullscreenDoesNotInventAPinThatWasOffBeforeEntry()
    {
        var state = new WindowFloatingState(); state.SetAutomatic(true);
        state.Observe(FloatingPlayback.Paused, 1, 1); state.EnterFullscreen();
        state.Observe(FloatingPlayback.Playing, 1, 1); state.ExitFullscreen();
        Assert.False(state.Requested); Assert.False(state.Applied);
        state.Observe(FloatingPlayback.Paused, 1, 1);
        state.Observe(FloatingPlayback.Playing, 1, 1); Assert.True(state.Applied);
    }
    [Fact]
    public void PreferenceChangedInFullscreenTakesEffectOnTheNextWindowedPauseEvent()
    {
        var state = new WindowFloatingState(); state.Observe(FloatingPlayback.Playing, 1, 1);
        state.EnterFullscreen(); state.SetAutomatic(true); state.ExitFullscreen();
        Assert.False(state.Applied);
        state.Observe(FloatingPlayback.Playing, 1, 1); Assert.False(state.Applied);
        state.Observe(FloatingPlayback.Paused, 1, 1);
        state.Observe(FloatingPlayback.Playing, 1, 1); Assert.True(state.Applied);
    }
    [Theory]
    [InlineData(2, 1)] [InlineData(1, 2)]
    public void ANewMediaEntryGetsItsOwnInitialPauseObservation(long request, long entry)
    {
        var state = new WindowFloatingState(); state.SetAutomatic(true);
        state.Observe(FloatingPlayback.Playing, 1, 1); state.SetManual(false);
        state.Observe(FloatingPlayback.Loading, request, entry); Assert.False(state.Applied);
        state.Observe(FloatingPlayback.Playing, request, entry); Assert.True(state.Applied);
    }
    [Fact]
    public void LoadingRetainsTheOldLevelButStopOrFailureClearsAnEmptyHost()
    {
        var state = new WindowFloatingState(); state.Observe(FloatingPlayback.Playing, 1, 1); state.SetManual(true);
        state.Observe(FloatingPlayback.Loading, 2, 2); Assert.True(state.Applied); Assert.False(state.CanSetManual);
        state.Observe(FloatingPlayback.Empty); Assert.False(state.Applied); Assert.False(state.Requested);
        state.SetManual(true); Assert.False(state.Applied);
        state.Observe(FloatingPlayback.Playing, 3, 3); Assert.False(state.Applied);
    }
    [Fact]
    public void AutomaticModeRecoversAfterEmptyEvenWhenRequestNumbersAreReused()
    {
        var state = new WindowFloatingState(); state.SetAutomatic(true);
        state.Observe(FloatingPlayback.Playing, 1, 1); state.Observe(FloatingPlayback.Empty);
        state.Observe(FloatingPlayback.Playing, 1, 1); Assert.True(state.Applied);
    }
    [Fact]
    public void RepeatedFullscreenCompletionDoesNotUndoALaterManualChoice()
    {
        var state = new WindowFloatingState(); state.Observe(FloatingPlayback.Paused, 1, 1);
        state.EnterFullscreen(); state.ExitFullscreen(); state.SetManual(true); state.ExitFullscreen();
        Assert.True(state.Applied);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task AutomaticPreferenceRoundTripsWithoutLosingUnrelatedFields(bool enabled)
    {
        string directory = Path.Combine(Path.GetTempPath(), "ManiFloatingTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var token = TestContext.Current.CancellationToken;
            string path = Path.Combine(directory, "settings.json");
            await File.WriteAllTextAsync(path, "{\"schemaVersion\":1,\"volumePercent\":37,\"custom\":42}", token);
            var store = new PlayerSettingsStore(path); var settings = await store.LoadAsync(token);
            Assert.False(settings.AlwaysFloatOnTop);
            await store.SaveAsync(settings with { AlwaysFloatOnTop = enabled }, token);
            Assert.Equal(enabled, (await store.LoadAsync(token)).AlwaysFloatOnTop);
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(path, token));
            Assert.Equal(42, json.RootElement.GetProperty("custom").GetInt32());
            Assert.Equal(37, json.RootElement.GetProperty("volumePercent").GetInt32());
            Assert.False(json.RootElement.TryGetProperty("isOntop", out _));
        }
        finally { Directory.Delete(directory, true); }
    }
}

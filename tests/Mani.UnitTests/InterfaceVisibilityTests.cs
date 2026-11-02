using System.Text.Json;
using Mani.Playback;
using Mani_App;

namespace Mani.UnitTests;

public sealed class InterfaceVisibilityTests
{
    [Fact]
    public void AutoHideOffKeepsControlsAndReenablingStartsFullDelay()
    {
        var osc = new FloatingOscState();
        osc.Configure(false, 500, 0, false); osc.SetActive(true, 0, false);
        osc.Leave(100, false); osc.Tick(5000, false);
        Assert.True(osc.Visible); Assert.False(osc.NeedsTick);
        osc.Configure(true, 500, 5000, false); osc.Tick(5499, false); Assert.True(osc.Visible);
        osc.Tick(5500, false); Assert.False(osc.Visible);
    }
    [Fact]
    public void ZeroDelayStillRespectsCaptureAndDisablingAutoHideDuringFadeRecallsControls()
    {
        var osc = new FloatingOscState(); osc.Configure(true, 0, 0, true); osc.SetActive(true, 0, false);
        osc.Hold(OscHold.Capture, true, 0, false); osc.Tick(500, false); Assert.True(osc.Visible);
        osc.Hold(OscHold.Capture, false, 500, true); osc.Tick(500, true); Assert.False(osc.HitTestVisible);
        osc.Tick(625, true); Assert.Equal(.5, osc.Opacity, 8);
        osc.Configure(false, 0, 625, true); osc.Tick(875, true); Assert.True(osc.Visible); Assert.Equal(1, osc.Opacity);
    }
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2500)] [InlineData(int.MaxValue)]
    public void OsdConfiguredDurationAndReplacementDoNotRenewOnTimeRefresh(long hold)
    {
        var state = new OsdState(); state.Show(new(OsdKind.Pause), 100, hold);
        state.Tick(100 + hold, true); Assert.True(state.Visible);
        state.Tick(350 + hold, true); Assert.Equal(.5, state.Opacity, 8);
        state.Show(new(OsdKind.Speed, Value: 2), 350 + hold, 0);
        Assert.Equal(1, state.Opacity); Assert.Equal(OsdKind.Speed, state.Message!.Kind);
        state.Tick(350 + hold, false); Assert.False(state.Visible);
    }
    [Theory]
    [InlineData(5, 11)] [InlineData(20, 11)] [InlineData(30, 15)] [InlineData(50, 25)] [InlineData(512, 25)]
    public void SecondaryTextUsesIinaScalingAndBounds(double size, double secondary)
        => Assert.Equal(secondary, OsdPreferences.SecondarySize(size));

    public static IEnumerable<object[]> OsdKinds() => Enum.GetValues<OsdKind>().Select(kind => new object[] { kind });
    [Theory, MemberData(nameof(OsdKinds))]
    internal void MasterOffSuppressesEveryExistingMessageAndTypeSwitchesLeaveOthers(OsdKind kind)
    {
        Assert.True(OsdPreferences.Allows(kind, new()));
        Assert.False(OsdPreferences.Allows(kind, new(EnableOsd: false)));
        var all = new PlayerSettings(DisableOsdFileStart: true, DisableOsdPauseResume: true, DisableOsdSeek: true, DisableOsdSpeed: true);
        Assert.Equal(kind is OsdKind.ShowTime or OsdKind.Volume or OsdKind.Mute or OsdKind.Unmute or OsdKind.Stop or
            OsdKind.VideoTrack or OsdKind.AudioTrack or OsdKind.SubtitleTrack or
            OsdKind.AudioDelay or OsdKind.SubDelay or OsdKind.SecondarySubDelay or
            OsdKind.SubHidden or OsdKind.SubVisible or OsdKind.SecondarySubHidden or OsdKind.SecondarySubVisible or OsdKind.SubPosition or OsdKind.SubScale or
            OsdKind.AbLoopA or OsdKind.AbLoopB or OsdKind.AbLoopClear or OsdKind.FileLoop or OsdKind.PlaylistLoop or OsdKind.LoopOff or OsdKind.Screenshot or OsdKind.Navigation,
            OsdPreferences.Allows(kind, all));
        Assert.Equal(kind != OsdKind.File, OsdPreferences.Allows(kind, new(DisableOsdFileStart: true)));
        Assert.Equal(kind is not (OsdKind.Pause or OsdKind.Resume), OsdPreferences.Allows(kind, new(DisableOsdPauseResume: true)));
        Assert.Equal(kind != OsdKind.Seek, OsdPreferences.Allows(kind, new(DisableOsdSeek: true)));
        Assert.Equal(kind != OsdKind.Speed, OsdPreferences.Allows(kind, new(DisableOsdSpeed: true)));
    }
    [Fact]
    public async Task OldPreferencesAcquireIinaDefaultsAndRoundTripAllUiKeysOnce()
    {
        using var directory = new TemporaryDirectory(); var token = TestContext.Current.CancellationToken;
        string path = Path.Combine(directory.Path, "settings.json");
        await File.WriteAllTextAsync(path, "{\"volumePercent\":37,\"custom\":42}", token);
        var store = new PlayerSettingsStore(path); var old = await store.LoadAsync(token);
        Assert.Equal(new PlayerSettings(VolumePercent: 37), old);
        var changed = old with { EnableControlBarAutoHide = false, ControlBarAutoHideTimeout = .125, EnableOsd = false,
            DisableOsdFileStart = true, DisableOsdPauseResume = true, DisableOsdSeek = true, DisableOsdSpeed = true,
            OsdAutoHideTimeout = 3.125, OsdTextSize = 38.125 };
        await store.SaveAsync(changed, token); Assert.Equal(changed, await store.LoadAsync(token));
        await store.SaveAsync(changed with { EnableOsd = true }, token);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(path, token));
        Assert.Equal(42, json.RootElement.GetProperty("custom").GetInt32());
        var keys = json.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(keys.Length, keys.Distinct().Count());
    }
    [Theory]
    [InlineData(-1, 0, 5)] [InlineData(0, 0, 5)] [InlineData(20, 20, 20)] [InlineData(1e100, InterfacePreferenceLimits.MaximumSeconds, 512)]
    public async Task ImportedNumbersClampToLocalBounds(double input, double delay, double size)
    {
        using var directory = new TemporaryDirectory(); var token = TestContext.Current.CancellationToken;
        string path = Path.Combine(directory.Path, "settings.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { controlBarAutoHideTimeout = input, osdAutoHideTimeout = input, osdTextSize = input }), token);
        var value = await new PlayerSettingsStore(path).LoadAsync(token);
        Assert.Equal(delay, value.ControlBarAutoHideTimeout); Assert.Equal(delay, value.OsdAutoHideTimeout); Assert.Equal(size, value.OsdTextSize);
    }
    [Theory]
    [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)] [InlineData(double.NegativeInfinity)] [InlineData(-1)] [InlineData(1e100)]
    public async Task InvalidSavesCannotDamagePreviousSettings(double invalid)
    {
        using var directory = new TemporaryDirectory(); var token = TestContext.Current.CancellationToken;
        string path = Path.Combine(directory.Path, "settings.json"); var store = new PlayerSettingsStore(path);
        await store.SaveAsync(new(), token); string before = await File.ReadAllTextAsync(path, token);
        foreach (var bad in new[] { new PlayerSettings(ControlBarAutoHideTimeout: invalid), new(OsdAutoHideTimeout: invalid), new(OsdTextSize: invalid) })
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.SaveAsync(bad, token));
        Assert.Equal(before, await File.ReadAllTextAsync(path, token));
    }
}

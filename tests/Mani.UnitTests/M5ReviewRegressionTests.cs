using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Mani.Playback;
using Mani_App;

namespace Mani.UnitTests;

public sealed class M5ReviewRegressionTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // Commands complete only after their fake engine effect, like the real command-reply path.
    private sealed class Engine : IAsyncDisposable
    {
        public FakeMpvClient Client { get; } = new();
        public ConcurrentQueue<string[]> Commands { get; } = new();
        public string? FailedProperty;
        public bool FailEqualizer;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _pump;
        public Engine()
        {
            Client.Properties["audio-device-list"] = Devices("wasapi/test");
            Client.Properties["pause"] = "yes";
            _pump = Task.Run(async () =>
            {
                await foreach (var command in Client.Commands.Reader.ReadAllAsync(_stop.Token))
                {
                    var args = command.Arguments; Commands.Enqueue(args);
                    bool fail = (args[0] == "set" && args[1] == FailedProperty) || (args[0] == "af" && FailEqualizer);
                    if (!fail && args[0] == "set") Client.Properties[args[1]] = args[2];
                    Client.Reply(command, fail ? -12 : 0);
                }
            }, _stop.Token);
        }
        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            try { await _pump; } catch (OperationCanceledException) { }
            _stop.Dispose();
        }
    }
    private static object[] Devices(params string[] names) => names.Prepend("auto")
        .Select(name => (object)new Dictionary<string, object?> { ["name"] = name, ["description"] = name }).ToArray();

    [Fact]
    public async Task DefaultFontResolutionPreservesPreferencesExplicitFontsAndTemporaryChannels()
    {
        await using var engine = new Engine(); await using var session = await engine.Client.Create();
        var saved = new MediaPreferences();
        var applied = await session.ApplyMediaPreferencesAsync(saved, Token);
        Assert.Empty(applied.Unsupported);
        Assert.Equal(SubtitleFontFallback.Resolve("sans-serif"), applied.Actual["Font"]);
        await session.SetAsync("sub-ass-override", "no");
        await session.SetAsync("secondary-sub-ass-override", "no");
        await session.SetAsync("secondary-sub-pos", "35");
        await session.SetAsync("sub-delay", "0.5");
        int writes = engine.Commands.Count(c => c is ["set", "sub-font", _]);
        await session.ApplyMediaPreferencesAsync(saved with { FontSize = "48" }, Token);
        Assert.Equal(writes, engine.Commands.Count(c => c is ["set", "sub-font", _]));
        foreach (string font in new[] { "Arial", "微软雅黑", "Missing user font", "sans-serif" })
        {
            var requested = saved with { Font = font };
            applied = await session.ApplyMediaPreferencesAsync(requested, Token);
            Assert.Equal(SubtitleFontFallback.Resolve(font), applied.Actual["Font"]);
            Assert.Equal(font, JsonSerializer.Deserialize<MediaPreferences>(JsonSerializer.Serialize(requested))!.Font);
            Assert.Equal("no", engine.Client.Properties["sub-ass-override"]);
            Assert.Equal("no", engine.Client.Properties["secondary-sub-ass-override"]);
            Assert.Equal("35", engine.Client.Properties["secondary-sub-pos"]);
            Assert.Equal("0.5", engine.Client.Properties["sub-delay"]);
        }
        Assert.Equal("sans-serif", saved.Font);
        Assert.Single(engine.Client.Threads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AutomaticSelectionWaitsForResetReplyAndHonorsCancellation(bool cancel)
    {
        var client = new FakeMpvClient(); await using var session = await client.Create();
        var loading = session.LoadAsync("https://localhost/media", true, Token);
        var load = await client.Next(); client.Playlist(41);
        client.Properties["playlist-playing-pos"] = 0L; client.Properties["playlist/0/id"] = 41L;
        client.Properties["sid"] = "no"; client.Properties["secondary-sid"] = "no";
        client.Reply(load); client.Loaded(41, "https://localhost/media"); var media = await loading;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var selecting = session.SelectAutomaticSubtitleAsync(media.RequestId, media.PlaylistEntryId, cancellation.Token);
        var reset = await client.Next(); Assert.Equal(["set", "sid", "no"], reset.Arguments);
        Assert.False(client.Commands.Reader.TryRead(out _));
        if (cancel) cancellation.Cancel();
        client.Reply(reset);
        if (cancel)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => selecting);
            Assert.False(client.Commands.Reader.TryRead(out _));
        }
        else
        {
            var auto = await client.Next(); Assert.Equal(["set", "sid", "auto"], auto.Arguments);
            client.Properties["sid"] = "2"; client.Reply(auto);
            Assert.Equal("2", (await selecting).SubtitleTrack);
        }
        Assert.Single(client.Threads);
    }

    [Fact]
    public async Task TemporaryChannelsSurviveUnrelatedPreferencesButExplicitGlobalsSynchronize()
    {
        await using var first = new Engine(); await using var second = new Engine();
        await using var a = await first.Client.Create(); await using var b = await second.Client.Create();
        var defaults = new MediaPreferences();
        await a.ApplyMediaPreferencesAsync(defaults, Token); await b.ApplyMediaPreferencesAsync(defaults, Token);
        await a.SetAsync("sub-pos", "80"); await a.SetAsync("sub-scale", "1.2");
        await a.SetAsync("secondary-sub-pos", "30"); await a.SetAsync("sub-ass-override", "strip");
        var changed = defaults with { FontSize = "48" };
        await a.ApplyMediaPreferencesAsync(changed, Token); await b.ApplyMediaPreferencesAsync(changed, Token);
        Assert.Equal("80", first.Client.Properties["sub-pos"]);
        Assert.Equal("strip", first.Client.Properties["sub-ass-override"]);
        Assert.Equal("100", second.Client.Properties["sub-pos"]);
        changed = changed with { PrimaryPosition = "90", PrimaryOverride = "force" };
        await a.ApplyMediaPreferencesAsync(changed, Token); await b.ApplyMediaPreferencesAsync(changed, Token);
        Assert.Equal("90", first.Client.Properties["sub-pos"]); Assert.Equal("90", second.Client.Properties["sub-pos"]);
        Assert.Equal("force", first.Client.Properties["sub-ass-override"]);
        Assert.Equal("1.2", first.Client.Properties["sub-scale"]); Assert.Equal("30", first.Client.Properties["secondary-sub-pos"]);
        Assert.DoesNotContain(first.Commands, c => c[0] == "set" && c[1] == "secondary-sub-scale");
        var json = JsonSerializer.SerializeToElement(changed);
        foreach (string key in new[] { "PrimaryScale", "SecondaryScale", "SecondaryPosition" }) Assert.False(json.TryGetProperty(key, out _));
        Assert.Single(first.Client.Threads); Assert.Single(second.Client.Threads);
    }

    [Fact]
    public async Task RecoveredDeviceCanBeExplicitlyReselectedWithoutPreferenceChangeOrUnpause()
    {
        await using var engine = new Engine(); await using var session = await engine.Client.Create();
        var prefs = new MediaPreferences { AudioDevice = "wasapi/test" };
        await session.ApplyMediaPreferencesAsync(prefs, Token);
        engine.Client.Properties["audio-device-list"] = Devices();
        await session.ApplyMediaPreferencesAsync(prefs with { FontSize = "48" }, Token);
        var state = await session.GetAudioOutputAsync(); Assert.Equal("auto", state.Selected); Assert.True(state.FellBackToDefault);
        engine.Client.Properties["audio-device-list"] = Devices("wasapi/test");
        await session.ApplyMediaPreferencesAsync(prefs, Token);
        state = await session.GetAudioOutputAsync(); Assert.Equal("auto", state.Selected); Assert.True(state.FellBackToDefault);
        await session.SelectAudioDeviceAsync(prefs.AudioDevice, Token);
        state = await session.GetAudioOutputAsync(); Assert.Equal("wasapi/test", state.Selected); Assert.False(state.FellBackToDefault); Assert.True(state.Paused);
        int count = engine.Commands.Count;
        await session.SelectAudioDeviceAsync(prefs.AudioDevice, Token);
        Assert.Equal(count, engine.Commands.Count);
        Assert.DoesNotContain(engine.Commands, c => c[0] == "set" && c[1] == "pause");
    }

    [Fact]
    public async Task PersistentSingleFieldFailureDoesNotRebuildSuccessfulEqualizer()
    {
        await using var engine = new Engine { FailedProperty = "sub-font" }; await using var session = await engine.Client.Create();
        var prefs = new MediaPreferences { EqualizerEnabled = "yes", Eq32 = "3" };
        for (int i = 0; i < 3; i++) Assert.Contains("Font", (await session.ApplyMediaPreferencesAsync(prefs, Token)).Unsupported);
        Assert.Single(engine.Commands, c => c[0] == "af");
        Assert.Equal(3, engine.Commands.Count(c => c[0] == "set" && c[1] == "sub-font"));
        Assert.Single(engine.Commands, c => c[0] == "set" && c[1] == "audio-device");
        engine.FailedProperty = null;
        Assert.Empty((await session.ApplyMediaPreferencesAsync(prefs, Token)).Unsupported);
        int count = engine.Commands.Count; await session.ApplyMediaPreferencesAsync(prefs, Token);
        Assert.Equal(count, engine.Commands.Count);
    }

    [Fact]
    public async Task EqualizerFailureRetriesOnlyEqualizerUntilRecovered()
    {
        await using var engine = new Engine { FailEqualizer = true }; await using var session = await engine.Client.Create();
        var prefs = new MediaPreferences { EqualizerEnabled = "yes" };
        Assert.Contains("EqualizerEnabled", (await session.ApplyMediaPreferencesAsync(prefs, Token)).Unsupported);
        int before = engine.Commands.Count;
        await session.ApplyMediaPreferencesAsync(prefs, Token);
        Assert.Single(engine.Commands.Skip(before)); Assert.Equal("af", engine.Commands.Last()[0]);
        engine.FailEqualizer = false; Assert.Empty((await session.ApplyMediaPreferencesAsync(prefs, Token)).Unsupported);
        before = engine.Commands.Count; await session.ApplyMediaPreferencesAsync(prefs, Token); Assert.Equal(before, engine.Commands.Count);
    }

    [Fact]
    public async Task ThirtyTwoLongChineseProfilesWithDecimalGainsRoundTripAndReportCorrectLimits()
    {
        string json = "{}";
        double[] gains = Enumerable.Range(0, 10).Select(i => -11.123456789012345 + i).ToArray();
        for (int i = 0; i < 32; i++) json = EqualizerProfiles.Save(json, new string('中', 78) + i.ToString("D2", CultureInfo.InvariantCulture), gains);
        Assert.True(json.Length > 4096);
        using var directory = new TemporaryDirectory(); var store = new PlayerSettingsStore(Path.Combine(directory.Path, "settings.json"));
        await store.SaveAsync(new() { Media = new() { EqualizerProfiles = json } }, Token);
        Assert.Equal(json, (await store.LoadAsync(Token)).Media.EqualizerProfiles);
        Assert.Equal(32, EqualizerProfiles.Read(json).Count);
        var applied = EqualizerProfiles.Apply(new(), gains);
        Assert.Equal(-11.123457, EqualizerProfiles.Gains(applied)[0]);
        Assert.True(EqualizerProfiles.Matches(applied, gains));
        Assert.False(EqualizerProfiles.Matches(applied with { Eq32 = "0" }, gains));
        Assert.Equal(EqualizerProfileError.Count, Assert.Throws<EqualizerProfileException>(() => EqualizerProfiles.Save(json, "extra", gains)).Reason);
        Assert.Equal(EqualizerProfileError.Size, Assert.Throws<EqualizerProfileException>(() => EqualizerProfiles.Validate(new string(' ', 32769))).Reason);
        Assert.Equal(EqualizerProfileError.Name, Assert.Throws<EqualizerProfileException>(() => EqualizerProfiles.Save("{}", new string('中', 81), gains)).Reason);
        Assert.Equal(EqualizerProfileError.Data, Assert.Throws<EqualizerProfileException>(() => EqualizerProfiles.Validate("{\"A\":[0]}")).Reason);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("zh-CN")]
    [InlineData("tr-TR")]
    public void ProfileIdentityIsOrdinalAcrossCulturesAndSharedEdits(string culture)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new(culture);
            string a = EqualizerProfiles.Save("{}", "Å", new double[10]);
            string b = EqualizerProfiles.Save("{}", "A\u030A", Enumerable.Repeat(1d, 10).ToArray());
            var merged = EqualizerProfiles.Read(EqualizerProfiles.Merge(a, "{}", b));
            Assert.Equal(2, merged.Count); Assert.Equal(0d, merged["Å"][0]); Assert.Equal(1d, merged["A\u030A"][0]);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NestedUnknownMediaSurvivesSharedSaveAndCorruptRecovery(bool recovery)
    {
        using var directory = new TemporaryDirectory(); string path = Path.Combine(directory.Path, "settings.json");
        const string original = "{\"schemaVersion\":1,\"futureTop\":[1,2],\"media\":{\"Font\":\"Original\",\"futureFlag\":true,\"futureSettings\":{\"nested\":[1,null,\"中文\"]},\"PrimaryScale\":\"1.7\"}}";
        await File.WriteAllTextAsync(path, original, Token);
        if (recovery) { File.Copy(path, path + ".backup"); await File.WriteAllTextAsync(path, "broken", Token); }
        var store = new PlayerSettingsStore(path); var baseline = await store.LoadAsync(Token);
        var first = baseline with { Media = baseline.Media with { Font = "Consolas" } };
        await store.SaveAsync(first, Token);
        var second = SharedPlayerSettings.Merge(await store.LoadAsync(Token), baseline, baseline with { Media = baseline.Media with { FontSize = "50" } });
        await store.SaveAsync(second, Token);
        using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(path, Token));
        var media = saved.RootElement.GetProperty("media");
        Assert.Equal("Consolas", media.GetProperty("Font").GetString()); Assert.Equal("50", media.GetProperty("FontSize").GetString());
        Assert.True(media.GetProperty("futureFlag").GetBoolean()); Assert.Equal("中文", media.GetProperty("futureSettings").GetProperty("nested")[2].GetString());
        Assert.Equal("1.7", media.GetProperty("PrimaryScale").GetString()); Assert.Equal(2, saved.RootElement.GetProperty("futureTop").GetArrayLength());
        Assert.Equal(recovery ? 1 : 0, Directory.GetFiles(directory.Path, "*.corrupt-*").Length);
    }

    [Fact]
    public async Task FutureSchemaRefusesWritesIncludingNestedMediaWithoutChangingAnyBytes()
    {
        using var directory = new TemporaryDirectory(); string path = Path.Combine(directory.Path, "settings.json");
        const string original = "{\"schemaVersion\":2,\"media\":{\"future\":{\"value\":1}}}";
        await File.WriteAllTextAsync(path, original, Token); var store = new PlayerSettingsStore(path);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync(Token));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(new(), Token));
        Assert.Equal(original, await File.ReadAllTextAsync(path, Token)); Assert.Single(Directory.GetFiles(directory.Path));
    }
}

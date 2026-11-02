using System.Globalization;
using System.Text.Json;
using Mani.Playback;
using Mani_App;

namespace Mani.UnitTests;

public sealed class M5MediaTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    [Fact]
    public void EqualizerPresetsAndIndependentProfileEditsPreserveGains()
    {
        Assert.Equal(22, EqualizerProfiles.Presets.Count);
        var rock = EqualizerProfiles.Presets.Single(p => p.Key == "rock");
        var settings = EqualizerProfiles.Apply(new(), rock.Gains);
        Assert.Equal("yes", settings.EqualizerEnabled);
        Assert.Equal(rock.Gains, EqualizerProfiles.Gains(settings));
        string first = EqualizerProfiles.Save("{}", "A", rock.Gains.ToArray());
        string second = EqualizerProfiles.Save("{}", "B", new double[10]);
        string merged = MediaPreferences.Merge(new() { EqualizerProfiles = first }, new(), new() { EqualizerProfiles = second }).EqualizerProfiles;
        Assert.Equal(2, EqualizerProfiles.Read(merged).Count);
        Assert.Equal(["B"], EqualizerProfiles.Read(EqualizerProfiles.Merge(merged, first, "{}")).Keys);
        Assert.ThrowsAny<ArgumentException>(() => EqualizerProfiles.Validate("{\"bad\":[1]}"));
        Assert.ThrowsAny<ArgumentException>(() => new MediaPreferences().With("EqualizerProfiles", "{\"bad\":null}"));
    }
    [Fact]
    public void EveryNewSettingAndPresetHasRuntimeLocalizedKeys()
    {
        foreach (string language in new[] { "en-US", "zh-CN" })
        {
            var keys = System.Xml.Linq.XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Resources", language + ".resw"))
                .Root!.Elements("data").Select(e => (string)e.Attribute("name")!).ToHashSet();
            foreach (var field in MediaPreferences.Fields.Concat(MediaPreferences.SubtitleChannelFields).Where(f => f.Group != "Library")) Assert.Contains("Text_M5" + field.Key, keys);
            foreach (string key in new[] { "DefaultPrimaryPosition", "DefaultPrimaryOverride", "DefaultSecondaryOverride", "EqCount", "EqLimit", "EqCapacity", "EqData" }) Assert.Contains("Text_M5" + key, keys);
            foreach (var preset in EqualizerProfiles.Presets) Assert.Contains("Text_M5Preset" + preset.Key, keys);
            Assert.Contains("Text_OsdSubPosition", keys); Assert.Contains("Text_OsdSubScale", keys);
        }
    }
    [Fact]
    public async Task MediaPreferencesRoundTripAndMergeIndividualFieldsAcrossWindows()
    {
        using var directory = new TemporaryDirectory();
        var store = new PlayerSettingsStore(Path.Combine(directory.Path, "settings.json"));
        var defaults = new PlayerSettings();
        var changed = defaults with { Media = defaults.Media with { MaximumVolume = "180", Font = "微软雅黑", AudioLanguage = "eng,zho", PrimaryPosition = "35", Eq1000 = "-3.5", EqualizerEnabled = "yes", SubtitleEncoding = "GB18030" }, VolumePercent = 145 };
        await store.SaveAsync(changed, Token);
        Assert.Equal(changed, await store.LoadAsync(Token));
        var otherWindow = defaults with { Media = defaults.Media with { SubtitleLanguage = "zho,eng", TextColor = "#FFEEDDCC" } };
        var merged = SharedPlayerSettings.Merge(changed, defaults, otherWindow);
        Assert.Equal("微软雅黑", merged.Media.Font);
        Assert.Equal("zho,eng", merged.Media.SubtitleLanguage);
        Assert.Equal("#FFEEDDCC", merged.Media.TextColor);
        Assert.Equal("eng,zho", merged.Media.AudioLanguage);
        Assert.Equal(changed.Media, SharedPlayerSettings.ForExistingWindow(changed, defaults, defaults).Media);
    }
    [Theory]
    [InlineData("MaximumVolume", "99")]
    [InlineData("Eq32", "13")]
    [InlineData("MarginX", "3.5")]
    [InlineData("Font", "")]
    [InlineData("TextColor", "white")]
    [InlineData("SecondaryOverride", "unsupported")]
    [InlineData("SubtitleEncoding", "not-an-encoding")]
    [InlineData("AudioLanguage", "eng;set pause no")]
    public void InvalidMediaPreferenceCannotReachEngine(string key, string value)
        => Assert.ThrowsAny<ArgumentException>(() => new MediaPreferences().With(key, value));

    [Fact]
    public async Task DamagedMediaPreferencesRecoverValidatedBackupWithoutChangingPositiveAutoHide()
    {
        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "settings.json");
        var saved = new PlayerSettings(ControlBarAutoHideTimeout: 4) { Media = new() { SecondaryOverride = "no" } };
        var store = new PlayerSettingsStore(path);
        await store.SaveAsync(saved, Token);
        File.Copy(path, path + ".backup");
        await File.WriteAllTextAsync(path, "{\"schemaVersion\":1,\"media\":{\"FontSize\":\"NaN\"}}", Token);
        Assert.Equal(saved, await store.LoadAsync(Token));
        Assert.True(store.Recovered);
        Assert.Single(Directory.GetFiles(directory.Path, "*.corrupt-*"));
    }
    [Fact]
    public void SeriesMatchingUsesMutualClosestPrefixAndEpisodeNumbers()
    {
        string[] videos = ["Show 01.mkv", "Show 02.mkv", "Show 03.mkv"];
        string[] subs = ["Show 1.eng.ass", "Show 2.eng.ass", "Show 3.eng.ass", "Other 02.srt"];
        var actual = SubtitleMatcher.Match(videos[1], videos, subs, new(), Token);
        Assert.Equal(["Show 2.eng.ass"], actual);
        // Like IINA's mpvFuzzy mode, name matching still runs stage-two fallback.
        Assert.Equal(["Show 2.eng.ass"], SubtitleMatcher.Match(videos[1], videos, subs, new() { SubtitleMatching = "name" }, Token));
    }
    [Fact]
    public void SeriesGroupingPreservesSupplementaryAndCombiningCharacters()
    {
        string[] videos = ["📺 Café\u0301 01.mkv", "📺 Café\u0301 02.mkv", "📺 Café\u0301 03.mkv"];
        string[] subtitles = ["📺 Café\u0301 1.srt", "📺 Café\u0301 2.srt", "📺 Café\u0301 3.srt"];
        Assert.Equal([subtitles[1]], SubtitleMatcher.Match(videos[1], videos, subtitles, new(), Token));
    }
    [Fact]
    public async Task MatcherScansConfiguredSubfoldersAndDeduplicatesVobSubPair()
    {
        using var directory = new TemporaryDirectory();
        string media = Path.Combine(directory.Path, "中文 film.mkv");
        await File.WriteAllTextAsync(media, "fixture", Token);
        Directory.CreateDirectory(Path.Combine(directory.Path, "Subs"));
        foreach (string file in new[] { "中文 film.en.srt", "中文 film.zh.srt", "中文 film.idx", "中文 film.sub", "Other.srt" })
            await File.WriteAllTextAsync(Path.Combine(directory.Path, "Subs", file), "fixture", Token);
        var actual = await SubtitleMatcher.FindAsync(media, new() { SubtitlePriority = "zh", SubtitleSearchPaths = "./*;Subs" }, Token);
        Assert.Equal(3, actual.Count);
        Assert.EndsWith("zh.srt", actual[0]);
        Assert.DoesNotContain(actual, p => p.EndsWith(".sub", StringComparison.Ordinal));
        Assert.Empty(await SubtitleMatcher.FindAsync(media, new() { SubtitleMatching = "no" }, Token));
    }
    [Fact]
    public async Task DeviceDisappearanceFallsBackWithoutUnpausing()
    {
        var client = new FakeMpvClient();
        client.Properties["pause"] = "yes";
        client.Properties["audio-device"] = "wasapi/missing";
        client.Properties["audio-device-list"] = new object[] { new Dictionary<string, object?> { ["name"] = "auto", ["description"] = "Default" } };
        await using var session = await client.Create();
        var output = await session.GetAudioOutputAsync();
        Assert.Equal("auto", output.Selected);
        Assert.True(output.FellBackToDefault);
        Assert.True(output.Paused);
        Assert.False(client.Commands.Reader.TryRead(out _));
        Assert.Single(client.Threads);
    }
    [Fact]
    public async Task EndpointRecoveryFailureIsReportedWithoutClosingPlaybackWorker()
    {
        var client = new FakeMpvClient { SetFailure = new InvalidOperationException("Endpoint unavailable") };
        client.Properties["pause"] = "yes"; client.Properties["audio-device"] = "wasapi/missing";
        client.Properties["audio-device-list"] = new object[] { new Dictionary<string, object?> { ["name"] = "auto", ["description"] = "Default" } };
        await using var session = await client.Create();
        var state = await session.GetAudioOutputAsync();
        Assert.True(state.RecoveryFailed); Assert.True(state.Paused); Assert.True(session.IsAvailable);
        Assert.Equal("wasapi/missing", state.Selected); Assert.Single(client.Threads);
    }
    private static async Task<MediaReady> Load(FakeMpvClient client, PlaybackSession session, long entry)
    {
        var loading = session.LoadAsync("https://localhost/media", true, Token);
        var command = await client.Next();
        client.Playlist(entry); client.Properties["playlist-playing-pos"] = 0L; client.Properties["playlist/0/id"] = entry;
        client.Properties["sid"] = "no"; client.Properties["secondary-sid"] = "no";
        client.Reply(command); client.Loaded(entry, "https://localhost/media");
        return await loading;
    }
    [Fact]
    public async Task StopDuringExternalLoadPreventsItsSelectionFromApplyingToNextMedia()
    {
        using var directory = new TemporaryDirectory();
        string sub = Path.Combine(directory.Path, "subtitle.srt"); await File.WriteAllTextAsync(sub, "fixture", Token);
        var client = new FakeMpvClient(); await using var session = await client.Create();
        var media = await Load(client, session, 41);
        var loading = session.LoadExternalTrackAsync(media.RequestId, media.PlaylistEntryId, sub, ExternalTrackKind.SecondarySubtitle, Token);
        var add = await client.Next(); Assert.Equal("sub-add", add.Arguments[0]);
        Assert.False(client.Commands.Reader.TryRead(out _));
        var stop = session.StopAsync(Token); var stopped = await client.Next(); client.Reply(stopped); await stop;
        client.Reply(add);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loading);
        Assert.False(client.Commands.Reader.TryRead(out _));
    }
    [Fact]
    public async Task FailedExternalAudioDoesNotStopOrReplaceMainMedia()
    {
        using var directory = new TemporaryDirectory();
        string audio = Path.Combine(directory.Path, "bad.wav"); await File.WriteAllTextAsync(audio, "bad", Token);
        var client = new FakeMpvClient(); await using var session = await client.Create();
        var media = await Load(client, session, 8);
        var loading = session.LoadExternalTrackAsync(media.RequestId, media.PlaylistEntryId, audio, ExternalTrackKind.Audio, Token);
        var command = await client.Next(); Assert.Equal("audio-add", command.Arguments[0]); client.Reply(command, -12);
        await Assert.ThrowsAsync<InvalidOperationException>(() => loading);
        Assert.Equal(media.RequestId, (await session.ReadAsync()).RequestId);
        Assert.False(client.Commands.Reader.TryRead(out _));
        Assert.Single(client.Threads);
    }
    [Fact]
    public async Task ManualTrackChoiceWaitsForExternalLoadAndStillRunsAfterItsFailure()
    {
        using var directory = new TemporaryDirectory();
        string sub = Path.Combine(directory.Path, "bad.srt"); await File.WriteAllTextAsync(sub, "bad", Token);
        var client = new FakeMpvClient(); await using var session = await client.Create();
        var media = await Load(client, session, 9);
        var loading = session.LoadExternalTrackAsync(media.RequestId, media.PlaylistEntryId, sub, ExternalTrackKind.PrimarySubtitle, Token);
        var add = await client.Next();
        var choice = session.TrackCommandForMediaAsync(media.RequestId, media.PlaylistEntryId, ["cycle", "sub"], Token);
        Assert.False(client.Commands.Reader.TryRead(out _));
        client.Reply(add, -12); await Assert.ThrowsAsync<InvalidOperationException>(() => loading);
        var cycle = await client.Next(); Assert.Equal(["cycle", "sub"], cycle.Arguments);
        client.Reply(cycle); await choice;
        Assert.Single(client.Threads);
    }
    [Fact]
    public async Task SelectionUsesExternalFileIdentityAfterReloadRenumbersTrack()
    {
        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "caption.srt");
        var client = new FakeMpvClient(); await using var session = await client.Create();
        var media = await Load(client, session, 10);
        object[] Tracks(long id) => [new Dictionary<string, object?> { ["id"] = id, ["type"] = "sub", ["external"] = true, ["external-filename"] = path }];
        client.Properties["track-list"] = Tracks(7);
        var before = await session.GetMediaInfoAsync(media.RequestId, media.PlaylistEntryId);
        client.Properties["track-list"] = Tracks(10);
        var selecting = session.SelectTrackAsync(before, "secondary-sid", 7, Token);
        var command = await client.Next(); Assert.Equal(["set", "secondary-sid", "10"], command.Arguments);
        client.Reply(command); await selecting;
    }
    [Fact]
    public void M5KeyboardBindingsRetainInputOwnershipAndFixedDirections()
    {
        Assert.Equal("add sub-scale 0.1", PlaybackKeyMap.Resolve(71, PlaybackModifiers.Shift, true)?.Value);
        Assert.Equal("add sub-scale -0.1", PlaybackKeyMap.Resolve(70, PlaybackModifiers.Shift, true)?.Value);
        Assert.Equal("add sub-pos -1", PlaybackKeyMap.Resolve(82, PlaybackModifiers.None, true)?.Value);
        Assert.Equal("add sub-pos 1", PlaybackKeyMap.Resolve(82, PlaybackModifiers.Shift, true)?.Value);
        Assert.Equal("add sub-pos 1", PlaybackKeyMap.Resolve(84, PlaybackModifiers.None, true)?.Value);
        Assert.Null(PlaybackKeyMap.Resolve(71, PlaybackModifiers.Shift, true, ownsInput: true));
    }
}

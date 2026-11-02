using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;
using Mani.Playback;
using Mani_App;

namespace Mani.UnitTests;

public sealed class ReviewFeedbackTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mani-review-" + Guid.NewGuid().ToString("N"));
    public ReviewFeedbackTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, true);
    private string FilePath(string name) => Path.Combine(_directory, name);
    private CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void NewOpenAndStopOwnTheirCompletionIndependentlyOfNavigation()
    {
        var opening = new MediaOpenOperation();
        long first = opening.Begin(), next = opening.Begin();
        Assert.False(opening.Complete(first)); Assert.True(opening.IsActive);
        Assert.True(opening.Complete(next)); Assert.False(opening.IsActive);
        long stopped = opening.Begin(); opening.Cancel();
        long replacement = opening.Begin();
        Assert.False(opening.Complete(stopped)); Assert.True(opening.IsActive);
        Assert.True(opening.Complete(replacement)); Assert.False(opening.IsActive);
    }

    [Theory]
    [InlineData("{\"volumePercent\":\"bad\"}")]
    [InlineData("{\"muted\":7}")]
    [InlineData("{\"schemaVersion\":\"bad\"}")]
    [InlineData("[]")]
    [InlineData("{\"volumePercent\":1e999}")]
    [InlineData("{\"controlBarAutoHideTimeout\":1e999}")]
    [InlineData("{\"leadingSidebarWidth\":999999999999999}")]
    public async Task SemanticDamageRecoversAndPreservesOneEvidenceCopy(string damage)
    {
        var path = FilePath("settings.json");
        await File.WriteAllTextAsync(path, damage, Token);
        await File.WriteAllTextAsync(path + ".backup", "{\"volumePercent\":37,\"controlBarAutoHideTimeout\":2.5}", Token);
        for (int i = 0; i < 3; i++)
        {
            var store = new PlayerSettingsStore(path);
            Assert.Equal(37, (await store.LoadAsync(Token)).VolumePercent);
            Assert.True(store.Recovered);
        }
        Assert.Single(Directory.GetFiles(_directory, "*.corrupt-*"));
        Assert.Equal(damage, await File.ReadAllTextAsync(path, Token));
    }

    [Fact]
    public async Task FutureSchemaDoesNotFallBackOrOverwrite()
    {
        var path = FilePath("settings.json"); const string future = "{\"schemaVersion\":99}";
        await File.WriteAllTextAsync(path, future, Token);
        await File.WriteAllTextAsync(path + ".backup", "{}", Token);
        await Assert.ThrowsAsync<InvalidDataException>(() => new PlayerSettingsStore(path).LoadAsync(Token));
        Assert.Equal(future, await File.ReadAllTextAsync(path, Token));
        Assert.Empty(Directory.GetFiles(_directory, "*.corrupt-*"));
    }

    [Fact]
    public async Task FailedSharedLoadCanRetryAfterRepairWithoutWritingDefaults()
    {
        string path = FilePath("settings.json"); await File.WriteAllTextAsync(path, "{", Token);
        var shared = new SharedPlayerSettings(new(path));
        await Assert.ThrowsAnyAsync<JsonException>(() => shared.LoadAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => shared.UpdateAsync(new(), new(VolumePercent: 23)));
        Assert.Equal("{", await File.ReadAllTextAsync(path, Token));
        await File.WriteAllTextAsync(path, "{\"volumePercent\":37}", Token);
        Assert.Equal(37, (await shared.LoadAsync()).VolumePercent);
    }

    [Fact]
    public async Task EverySettingRoundTripsAndParticipatesInCrossWindowMerge()
    {
        var baseline = new PlayerSettings();
        var properties = typeof(PlayerSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        foreach (var property in properties)
        {
            var edited = baseline with { };
            object before = property.GetValue(baseline)!;
            object value = before switch
            {
                bool flag => !flag,
                string => "zh-CN",
                Enum => Enum.GetValues(property.PropertyType).Cast<object>().First(v => !v.Equals(before)),
                int => 420,
                double d => d is > 0 and < 1 ? .7 : d + 1,
                MediaPreferences => new MediaPreferences { Font = "Arial", SecondaryOverride = "no", AudioLanguage = "eng" },
                _ => throw new InvalidOperationException("Add a non-default test value for " + property.Name)
            };
            property.SetValue(edited, value);
            var store = new PlayerSettingsStore(FilePath(property.Name + ".json"));
            await store.SaveAsync(edited, Token);
            Assert.Equal(edited, await store.LoadAsync(Token));
            Assert.Equal(edited, SharedPlayerSettings.Merge(baseline, baseline, edited));
            Assert.Equal(edited, SharedPlayerSettings.Merge(edited, baseline, baseline));
        }
    }

    [Fact]
    public async Task RapidSettingsChangesPublishNowAndFlushOnlyLatestSnapshot()
    {
        var path = FilePath("settings.json");
        var shared = new SharedPlayerSettings(new(path), TimeSpan.FromMinutes(1));
        var baseline = await shared.LoadAsync(); int changes = 0;
        shared.Changed += _ => changes++;
        var writes = new List<Task>();
        for (int i = 1; i <= 20; i++) writes.Add(shared.UpdateAsync(baseline, baseline with { VolumePercent = i }));
        Assert.Equal(20, shared.Current.VolumePercent); Assert.Equal(20, changes);
        Assert.False(File.Exists(path));
        await shared.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5), Token);
        await Task.WhenAll(writes);
        Assert.Equal(20, (await new PlayerSettingsStore(path).LoadAsync(Token)).VolumePercent);
        Assert.False(File.Exists(path + ".backup")); // No intermediate snapshots hit disk.
    }

    [Theory]
    [InlineData("ftp")][InlineData("sftp")][InlineData("rtsp")][InlineData("smb")][InlineData("https")]
    public void AllNetworkCredentialsAreRedacted(string scheme)
    {
        string url = $"{scheme}://user:secret@example.com/private?token=secret";
        Assert.True(DiagnosticPrivacy.IsNetworkSource(url));
        Assert.DoesNotContain("secret", DiagnosticPrivacy.Redact("failed " + url));
        Assert.False(DiagnosticPrivacy.IsNetworkSource(@"C:\media\movie.mkv"));
    }

    [Fact]
    public async Task CacheSizeDoesNotWaitForSlowDecode()
    {
        string source = FilePath("movie.mkv"); await File.WriteAllTextAsync(source, "video", Token);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cache = new ThumbnailPreviewCache(FilePath("thumbnails"), async (_, _, target, token) =>
        { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return target; });
        var capture = cache.GetAsync(source, 1, 10, cancel.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        try { Assert.Equal(0, await cache.SizeAsync(Token).WaitAsync(TimeSpan.FromSeconds(1), Token)); }
        finally { cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capture); }
    }

    [Theory]
    [InlineData(1)][InlineData(24)]
    public async Task EngineFailureBecomesUnavailableAndPendingCommandsFail(int eventId)
    {
        var client = new FakeMpvClient(); await using var session = await client.Create();
        var command = session.CommandAsync("cycle", "pause"); await client.Next();
        client.Events.Enqueue(new(eventId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => command.WaitAsync(TimeSpan.FromSeconds(5), Token));
        Assert.False(session.IsAvailable);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ReadAsync());
    }

    [Fact]
    public void SessionJournalStopsAtItsBudgetWithAnExplicitMarker()
    {
        var path = FilePath("state.jsonl");
        using (var log = new SessionTraceLog(path, maxBytes: 512))
            for (int i = 0; i < 100; i++) log.Append("state", new { i });
        Assert.InRange(new FileInfo(path).Length, 1, 550);
        Assert.Contains("log-budget-reached", File.ReadAllText(path));
        foreach (string line in File.ReadAllLines(path)) using (JsonDocument.Parse(line)) { }
    }

    [Fact]
    public void RetentionPreservesActiveLegacyEvidenceAndUnknownFiles()
    {
        string root = FilePath("logs"); Directory.CreateDirectory(root);
        string legacy = Path.Combine(root, "legacy"); Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "mpv.log"), "historical evidence");
        using var active = new SessionLogDirectory(root);
        var ordinary = new SessionLogDirectory(root); string removed = ordinary.Path; ordinary.Dispose();
        var evidence = new SessionLogDirectory(root); evidence.PreserveEvidence(); evidence.Dispose();
        var unknown = new SessionLogDirectory(root); File.WriteAllText(Path.Combine(unknown.Path, "user.txt"), "keep"); unknown.Dispose();
        SessionLogDirectory.Prune(root, keepSessions: 0, budgetBytes: 0);
        Assert.False(Directory.Exists(removed));
        Assert.True(Directory.Exists(active.Path)); Assert.True(Directory.Exists(legacy));
        Assert.True(Directory.Exists(evidence.Path)); Assert.True(Directory.Exists(unknown.Path));
    }

    [Theory]
    [InlineData("en-US")][InlineData("zh-CN")]
    public void EveryDynamicOsdKindHasAllItsResourceVariants(string language)
    {
        var keys = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Resources", language + ".resw"))
            .Root!.Elements("data").Select(e => (string)e.Attribute("name")!).ToHashSet();
        foreach (var kind in Enum.GetValues<OsdKind>())
        {
            if (kind is OsdKind.File or OsdKind.Seek or OsdKind.ShowTime or OsdKind.Navigation) continue;
            if (kind is OsdKind.AudioDelay or OsdKind.SubDelay or OsdKind.SecondarySubDelay)
                foreach (string suffix in new[] { "None", "Later", "Earlier" }) Assert.Contains("Text_Osd" + kind + suffix, keys);
            else Assert.Contains("Text_Osd" + kind, keys);
        }
    }
}

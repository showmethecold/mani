using System.Text.Json;
using Mani.Playback;

namespace Mani.UnitTests;

public sealed class PlayerSettingsTests
{
    [Theory]
    [InlineData(360, 360, 360, 360)] [InlineData(300, 500, 300, 500)]
    [InlineData(100, 900, 300, 500)] [InlineData(900, 100, 500, 300)]
    public async Task SidebarWidthsRemainIndependentAndRetainUnrelatedSettings(int left, int right, int expectedLeft, int expectedRight)
    {
        using var directory = new TemporaryDirectory();
        var token = TestContext.Current.CancellationToken;
        string path = Path.Combine(directory.Path, "settings.json");
        await File.WriteAllTextAsync(path, $$"""{"leadingSidebarWidth":{{left}},"trailingSidebarWidth":{{right}},"volumePercent":37,"custom":42}""", token);
        var store = new PlayerSettingsStore(path);
        var settings = await store.LoadAsync(token);
        Assert.False(settings.SidebarSettingsAtLeading);
        Assert.Equal(expectedLeft, settings.LeadingSidebarWidth);
        Assert.Equal(expectedRight, settings.TrailingSidebarWidth);
        var moved = settings with { SidebarSettingsAtLeading = true, LeadingSidebarWidth = 410 };
        await store.SaveAsync(moved, token);
        Assert.Equal(moved, await store.LoadAsync(token));
        using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(path, token));
        Assert.Equal(42, saved.RootElement.GetProperty("custom").GetInt32());
        string before = await File.ReadAllTextAsync(path, token);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.SaveAsync(moved with { TrailingSidebarWidth = 0 }, token));
        Assert.Equal(before, await File.ReadAllTextAsync(path, token));
    }

    [Theory]
    [InlineData(0, 0)] [InlineData(1, 2)] [InlineData(2, 4)] [InlineData(99, 99)]
    public async Task WindowPreferencesMigrateAndRoundTripWithoutLosingOtherKeys(int timing, int size)
    {
        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "settings.json");
        var token = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(path, $$"""{"schemaVersion":1,"resizeWindowTiming":{{timing}},"resizeWindowOption":{{size}},"custom":42}""", token);
        var store = new PlayerSettingsStore(path);
        var loaded = await store.LoadAsync(token);
        Assert.True(loaded.UsePhysicalResolution);
        Assert.False(loaded.UnlockWindowAspectRatio);
        Assert.Equal(timing == 99 ? ResizeWindowTiming.OnlyWhenOpen : (ResizeWindowTiming)timing, loaded.ResizeWindowTiming);
        Assert.Equal(size == 99 ? ResizeWindowOption.Normal : (ResizeWindowOption)size, loaded.ResizeWindowOption);
        var changed = loaded with { UsePhysicalResolution = false, UnlockWindowAspectRatio = true };
        await store.SaveAsync(changed, token);
        Assert.Equal(changed, await store.LoadAsync(token));
        using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(path, token));
        Assert.Equal(42, saved.RootElement.GetProperty("custom").GetInt32());
        string before = await File.ReadAllTextAsync(path, token);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.SaveAsync(changed with { ResizeWindowOption = (ResizeWindowOption)99 }, token));
        Assert.Equal(before, await File.ReadAllTextAsync(path, token));
    }

    [Theory]
    [InlineData(0, OscPosition.Floating)]
    [InlineData(1, OscPosition.Top)]
    [InlineData(2, OscPosition.Bottom)]
    [InlineData(-1, OscPosition.Floating)]
    [InlineData(99, OscPosition.Floating)]
    public async Task OscModeUsesIinaRawValuesAndUnknownValuesFallBack(int raw, OscPosition expected)
    {
        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "settings.json");
        var token = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(path, $$"""{"schemaVersion":1,"oscPosition":{{raw}},"oscHorizontal":0.7,"oscBottom":0.4,"volumePercent":37,"muted":true,"custom":42}""", token);
        var store = new PlayerSettingsStore(path);
        var loaded = await store.LoadAsync(token);
        Assert.Equal(new PlayerSettings(37, true, false, .7, .4, expected), loaded);
        foreach (var mode in new[] { OscPosition.Top, OscPosition.Bottom, OscPosition.Floating })
        {
            var changed = loaded with { OscPosition = mode };
            await store.SaveAsync(changed, token);
            Assert.Equal(changed, await new PlayerSettingsStore(path).LoadAsync(token));
            using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(path, token));
            Assert.Equal(42, saved.RootElement.GetProperty("custom").GetInt32());
        }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task DockingPreferencesRoundTripIndependentlyAndRetainOtherData(bool edgeToEdge, bool dock)
    {
        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "settings.json");
        var token = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(path, "{\"schemaVersion\":1,\"volumePercent\":39,\"muted\":true,\"pauseWhenOpen\":true,\"oscPosition\":2,\"oscHorizontal\":0.7,\"oscBottom\":0.3,\"futureKey\":42}", token);
        var store = new PlayerSettingsStore(path);
        var before = await store.LoadAsync(token);
        Assert.True(before.EdgeToEdgeVideo);
        Assert.False(before.DockedControls);
        var expected = before with { EdgeToEdgeVideo = edgeToEdge, DockedControls = dock };
        await store.SaveAsync(expected, token);
        Assert.Equal(expected, await new PlayerSettingsStore(path).LoadAsync(token));
        using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(path, token));
        Assert.Equal(42, saved.RootElement.GetProperty("futureKey").GetInt32());
        Assert.Equal(edgeToEdge, saved.RootElement.GetProperty("edgeToEdgeVideo").GetBoolean());
        Assert.Equal(dock, saved.RootElement.GetProperty("dockedControlBarAndTitlebar").GetBoolean());
    }

    [Fact]
    public async Task InvalidModeWriteCannotDamageExistingSettings()
    {
        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "settings.json");
        var token = TestContext.Current.CancellationToken;
        var store = new PlayerSettingsStore(path);
        await store.SaveAsync(new(OscPosition: OscPosition.Bottom), token);
        string before = await File.ReadAllTextAsync(path, token);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.SaveAsync(new(OscPosition: (OscPosition)99), token));
        Assert.Equal(before, await File.ReadAllTextAsync(path, token));
    }

    [Fact]
    public async Task ExistingSchemaOneGainsDefaultOscPositionWithoutLosingPreferences()
    {
        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "settings.json");
        var token = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(path, "{\"schemaVersion\":1,\"volumePercent\":37,\"muted\":true,\"pauseWhenOpen\":true,\"futureKey\":{\"value\":42}}", token);
        var store = new PlayerSettingsStore(path);
        var before = await store.LoadAsync(token);
        Assert.Equal(new PlayerSettings(37, true, true, .5, .1), before);
        var moved = before with { OscHorizontal = .7, OscBottom = .4 };
        await store.SaveAsync(moved, token);
        Assert.Equal(moved, await store.LoadAsync(token));
        using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(path, token));
        Assert.Equal(42, saved.RootElement.GetProperty("futureKey").GetProperty("value").GetInt32());
    }
    [Fact]
    public async Task OutOfBoundsImportedPositionIsClamped()
    {
        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "settings.json");
        var token = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(path, "{\"schemaVersion\":1,\"oscHorizontal\":-4,\"oscBottom\":9}", token);
        var settings = await new PlayerSettingsStore(path).LoadAsync(token);
        Assert.Equal(0, settings.OscHorizontal);
        Assert.Equal(1, settings.OscBottom);
    }
    [Theory]
    [InlineData(-.1, .5)]
    [InlineData(.5, 1.1)]
    [InlineData(double.NaN, .1)]
    [InlineData(.5, double.PositiveInfinity)]
    public async Task InvalidOscWritesCannotChangeStoredPreferences(double horizontal, double bottom)
    {
        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "settings.json");
        var token = TestContext.Current.CancellationToken;
        var store = new PlayerSettingsStore(path);
        await store.SaveAsync(new PlayerSettings(), token);
        string before = await File.ReadAllTextAsync(path, token);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.SaveAsync(new(OscHorizontal: horizontal, OscBottom: bottom), token));
        Assert.Equal(before, await File.ReadAllTextAsync(path, token));
    }
    [Fact]
    public async Task MissingSettingsUsePrototypeDefaultsAndRoundTripObservedValues()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "settings.json");
        var store = new PlayerSettingsStore(path);
        var token = TestContext.Current.CancellationToken;
        Assert.Equal(new PlayerSettings(), await store.LoadAsync(token));
        var expected = new PlayerSettings(37.5, true, true);
        await store.SaveAsync(expected, token);
        Assert.Equal(expected, await new PlayerSettingsStore(path).LoadAsync(token));
    }

    [Theory]
    [InlineData("{\"volume\":23,\"custom\":\"保留\"}", 23)]
    [InlineData("{\"volume\":23,\"volumePercent\":71,\"custom\":\"保留\"}", 71)]
    public async Task MigrationPrefersModernKeysAndPreservesUnknownDataAndBackup(string input, double volume)
    {
        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "settings.json");
        var token = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(path, input, token);
        var store = new PlayerSettingsStore(path);
        var settings = await store.LoadAsync(token);
        Assert.Equal(volume, settings.VolumePercent);
        await store.SaveAsync(settings, token);
        Assert.Equal(input, await File.ReadAllTextAsync(path + ".backup", token));
        using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(path, token));
        Assert.Equal(1, saved.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("保留", saved.RootElement.GetProperty("custom").GetString());
        Assert.Equal(settings, await store.LoadAsync(token));
    }

    [Theory]
    [InlineData("{not JSON")]
    [InlineData("{\"schemaVersion\":2,\"volumePercent\":40}")]
    [InlineData("{\"schemaVersion\":1,\"oscPosition\":\"top\"}")]
    [InlineData("{\"schemaVersion\":1,\"oscPosition\":null}")]
    [InlineData("{\"schemaVersion\":1,\"edgeToEdgeVideo\":\"true\"}")]
    [InlineData("{\"schemaVersion\":1,\"edgeToEdgeVideo\":null}")]
    [InlineData("{\"schemaVersion\":1,\"dockedControlBarAndTitlebar\":1}")]
    [InlineData("{\"schemaVersion\":1,\"dockedControlBarAndTitlebar\":null}")]
    public async Task CorruptOrFutureSettingsAreNeverOverwritten(string input)
    {
        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "settings.json");
        var token = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(path, input, token);
        var store = new PlayerSettingsStore(path);
        await Assert.ThrowsAnyAsync<Exception>(() => store.LoadAsync(token));
        await Assert.ThrowsAnyAsync<Exception>(() => store.SaveAsync(new(), token));
        Assert.Equal(input, await File.ReadAllTextAsync(path, token));
        Assert.Single(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public async Task ConcurrentSavesProduceOneCompleteDocumentAndCancellationPreservesIt()
    {
        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "settings.json");
        var token = TestContext.Current.CancellationToken;
        var store = new PlayerSettingsStore(path);
        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => store.SaveAsync(new(i), token)));
        var value = await store.LoadAsync(token);
        Assert.InRange(value.VolumePercent, 0, 19);
        string before = await File.ReadAllTextAsync(path, token);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(new(99), cancelled.Token));
        Assert.Equal(before, await File.ReadAllTextAsync(path, token));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }
}

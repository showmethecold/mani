using Mani_App;
using Mani.Playback;

namespace Mani.UnitTests;

public sealed class MultiWindowTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mani-windows-" + Guid.NewGuid().ToString("N"));
    private string SettingsPath => Path.Combine(_directory, "settings.json");
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
    private sealed class Window { public bool Idle; public bool Closing; }

    [Fact]
    public void NewWindowReusesFirstAvailableIdleAndDoesNotInterruptBusyWindow()
    {
        var registry = new WindowRegistry<Window>();
        Window New() => new();
        bool Idle(Window window) => window.Idle && !window.Closing;
        var playing = registry.GetOrCreate(Idle, New)!;
        var empty = registry.GetOrCreate(Idle, New)!;
        empty.Idle = true;
        Assert.Same(empty, registry.GetOrCreate(Idle, New));
        Assert.Equal(2, registry.Windows.Count);
        empty.Closing = true;
        Assert.NotSame(empty, registry.GetOrCreate(Idle, New));
        Assert.Contains(playing, registry.Windows);
    }

    [Fact]
    public void ClosingOneDoesNotRemoveOthersAndQuitBlocksReentrantNewWindows()
    {
        var registry = new WindowRegistry<Window>();
        var first = registry.GetOrCreate(_ => false, () => new())!;
        var second = registry.GetOrCreate(_ => false, () => new())!;
        registry.Remove(first);
        Assert.Same(second, Assert.Single(registry.Windows));
        var closing = registry.BeginQuit();
        registry.Remove(second);
        Assert.Same(second, Assert.Single(closing));
        Assert.Empty(registry.BeginQuit());
        Assert.Null(registry.GetOrCreate(_ => true, () => throw new Exception("Must not construct during shutdown")));
    }

    public static IEnumerable<object[]> PreferenceEdits()
    {
        var original = new PlayerSettings();
        yield return [original with { VolumePercent = 17 }];
        yield return [original with { Muted = true }];
        yield return [original with { PauseWhenOpen = true }];
        yield return [original with { OscHorizontal = .8 }];
        yield return [original with { OscBottom = .3 }];
        yield return [original with { EnableControlBarAutoHide = false }];
        yield return [original with { ControlBarAutoHideTimeout = .75 }];
        yield return [original with { EnableOsd = false }];
        yield return [original with { DisableOsdFileStart = true }];
        yield return [original with { DisableOsdPauseResume = true }];
        yield return [original with { DisableOsdSeek = true }];
        yield return [original with { DisableOsdSpeed = true }];
        yield return [original with { OsdAutoHideTimeout = 3.5 }];
        yield return [original with { OsdTextSize = 40 }];
        yield return [original with { OscPosition = OscPosition.Bottom }];
        yield return [original with { EdgeToEdgeVideo = false }];
        yield return [original with { DockedControls = true }];
        yield return [original with { UnlockWindowAspectRatio = true }];
        yield return [original with { UsePhysicalResolution = false }];
        yield return [original with { ResizeWindowTiming = ResizeWindowTiming.Never }];
        yield return [original with { ResizeWindowOption = ResizeWindowOption.Double }];
        yield return [original with { SidebarSettingsAtLeading = true }];
        yield return [original with { LeadingSidebarWidth = 420 }];
        yield return [original with { TrailingSidebarWidth = 440 }];
        yield return [original with { AlwaysFloatOnTop = true }];
    }

    [Theory]
    [MemberData(nameof(PreferenceEdits))]
    public async Task IndependentWindowEditsPreserveEveryOtherPreference(PlayerSettings edited)
    {
        var store = new PlayerSettingsStore(SettingsPath);
        var shared = new SharedPlayerSettings(store);
        var baseline = await shared.LoadAsync();
        await shared.UpdateAsync(baseline, edited);
        // Another stale window saves a disjoint setting. All 16 fields must survive.
        var other = edited.PauseWhenOpen ? baseline with { VolumePercent = 25 } : baseline with { PauseWhenOpen = true };
        await shared.UpdateAsync(baseline, other);
        Assert.Equal(edited.PauseWhenOpen ? edited with { VolumePercent = 25 } : edited with { PauseWhenOpen = true }, await store.LoadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RapidChangesAreOrderedAndUnknownSettingsArePreserved()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(SettingsPath, "{\"schemaVersion\":1,\"futureExtension\":{\"value\":42}}", TestContext.Current.CancellationToken);
        var store = new PlayerSettingsStore(SettingsPath);
        var shared = new SharedPlayerSettings(store);
        var baseline = await shared.LoadAsync();
        var writes = Enumerable.Range(1, 40).Select(i => shared.UpdateAsync(baseline, baseline with { VolumePercent = i })).ToArray();
        await Task.WhenAll(writes);
        Assert.Equal(40, (await store.LoadAsync(TestContext.Current.CancellationToken)).VolumePercent);
        Assert.Contains("futureExtension", await File.ReadAllTextAsync(SettingsPath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExistingSessionsKeepTheirVolumeAndNewSessionUsesLatestSavedValues()
    {
        var shared = new SharedPlayerSettings(new(SettingsPath));
        var baseline = await shared.LoadAsync();
        PlayerSettings local = baseline;
        shared.Changed += next => local = SharedPlayerSettings.ForExistingWindow(next, baseline, local);
        await shared.UpdateAsync(baseline, baseline with { VolumePercent = 23, Muted = true, OscPosition = OscPosition.Top });
        Assert.Equal(50, local.VolumePercent);
        Assert.False(local.Muted);
        Assert.Equal(OscPosition.Top, local.OscPosition);
        var newWindow = await shared.LoadAsync();
        Assert.Equal(23, newWindow.VolumePercent);
        Assert.True(newWindow.Muted);
        await shared.UpdateAsync(local, local with { PauseWhenOpen = true });
        Assert.Equal(23, shared.Current.VolumePercent);
        Assert.True(shared.Current.Muted);
    }

    [Fact]
    public void UnsavedLocalDragSurvivesBroadcastAndCanBeCommitted()
    {
        var baseline = new PlayerSettings();
        var drag = baseline with { LeadingSidebarWidth = 400 };
        var remote = baseline with { OscPosition = OscPosition.Bottom };
        var merged = SharedPlayerSettings.ForExistingWindow(remote, baseline, drag);
        Assert.Equal(400, merged.LeadingSidebarWidth);
        Assert.Equal(OscPosition.Bottom, merged.OscPosition);
        Assert.Equal(merged, SharedPlayerSettings.Merge(remote, remote, merged));
    }

    [Fact]
    public async Task FailedWriteDoesNotPoisonLaterUpdates()
    {
        var shared = new SharedPlayerSettings(new(SettingsPath));
        var baseline = await shared.LoadAsync();
        Directory.CreateDirectory(SettingsPath); // Occupy the file path with a directory.
        await Assert.ThrowsAnyAsync<Exception>(() => shared.UpdateAsync(baseline, baseline with { VolumePercent = 22 }));
        Directory.Delete(SettingsPath);
        await shared.UpdateAsync(shared.Current, shared.Current with { VolumePercent = 24 });
        Assert.Equal(24, (await new PlayerSettingsStore(SettingsPath).LoadAsync(TestContext.Current.CancellationToken)).VolumePercent);
    }

    [Fact]
    public async Task InvalidLoadNeverAllowsDefaultPreferencesToOverwriteFile()
    {
        Directory.CreateDirectory(_directory);
        const string corrupt = "{\"schemaVersion\":99}";
        await File.WriteAllTextAsync(SettingsPath, corrupt, TestContext.Current.CancellationToken);
        var shared = new SharedPlayerSettings(new(SettingsPath));
        await Assert.ThrowsAsync<InvalidDataException>(() => shared.LoadAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => shared.UpdateAsync(new(), new(20)));
        Assert.Equal(corrupt, await File.ReadAllTextAsync(SettingsPath, TestContext.Current.CancellationToken));
    }
}

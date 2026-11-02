using System.Globalization;
using System.Text;
using Mani.Playback;
using Mani_App;

namespace Mani.UnitTests;

public sealed class M4LibraryToolsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mani-library-" + Guid.NewGuid().ToString("N"));
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a1xkAAAAASUVORK5CYII=");
    public M4LibraryToolsTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, true);
    private static PlaybackHistoryEntry Entry(string source, string when = "2026-10-31T10:00:00Z")
        => new(PlaybackHistoryStore.Identity(source), source, "Title", 2, 90, DateTimeOffset.Parse(when, CultureInfo.InvariantCulture));
    [Theory]
    [InlineData("cafe", HistorySearchField.Filename, 1)]
    [InlineData("CAFÉ", HistorySearchField.FullPath, 1)]
    [InlineData("Season", HistorySearchField.Filename, 0)]
    [InlineData("Season", HistorySearchField.FullPath, 2)]
    [InlineData("日本語", HistorySearchField.Filename, 1)]
    public void SearchUsesIinaFieldAndLocaleRules(string query, HistorySearchField field, int expected)
    {
        var entries = new[] { Entry(@"C:\Season 2\Café.mkv"), Entry(@"C:\Season 2\日本語.mkv") };
        Assert.Equal(expected, HistoryBrowser.Query(entries, query, field, HistoryGrouping.Date, English).Sum(group => group.Entries.Count));
    }
    [Fact]
    public void RemoteSearchUsesDecodedPathAndGroupsByHostWithoutToken()
    {
        var a = Entry("https://host.test/Caf%C3%A9.mkv?token=alpha"); var b = Entry("https://host.test/Caf%C3%A9.mkv?token=beta");
        var groups = HistoryBrowser.Query([a, b], "cafe", HistorySearchField.Filename, HistoryGrouping.Location, English);
        Assert.Equal("host.test", Assert.Single(groups).Label); Assert.Equal(2, groups[0].Entries.Count);
        Assert.Empty(HistoryBrowser.Query([a, b], "token", HistorySearchField.FullPath, HistoryGrouping.Date, English));
    }
    [Fact]
    public void DateGroupingUsesLocalDayAndKeepsOrderWithinDay()
    {
        var early = Entry(@"C:\a.mkv", "2026-10-31T01:00:00Z"); var late = Entry(@"C:\b.mkv", "2026-10-31T10:00:00Z"); var older = Entry(@"C:\c.mkv", "2026-10-30T23:00:00Z");
        var zone = TimeZoneInfo.CreateCustomTimeZone("test", TimeSpan.FromHours(-7), "test", "test");
        var groups = HistoryBrowser.Query([late, early, older], "", HistorySearchField.FullPath, HistoryGrouping.Date, English, zone);
        Assert.Equal(2, groups.Count); Assert.Equal(late, Assert.Single(groups[0].Entries)); Assert.Equal(new[] { early, older }, groups[1].Entries);
    }
    [Fact]
    public void LocationGroupingUsesNaturalOrder()
    {
        var groups = HistoryBrowser.Query([Entry(@"C:\Season 10\a.mkv"), Entry(@"C:\Season 2\b.mkv")], "", HistorySearchField.FullPath, HistoryGrouping.Location, English);
        Assert.Equal(@"C:\Season 2", groups[0].Label);
    }
    [Fact]
    public void LargeHistoryQueryPreservesDistinctSourcesWithoutMutatingInput()
    {
        var entries = Enumerable.Range(0, 10000).Select(i => Entry($@"C:\Season {i % 20}\film {i}.mkv")).ToArray();
        var groups = HistoryBrowser.Query(entries, "Season 12", HistorySearchField.FullPath, HistoryGrouping.Location, English);
        Assert.Equal(500, Assert.Single(groups).Entries.Count); Assert.Equal(10000, entries.Length);
    }
    private static PlaylistSnapshot Playlist(params string[] paths) => new(paths.Select((path, i) => new PlaylistEntry(i, path, "ignored", i == 0, i == 0)).ToArray(), false);
    [Fact]
    public async Task PlaylistSavesUtf8OrderedDuplicatesAndCompleteUrls()
    {
        string file = Path.Combine(_directory, "list.m3u8"); string a = @"C:\中文 空格\01.mkv", url = "https://media.test/a?token=A%2B1&lang=zh";
        await PlaylistFile.SaveAsync(file, Playlist(a, url, a), Token);
        Assert.Equal(Encoding.UTF8.GetBytes(a + "\n" + url + "\n" + a + "\n"), await File.ReadAllBytesAsync(file, Token));
        Assert.Single(Directory.GetFiles(_directory));
    }
    [Theory]
    [InlineData("https://test/a\n#EXTINF:1,broken")]
    [InlineData("https://test/a\rb")]
    [InlineData("#a")]
    public async Task InvalidPlaylistLeavesExistingFileUntouched(string path)
    {
        string file = Path.Combine(_directory, "list.m3u8"); await File.WriteAllTextAsync(file, "original", Token);
        await Assert.ThrowsAsync<InvalidDataException>(() => PlaylistFile.SaveAsync(file, Playlist(path), Token));
        Assert.Equal("original", await File.ReadAllTextAsync(file, Token)); Assert.Single(Directory.GetFiles(_directory));
    }
    [Fact]
    public async Task CancelledSaveDoesNotReplaceExistingPlaylist()
    {
        string file = Path.Combine(_directory, "list.m3u8"); await File.WriteAllTextAsync(file, "original", Token);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PlaylistFile.SaveAsync(file, Playlist(@"C:\a.mkv"), cancel.Token));
        Assert.Equal("original", await File.ReadAllTextAsync(file, Token)); Assert.Single(Directory.GetFiles(_directory));
    }
    [Fact]
    public async Task CacheClearInvalidatesOtherWindowActiveAndQueuedWorkWithoutDeletingForeignFiles()
    {
        string media = Path.Combine(_directory, "a.mkv"), dir = Path.Combine(_directory, "cache"); await File.WriteAllTextAsync(media, "media", Token); Directory.CreateDirectory(dir);
        await File.WriteAllBytesAsync(Path.Combine(dir, "unrelated.png"), Png, Token); await File.WriteAllTextAsync(Path.Combine(dir, "decoder.log"), "keep", Token);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new ThumbnailPreviewCache(dir, async (_, _, target, token) => { entered.TrySetResult(); await release.Task.WaitAsync(token); await File.WriteAllBytesAsync(target, Png, token); return target; });
        var other = new ThumbnailPreviewCache(dir + Path.DirectorySeparatorChar, async (_, _, target, token) => { await File.WriteAllBytesAsync(target, Png, token); return target; });
        var active = first.GetAsync(media, 2, 100, Token); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        var queued = other.GetAsync(media, 3, 100, Token); var clear = other.ClearAsync(Token); release.SetResult();
        Assert.Null(await active); Assert.Null(await queued); await clear;
        Assert.Equal(0, await first.SizeAsync(Token)); Assert.True(File.Exists(Path.Combine(dir, "unrelated.png"))); Assert.True(File.Exists(Path.Combine(dir, "decoder.log")));
        Assert.NotNull(await first.GetAsync(media, 4, 100, Token)); Assert.Equal(Png.Length, await other.SizeAsync(Token));
    }
    [Fact]
    public async Task PreviewPreferenceDefaultsOnPersistsOffAndSurvivesUnrelatedWindowEdits()
    {
        var store = new PlayerSettingsStore(Path.Combine(_directory, "settings.json")); var original = await store.LoadAsync(Token);
        Assert.True(original.EnableThumbnailPreview); var off = original with { EnableThumbnailPreview = false };
        await store.SaveAsync(off, Token); Assert.False((await store.LoadAsync(Token)).EnableThumbnailPreview);
        Assert.False(SharedPlayerSettings.Merge(off, original, original with { Language = "zh-CN" }).EnableThumbnailPreview);
    }
}

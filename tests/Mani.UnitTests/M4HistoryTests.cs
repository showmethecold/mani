using Mani.Playback;
namespace Mani.UnitTests;

public sealed class M4HistoryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mani-history-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_directory, "history.json");
    public M4HistoryTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, true);
    [Fact]
    public async Task ThrottleKeepsLatestPositionAndFlushSurvivesRestart()
    {
        var store = new PlaybackHistoryStore(FilePath);
        await store.RecordAsync("https://test/a", "中文 media", 1, 60, true);
        var first = await File.ReadAllTextAsync(FilePath, TestContext.Current.CancellationToken);
        await store.RecordAsync("https://test/a", "中文 media", 20, 60, false);
        Assert.Equal(first, await File.ReadAllTextAsync(FilePath, TestContext.Current.CancellationToken));
        await store.FlushAsync();
        var entry = Assert.Single(await new PlaybackHistoryStore(FilePath).ReadAsync()); Assert.Equal(20, entry.Position); Assert.Equal("中文 media", entry.Title);
    }
    [Fact]
    public async Task CorruptMainRecoversBackupAndPreservesFailure()
    {
        var store = new PlaybackHistoryStore(FilePath);
        await store.RecordAsync("https://test/a", "A", 1, 60, true, true);
        await store.RecordAsync("https://test/a", "A", 2, 60, false, true);
        await File.WriteAllTextAsync(FilePath, "{invalid", TestContext.Current.CancellationToken);
        var recovered = new PlaybackHistoryStore(FilePath);
        Assert.Equal(1, Assert.Single(await recovered.ReadAsync()).Position); Assert.True(recovered.Recovered);
        Assert.Single(Directory.GetFiles(_directory, "*.corrupt-*"));
        await recovered.RecordAsync("https://test/a", "A", 3, 60, false, true);
        Assert.Equal(3, Assert.Single(await new PlaybackHistoryStore(FilePath).ReadAsync()).Position);
    }
    [Fact]
    public async Task FutureSchemaIsNotOverwritten()
    {
        const string text = "{\"SchemaVersion\":99,\"Entries\":[]}"; await File.WriteAllTextAsync(FilePath, text, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidDataException>(() => new PlaybackHistoryStore(FilePath).RecordAsync("https://test/a", "A", 0, 2, true));
        Assert.Equal(text, await File.ReadAllTextAsync(FilePath, TestContext.Current.CancellationToken));
    }
    [Fact]
    public async Task SharedWindowsDoNotLoseEachOthersEntriesAndClearDoesNotResurrect()
    {
        var store = new PlaybackHistoryStore(FilePath);
        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => store.RecordAsync("https://test/" + i, "item " + i, i, 60, true)));
        await store.FlushAsync(); Assert.Equal(20, (await new PlaybackHistoryStore(FilePath).ReadAsync()).Count);
        await store.RemoveAsync(); await store.RecordAsync("https://test/1", "item 1", 22, 60, false, true);
        Assert.Empty(await store.ReadAsync());
        await store.RecordAsync("https://test/1", "item 1", 22, 60, true, true); Assert.Single(await store.ReadAsync());
    }
    [Fact]
    public void UrlIdentityPreservesTokensCaseAndResourceWhileWindowsPathsIgnoreCase()
    {
        Assert.NotEqual(PlaybackHistoryStore.Identity("https://test/a?token=a"), PlaybackHistoryStore.Identity("https://test/a?token=b"));
        Assert.NotEqual(PlaybackHistoryStore.Identity("https://test/A"), PlaybackHistoryStore.Identity("https://test/a"));
        Assert.Equal(PlaybackHistoryStore.Identity(@"C:\Videos\A.mkv"), PlaybackHistoryStore.Identity(@"c:\videos\a.mkv"));
    }
    [Theory]
    [InlineData("null")]
    [InlineData("{\"Source\":\"C:\\\u0000bad\",\"Title\":\"Bad\",\"Identity\":\"x\"}")]
    public async Task MalformedEntryRecoversWithoutThrowing(string entry)
    {
        await File.WriteAllTextAsync(FilePath, "{\"SchemaVersion\":1,\"Entries\":[" + entry + "]}", TestContext.Current.CancellationToken);
        var store = new PlaybackHistoryStore(FilePath);
        Assert.Empty(await store.ReadAsync()); Assert.True(store.Recovered);
        Assert.Single(Directory.GetFiles(_directory, "*.corrupt-*"));
    }
    [Fact]
    public async Task ClearingCannotBeUndoneByBackupRecovery()
    {
        var store = new PlaybackHistoryStore(FilePath);
        await store.RecordAsync("https://test/private", "Private", 1, 60, true, true);
        await store.RemoveAsync();
        await File.WriteAllTextAsync(FilePath, "{broken", TestContext.Current.CancellationToken);
        Assert.Empty(await new PlaybackHistoryStore(FilePath).ReadAsync());
    }
}

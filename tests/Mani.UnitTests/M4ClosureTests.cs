using Mani.Playback;
namespace Mani.UnitTests;

public sealed class M4ClosureTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mani-m4-closure-" + Guid.NewGuid().ToString("N"));
    private CancellationToken Token => TestContext.Current.CancellationToken;
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UnchangedPausedSnapshotPersistsThrottledNewHistory(bool force)
    {
        string path = Path.Combine(_root, "paused-history.json");
        var store = new PlaybackHistoryStore(path);
        await store.RecordAsync("https://test/old", "Old", 1, 60, true, true);
        await store.RecordAsync("https://test/new", "New", 0, 60, true);
        Assert.Single(await new PlaybackHistoryStore(path).ReadAsync());
        if (!force) await Task.Delay(TimeSpan.FromSeconds(5.1), Token);
        await store.RecordAsync("https://test/new", "New", 0, 60, false, force);
        Assert.Equal("https://test/new", (await new PlaybackHistoryStore(path).ReadAsync())[0].Source);
    }
    public M4ClosureTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);
    [Theory]
    [InlineData(new long[] { 2, 3 }, -1, new long[] { 2, 3, 1, 4, 5 })]
    [InlineData(new long[] { 2, 3 }, 1, new long[] { 1, 4, 2, 3, 5 })]
    [InlineData(new long[] { 1, 3 }, -1, new long[] { 1, 3, 2, 4, 5 })]
    [InlineData(new long[] { 2, 5 }, 1, new long[] { 1, 3, 2, 4, 5 })]
    [InlineData(new long[] { 1, 2, 3, 4, 5 }, -1, new long[] { 1, 2, 3, 4, 5 })]
    [InlineData(new long[] { 1, 2, 3, 4, 5 }, 1, new long[] { 1, 2, 3, 4, 5 })]
    public void MultiSelectionMovesOneStepWithoutReversingOrCrossingBoundaries(long[] selected, int direction, long[] expected)
        => Assert.Equal(expected, PlaylistOrder.MoveSelection([1, 2, 3, 4, 5], selected, direction));
    [Fact]
    public void DropTextPreservesCompleteUrlsAndLocalizesFileUris()
    {
        const string url = "https://example.test/A?token=a%2Bb&lang=zh";
        var parsed = MediaSources.FromDropText(url + "\r\nfile:///C:/%E4%B8%AD%E6%96%87/a.mkv\n");
        Assert.Equal(new[] { url, @"C:\中文\a.mkv" }, parsed);
        Assert.Single(MediaSources.Expand([@"C:\Videos\A.mkv", @"c:\videos\a.MKV"]));
        Assert.Equal(2, MediaSources.Expand(["https://test/A?x=1", "https://test/a?x=1"]).Count);
    }
    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://test/a\nrelative.mkv")]
    [InlineData("https://test/a\0x")]
    public void InvalidDropIsRejectedAsAWhole(string value) => Assert.Empty(MediaSources.FromDropText(value));
    [Fact]
    public async Task MissingHistoryPrimaryRecoversValidBackupAndCanSaveAgain()
    {
        string path = Path.Combine(_root, "history.json");var store = new PlaybackHistoryStore(path);
        await store.RecordAsync("https://test/a", "A", 1, 60, true, true);
        await store.RecordAsync("https://test/a", "A", 2, 60, false, true);File.Delete(path);
        var restored = new PlaybackHistoryStore(path);Assert.Equal(1, Assert.Single(await restored.ReadAsync()).Position);Assert.True(restored.Recovered);
        await restored.RecordAsync("https://test/a", "A", 3, 60, false, true);
        Assert.Equal(3, Assert.Single(await new PlaybackHistoryStore(path).ReadAsync()).Position);
    }
    [Fact]
    public async Task MissingPrimaryWithDamagedBackupPreservesEvidence()
    {
        string path = Path.Combine(_root, "history.json");await File.WriteAllTextAsync(path + ".backup", "{broken", Token);
        var store = new PlaybackHistoryStore(path);Assert.Empty(await store.ReadAsync());Assert.True(store.Recovered);
        Assert.Single(Directory.GetFiles(_root, "*.corrupt-backup-*"));
    }
    [Fact]
    public async Task MissingPrimaryDoesNotOverwriteFutureVersionBackup()
    {
        string path = Path.Combine(_root, "history.json"), text = "{\"SchemaVersion\":99,\"Entries\":[]}";
        await File.WriteAllTextAsync(path + ".backup", text, Token);
        await Assert.ThrowsAsync<InvalidDataException>(() => new PlaybackHistoryStore(path).RecordAsync("https://test/a", "A", 1, 2, true));
        Assert.False(File.Exists(path));Assert.Equal(text, await File.ReadAllTextAsync(path + ".backup", Token));
    }
    [Fact]
    public async Task BudgetOnlyEvictsOwnedImages()
    {
        string media = Path.Combine(_root, "a.mkv"), dir = Path.Combine(_root, "cache");Directory.CreateDirectory(dir);File.WriteAllText(media,"media");
        string foreign = Path.Combine(dir,"unrelated.png");await File.WriteAllBytesAsync(foreign, new byte[100],Token);File.SetLastWriteTimeUtc(foreign,DateTime.UtcNow.AddYears(-1));
        var cache = new ThumbnailPreviewCache(dir,async (_,_,target,token) => {await File.WriteAllBytesAsync(target,new byte[10],token);return target;},budgetBytes:10);
        var first=await cache.GetAsync(media,1,100,Token);var second=await cache.GetAsync(media,2,100,Token);
        Assert.False(File.Exists(first));Assert.True(File.Exists(second));Assert.True(File.Exists(foreign));Assert.Equal(10,await cache.SizeAsync(Token));
    }
    [Fact]
    public async Task FailedCaptureCannotLeaveAnUnbudgetedPartialImage()
    {
        string media=Path.Combine(_root,"a.mkv"),dir=Path.Combine(_root,"cache");File.WriteAllText(media,"media");
        var cache=new ThumbnailPreviewCache(dir,async (_,_,target,token)=>{await File.WriteAllTextAsync(target,"partial",token);throw new OperationCanceledException();});
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>cache.GetAsync(media,1,100,Token));
        Assert.Empty(Directory.GetFiles(dir));Assert.Equal(0,await cache.SizeAsync(Token));
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SettingsRecoverValidBackupPreserveUnknownKeysAndPositiveHideDelay(bool missing)
    {
        string path=Path.Combine(_root,"settings.json");
        const string backup="{\"schemaVersion\":1,\"language\":\"zh-CN\",\"volumePercent\":72,\"controlBarAutoHideTimeout\":0,\"futureOption\":\"keep\"}";
        await File.WriteAllTextAsync(path+".backup",backup,Token);
        if(!missing) await File.WriteAllTextAsync(path,"{broken",Token);
        var store=new PlayerSettingsStore(path);var settings=await store.LoadAsync(Token);
        Assert.True(store.Recovered);Assert.True(store.RecoveryAdjustedAutoHide);Assert.Equal("zh-CN",settings.Language);Assert.Equal(72,settings.VolumePercent);Assert.Equal(2.5,settings.ControlBarAutoHideTimeout);
        await store.SaveAsync(settings with { EnableThumbnailPreview=false },Token);
        Assert.Contains("futureOption",await File.ReadAllTextAsync(path,Token));Assert.Equal(backup,await File.ReadAllTextAsync(path+".backup",Token));
        Assert.False((await new PlayerSettingsStore(path).LoadAsync(Token)).EnableThumbnailPreview);
        Assert.Equal(missing?0:1,Directory.GetFiles(_root,"*.corrupt-*").Length);
    }
    [Fact]
    public async Task SettingsFuturePrimaryCannotBeDowngradedUsingOldBackup()
    {
        string path=Path.Combine(_root,"settings.json");const string future="{\"schemaVersion\":99}";
        await File.WriteAllTextAsync(path,future,Token);await File.WriteAllTextAsync(path+".backup","{\"schemaVersion\":1}",Token);
        var store=new PlayerSettingsStore(path);await Assert.ThrowsAsync<InvalidDataException>(()=>store.LoadAsync(Token));
        await Assert.ThrowsAsync<InvalidDataException>(()=>store.SaveAsync(new(),Token));Assert.Equal(future,await File.ReadAllTextAsync(path,Token));
    }
    [Fact]
    public async Task IdleMetadataReadReportsRetryableTransition()
    {
        var client=new FakeMpvClient();await using var session=await client.Create();
        await Assert.ThrowsAsync<MediaUnavailableException>(()=>session.GetMediaInfoAsync());
    }
    private static object?[] Entries(params long[] ids)=>ids.Select(id=>(object?)new Dictionary<string,object?>{["id"]=id,["filename"]="https://test/"+id}).ToArray();
    [Fact]
    public async Task InsertUsesNativeIdsAndSerializesOtherEdits()
    {
        var client=new FakeMpvClient();await using var session=await client.Create();client.Properties["playlist"]=Entries(10,20,30);
        var insert=session.AppendPlaylistAsync(["https://test/a","https://test/b"],Token,beforeId:20);
        var a=await client.Next();Assert.Equal("append",a.Arguments[2]);
        var remove=session.RemovePlaylistEntriesAsync([30]);Assert.False(client.Commands.Reader.TryRead(out _));
        client.Properties["playlist"]=Entries(10,20,30,40);client.Reply(a);var b=await client.Next();
        client.Properties["playlist"]=Entries(10,20,30,40,50);client.Reply(b);
        var move=await client.Next();Assert.Equal(new[]{"playlist-move","3","1"},move.Arguments);client.Properties["playlist"]=Entries(10,40,20,30,50);client.Reply(move);
        move=await client.Next();Assert.Equal(new[]{"playlist-move","4","2"},move.Arguments);client.Properties["playlist"]=Entries(10,40,50,20,30);client.Reply(move);await insert;
        var deletion=await client.Next();Assert.Equal(new[]{"playlist-remove","4"},deletion.Arguments);client.Reply(deletion);await remove;
    }
    [Fact]
    public async Task ReplacedPlaylistCancelsRemainingInsertMoves()
    {
        var client=new FakeMpvClient();await using var session=await client.Create();client.Properties["playlist"]=Entries(10,20);
        var insert=session.AppendPlaylistAsync(["https://test/a","https://test/b"],Token,beforeId:20);
        var a=await client.Next();client.Properties["playlist"]=Entries(10,20,30);client.Reply(a);
        var b=await client.Next();client.Properties["playlist"]=Entries(10,20,30,40);client.Reply(b);
        var move=await client.Next();var replacement=session.OpenAsync("https://test/new",true,Token);var load=await client.Next();
        client.Properties["playlist"]=Entries(99);client.Reply(load);client.Reply(move);await replacement;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>insert);
        Assert.False(client.Commands.Reader.TryRead(out _));
    }
}

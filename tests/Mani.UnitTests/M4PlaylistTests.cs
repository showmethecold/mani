using Mani.Playback;
namespace Mani.UnitTests;

public sealed class M4PlaylistTests
{
    [Fact]
    public async Task PlaylistExpandedBeforeReplyKeepsKnownChildOwnership()
    {
        var client = new FakeMpvClient(); await using var session = await client.Create();
        var load = session.LoadAsync("https://test/list.m3u8", true, TestContext.Current.CancellationToken);
        var command = await client.Next(); client.Properties["playlist"] = Entries(5, 6); client.Reply(command);
        await session.ReadAsync(); // Reply sees children; removed parent 4 arrives afterwards.
        client.Events.Enqueue(new(6, EntryId: 4));
        client.Events.Enqueue(new(7, EntryId: 4, EndReason: 5, InsertId: 5, InsertCount: 2));
        client.Loaded(5, "https://test/child");
        var ready = await load.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(5, ready.PlaylistEntryId); Assert.Equal("https://test/child", (await session.ReadAsync()).Path);
    }
    private static object?[] Entries(params long[] ids) => ids.Select(id => (object?)new Dictionary<string, object?> { ["id"] = id, ["filename"] = "https://test/same", ["current"] = id == 10 }).ToArray();
    [Fact]
    public async Task MoveResolvesStableIdentityAgainstCurrentNativeOrder()
    {
        var client = new FakeMpvClient(); await using var session = await client.Create();
        client.Properties["playlist"] = Entries(30, 10, 20);
        var move = session.MovePlaylistEntryAsync(10, 30); var command = await client.Next();
        Assert.Equal(new[] { "playlist-move", "1", "0" }, command.Arguments); client.Reply(command); await move;
        Assert.Equal(3, (await session.GetPlaylistAsync()).Entries.Count);
    }
    [Fact]
    public async Task RemovalUsesDescendingIndicesAndLeavesDuplicateSourcesIndependent()
    {
        var client = new FakeMpvClient(); await using var session = await client.Create(); client.Properties["playlist"] = Entries(10, 20, 30);
        var remove = session.RemovePlaylistEntriesAsync([10, 30]);
        var a = await client.Next(); await session.ReadAsync();
        Assert.False(client.Commands.Reader.TryRead(out _));
        client.Properties["playlist"] = Entries(10, 20); client.Reply(a);
        var b = await client.Next();
        Assert.Equal(new[] { "playlist-remove", "2" }, a.Arguments); Assert.Equal(new[] { "playlist-remove", "0" }, b.Arguments);
        client.Properties["playlist"] = Entries(20); client.Reply(b); await remove;
    }
    [Fact]
    public async Task AutomaticNextEntryGetsFreshGenerationAndOldEndCannotPoisonIt()
    {
        var client = new FakeMpvClient(); await using var session = await client.Create();
        var load = session.LoadAsync("https://test/a", cancellationToken: TestContext.Current.CancellationToken); var command = await client.Next(); client.Playlist(10); client.Reply(command); client.Loaded(10, "https://test/a"); var initial = await load;
        client.Properties["playlist"] = Entries(20, 30); client.Loaded(20, "https://test/b");
        var next = await session.ReadAsync(); Assert.Equal("https://test/b", next.Path); Assert.True(next.RequestId > initial.RequestId); Assert.Equal(20, next.PlaylistEntryId);
        client.Events.Enqueue(new(7, Error: -1, EntryId: 10, Message: "old failure"));
        Assert.Equal("https://test/b", (await session.ReadAsync()).Path);
    }
    [Fact]
    public async Task ManuallyPlayingCurrentEntryReloadsByStableId()
    {
        var client = new FakeMpvClient(); await using var session = await client.Create(); client.Properties["playlist"] = Entries(10, 20);
        var load = session.PlayPlaylistEntryAsync(20, paused: true, token: TestContext.Current.CancellationToken); var command = await client.Next(); Assert.Equal(new[] { "playlist-play-index", "1" }, command.Arguments);
        client.Reply(command); client.Loaded(20, "https://test/same"); Assert.Equal(20, (await load).PlaylistEntryId); Assert.True(client.Flag("pause"));
    }
    [Fact]
    public async Task ShuffleRestoresWithNativeCommandAndEditInvalidatesRestoration()
    {
        var client = new FakeMpvClient(); await using var session = await client.Create();
        var task = session.TogglePlaylistShuffleAsync(); var command = await client.Next(); Assert.Equal("playlist-shuffle", command.Arguments[0]); client.Reply(command); await task;
        Assert.True((await session.GetPlaylistAsync()).Shuffled);
        task = session.TogglePlaylistShuffleAsync(); command = await client.Next(); Assert.Equal("playlist-unshuffle", command.Arguments[0]); client.Reply(command); await task;
        task = session.AppendPlaylistAsync(["https://test/a"], TestContext.Current.CancellationToken); command = await client.Next(); client.Reply(command); await task;
        Assert.False((await session.GetPlaylistAsync()).Shuffled);
    }
    [Fact]
    public async Task ObsoleteChapterAndEditionCommandsAreRejectedBeforeNativeWrite()
    {
        var client = new FakeMpvClient(); await using var session = await client.Create();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.PlayChapterAsync(100, 42, 2));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.SetMediaPropertyAsync(100, 42, "edition", "1"));
        Assert.False(client.Commands.Reader.TryRead(out _));
    }
    [Fact]
    public async Task LargeAppendPreservesOrderWithOneAcknowledgedCommandAtATime()
    {
        var client = new FakeMpvClient(); await using var session = await client.Create();
        var append = session.AppendPlaylistAsync(Enumerable.Range(0, 130).Select(i => "https://test/" + i), TestContext.Current.CancellationToken);
        for (int i = 0; i < 130; i++)
        {
            var command = await client.Next();
            Assert.Equal(new[] { "loadfile", "https://test/" + i, "append" }, command.Arguments);
            await session.ReadAsync(); Assert.False(client.Commands.Reader.TryRead(out _));
            client.Reply(command);
        }
        await append;
    }
}

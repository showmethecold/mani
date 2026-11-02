using Mani.Playback;

namespace Mani.UnitTests;

public sealed class PlaybackSessionTests
{
    private static async Task Flush(PlaybackSession session) => await session.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

    [Fact]
    public async Task StartFileDoesNotPublishUnreadyMetadataFromThePreviousMedia()
    {
        var client = new FakeMpvClient();
        await using var session = await client.Create();
        var load = session.LoadAsync("https://localhost/b", cancellationToken: TestContext.Current.CancellationToken);
        var command = await client.Next(); client.Playlist(2); client.Reply(command);
        client.Properties["path"] = "https://localhost/a";
        client.Properties["media-title"] = "Previous media";
        client.Properties["duration"] = 600d;
        client.Events.Enqueue(new(6, EntryId: 2));
        var state = await session.ReadAsync();
        Assert.True(state.Loading);
        Assert.Equal("", state.Path);
        Assert.Equal("", state.Title);
        Assert.Equal(0, state.Duration);
        Assert.False(load.IsCompleted);
        client.Properties["path"] = "https://localhost/b";
        client.Events.Enqueue(new(8)); await load;
    }

    [Theory]
    [InlineData("paused-for-cache", true, PlaybackState.Buffering)]
    [InlineData("pause", true, PlaybackState.Paused)]
    [InlineData("seeking", true, PlaybackState.Seeking)]
    [InlineData("eof-reached", true, PlaybackState.Ended)]
    [InlineData("pause", false, PlaybackState.Playing)]
    public async Task PlaybackPhaseFollowsEngineObservations(string property, bool value, PlaybackState expected)
    {
        var client = new FakeMpvClient();
        await using var session = await client.Create();
        var load = session.LoadAsync("https://localhost/a", cancellationToken: TestContext.Current.CancellationToken);
        var command = await client.Next(); client.Playlist(1); client.Reply(command); client.Loaded(1, "https://localhost/a"); await load;
        client.Properties[property] = value;
        Assert.Equal(expected, (await session.ReadAsync()).State);
    }

    [Fact]
    public async Task ANewerQueuedOpenSupersedesAnIntermediateTargetBeforeNativeSubmission()
    {
        var client = new FakeMpvClient();
        await using var session = await client.Create();
        var token = TestContext.Current.CancellationToken;
        var a = session.LoadAsync("https://localhost/a", cancellationToken: token);
        var command = await client.Next();
        var b = session.LoadAsync("https://localhost/b", cancellationToken: token);
        var c = session.LoadAsync("https://localhost/c", cancellationToken: token);
        await Flush(session);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => a);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => b);
        client.Playlist(1); client.Reply(command);
        var last = await client.Next();
        Assert.Equal("https://localhost/c", last.Arguments[1]);
        client.Playlist(3); client.Reply(last); client.Loaded(3, "https://localhost/c");
        Assert.Equal(3, (await c).PlaylistEntryId);
    }

    [Fact]
    public async Task CancelledSeekHasATerminalTaskEvenBeforeNativeReply()
    {
        var client = new FakeMpvClient();
        await using var session = await client.Create();
        var load = session.LoadAsync("https://localhost/a", cancellationToken: TestContext.Current.CancellationToken);
        var command = await client.Next(); client.Playlist(1); client.Reply(command); client.Loaded(1, "https://localhost/a"); await load;
        using var cancellation = new CancellationTokenSource();
        var seek = session.SeekAsync(3, cancellation.Token);
        command = await client.Next(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => seek.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.True(seek.IsCanceled);
        client.Reply(command); await Flush(session);
        Assert.True(seek.IsCanceled);
    }

    [Fact]
    public async Task RepliesAreCorrelatedEvenWhenTheyArriveInReverseOrder()
    {
        var client = new FakeMpvClient();
        await using var session = await client.Create();
        var a = session.CommandAsync("show-text", "A");
        var b = session.CommandAsync("show-text", "B");
        var first = await client.Next(); var second = await client.Next();
        client.Reply(second); await Flush(session);
        Assert.True(b.IsCompletedSuccessfully);
        Assert.False(a.IsCompleted);
        client.Reply(first, -12);
        await Assert.ThrowsAsync<InvalidOperationException>(() => a);
        Assert.Single(client.Threads);
    }

    [Fact]
    public async Task CancellationSettlesBeforeALateReplyAndNeverCallsNativeFromTheCaller()
    {
        var client = new FakeMpvClient();
        await using var session = await client.Create();
        using var cancel = new CancellationTokenSource();
        var task = session.CommandAsync(cancel.Token, "subprocess", "test");
        var command = await client.Next();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        client.Reply(command); await Flush(session);
        Assert.True(task.IsCanceled);
        Assert.Contains(command.Id, client.Aborted);
        Assert.Single(client.Threads);
    }

    [Fact]
    public async Task ImmediateSubmissionFailureDoesNotWaitForAReply()
    {
        var client = new FakeMpvClient { SendFailure = new ArgumentException("Invalid command") };
        await using var session = await client.Create();
        await Assert.ThrowsAsync<ArgumentException>(() => session.CommandAsync("invalid"));
        var next = session.CommandAsync("show-text", "still usable");
        client.Reply(await client.Next()); await next;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReadinessRequiresFileLoadedRegardlessOfReplyOrdering(bool loadedFirst)
    {
        var client = new FakeMpvClient();
        await using var session = await client.Create();
        var load = session.LoadAsync("https://localhost/a", paused: true, cancellationToken: TestContext.Current.CancellationToken);
        var command = await client.Next(); client.Playlist(11);
        if (loadedFirst) client.Loaded(11, "https://localhost/a"); else client.Reply(command);
        await Flush(session);
        Assert.False(load.IsCompleted);
        Assert.Equal(PlaybackState.Loading, session.State);
        if (loadedFirst) client.Reply(command); else client.Loaded(11, "https://localhost/a");
        var ready = await load.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var snapshot = await session.ReadAsync();
        Assert.Equal(11, ready.PlaylistEntryId);
        Assert.Equal(PlaybackState.Paused, snapshot.State);
        Assert.Equal(1, snapshot.FileLoadedCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewOpenRejectsOldLoadedAndErrorEventsIncludingTheSamePath(bool samePath)
    {
        var client = new FakeMpvClient();
        await using var session = await client.Create();
        const string aSource = "https://localhost/a";
        string bSource = samePath ? aSource : "https://localhost/b";
        var a = session.LoadAsync(aSource, cancellationToken: TestContext.Current.CancellationToken);
        var aCommand = await client.Next();
        var b = session.LoadAsync(bSource, paused: true, cancellationToken: TestContext.Current.CancellationToken);
        await Flush(session);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => a);
        client.Playlist(11); client.Reply(aCommand);
        var bCommand = await client.Next();
        client.Loaded(11, aSource);
        client.Events.Enqueue(new(7, -13, EntryId: 11, EndReason: 4, Message: "Old failure"));
        client.Playlist(12); client.Reply(bCommand);
        var switching = await session.ReadAsync();
        Assert.Equal("", switching.Path);
        Assert.Equal("", switching.LoadFailure);
        Assert.True(switching.Loading);
        Assert.False(b.IsCompleted);
        client.Loaded(12, bSource);
        var ready = await b.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var snapshot = await session.ReadAsync();
        Assert.Equal(ready.RequestId, snapshot.RequestId);
        Assert.Equal(12, snapshot.PlaylistEntryId);
        Assert.Equal(bSource, snapshot.Path);
        Assert.Equal(1, snapshot.FileLoadedCount);
    }

    [Fact]
    public async Task FailedMediaCanBeReplacedByGoodMedia()
    {
        var client = new FakeMpvClient();
        await using var session = await client.Create();
        var bad = session.LoadAsync("https://localhost/bad", cancellationToken: TestContext.Current.CancellationToken);
        var command = await client.Next(); client.Playlist(1); client.Reply(command);
        client.Events.Enqueue(new(6, EntryId: 1));
        client.Events.Enqueue(new(7, -13, EntryId: 1, EndReason: 4, Message: "Cannot load media"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => bad);
        Assert.Equal(PlaybackState.Failed, (await session.ReadAsync()).State);
        var good = session.LoadAsync("https://localhost/good", cancellationToken: TestContext.Current.CancellationToken);
        command = await client.Next(); client.Playlist(2); client.Reply(command); client.Loaded(2, "https://localhost/good");
        await good;
        var recovered = await session.ReadAsync();
        Assert.Equal("", recovered.LoadFailure);
        Assert.Equal(PlaybackState.Playing, recovered.State);
    }

    [Fact]
    public async Task CancelledNetworkLoadStopsTheEngineAndDoesNotResurrectFromLateEvents()
    {
        var client = new FakeMpvClient();
        await using var session = await client.Create();
        using var cancel = new CancellationTokenSource();
        var load = session.LoadAsync("https://localhost/slow", cancellationToken: cancel.Token);
        var command = await client.Next(); client.Playlist(1); client.Reply(command);
        await Flush(session); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        var stop = await client.Next(); Assert.Equal("stop", stop.Arguments[0]); client.Reply(stop);
        client.Loaded(1, "https://localhost/slow");
        var state = await session.ReadAsync();
        Assert.Equal(PlaybackState.Stopped, state.State);
        Assert.Equal("", state.Path);
    }

    [Fact]
    public async Task SeekBurstRetainsOnlyTheNewestWaitingTargetAndPreservesPause()
    {
        var client = new FakeMpvClient();
        await using var session = await client.Create();
        var load = session.LoadAsync("https://localhost/a", paused: true, cancellationToken: TestContext.Current.CancellationToken);
        var command = await client.Next(); client.Playlist(1); client.Reply(command); client.Loaded(1, "https://localhost/a"); await load;
        var first = session.SeekAsync(1, TestContext.Current.CancellationToken); var seekCommand = await client.Next();
        var middle = session.SeekAsync(2, TestContext.Current.CancellationToken); var last = session.SeekAsync(3, TestContext.Current.CancellationToken);
        await Flush(session);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => middle);
        client.Reply(seekCommand); await first;
        var finalCommand = await client.Next(); Assert.Equal("3", finalCommand.Arguments[1]);
        client.Reply(finalCommand); await last;
        Assert.True((await session.ReadAsync()).Paused);
        Assert.False(client.Commands.Reader.TryRead(out _));
    }

    [Fact]
    public async Task CloseSettlesPendingWorkBeforeVideoDetachAndDestroysExactlyOnce()
    {
        var client = new FakeMpvClient();
        var session = await client.Create();
        var load = session.LoadAsync("https://localhost/slow", cancellationToken: TestContext.Current.CancellationToken);
        await client.Next();
        var command = session.CommandAsync("show-text", "pending"); await client.Next();
        session.BeginClose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => load.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => command.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.False(client.Disposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.ReadAsync());
        await Task.WhenAll(session.DisposeAsync().AsTask(), session.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(client.Disposed);
        Assert.Equal(PlaybackState.Closed, session.State);
        Assert.Single(client.Threads);
    }

    [Fact]
    public async Task QueueOverflowFailsPendingRequestsAndReleasesTheClient()
    {
        var client = new FakeMpvClient();
        var session = await client.Create();
        var task = session.CommandAsync("show-text", "pending"); await client.Next();
        client.Events.Enqueue(new(24));
        await Assert.ThrowsAsync<InvalidOperationException>(() => task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        await session.DisposeAsync();
        Assert.True(client.Disposed);
    }
}

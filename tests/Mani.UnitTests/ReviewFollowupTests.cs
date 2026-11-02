using Mani.Playback;
using Mani_App;

namespace Mani.UnitTests;

public sealed class ReviewFollowupTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mani-followup-" + Guid.NewGuid().ToString("N"));
    public ReviewFollowupTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, true);
    private string FilePath(string name) => Path.Combine(_directory, name);
    private CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FutureSettingsRemainIntactAndTheirErrorIsHandledByTheApplication()
    {
        string path = FilePath("settings.json"), future = "{\"schemaVersion\":99,\"volumePercent\":37}";
        await File.WriteAllTextAsync(path, future, Token);
        var shared = new SharedPlayerSettings(new(path));
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => shared.LoadAsync());
        Assert.True(SharedPlayerSettings.IsStorageError(error));
        await Assert.ThrowsAsync<InvalidOperationException>(() => shared.UpdateAsync(new(), new(VolumePercent: 20)));
        await shared.FlushAsync();
        Assert.Equal(future, await File.ReadAllTextAsync(path, Token));
        await File.WriteAllTextAsync(path, "{\"volumePercent\":37}", Token);
        Assert.Equal(37, (await shared.LoadAsync()).VolumePercent);
    }

    [Theory]
    [InlineData("\"Entries\":[]")]
    [InlineData("\"SchemaVersion\":0,\"Entries\":[]")]
    [InlineData("\"SchemaVersion\":null,\"Entries\":[]")]
    [InlineData("\"SchemaVersion\":\"1\",\"Entries\":[]")]
    public async Task MissingOrMalformedHistoryVersionRecoversBackup(string fields)
    {
        string path = FilePath("history.json"); var original = new PlaybackHistoryStore(path);
        await original.RecordAsync("https://test/a", "A", 12, 60, true, true);
        File.Copy(path, path + ".backup"); string damaged = "{" + fields + "}";
        await File.WriteAllTextAsync(path, damaged, Token);
        var store = new PlaybackHistoryStore(path);
        Assert.Equal(12, Assert.Single(await store.ReadAsync()).Position); Assert.True(store.Recovered);
        Assert.Equal(damaged, await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(_directory, "*.corrupt-*")), Token));
        await store.RecordAsync("https://test/a", "A", 20, 60, false, true);
        Assert.Equal(20, Assert.Single(await new PlaybackHistoryStore(path).ReadAsync()).Position);
    }

    [Fact]
    public async Task FutureHistoryRejectsAllMutationsWithoutBackupFallbackAndIsHandled()
    {
        string path = FilePath("history.json"), future = "{\"SchemaVersion\":99,\"Entries\":[]}";
        await File.WriteAllTextAsync(path, future, Token);
        await File.WriteAllTextAsync(path + ".backup", "{\"SchemaVersion\":1,\"Entries\":[]}", Token);
        var store = new PlaybackHistoryStore(path);
        Assert.True(SharedPlayerSettings.IsStorageError(await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadAsync())));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.RecordAsync("https://test/a", "A", 2, 60, true));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.RemoveAsync()); await store.FlushAsync();
        Assert.False(store.Recovered); Assert.Equal(future, await File.ReadAllTextAsync(path, Token));
        Assert.Empty(Directory.GetFiles(_directory, "*.corrupt-*"));
    }

    private static object?[] Entries(params long[] ids) => ids.Select(id => (object?)new Dictionary<string, object?>
        { ["id"] = id, ["filename"] = "https://test/" + id }).ToArray();
    private static async Task<MediaReady> Load(FakeMpvClient client, PlaybackSession session)
    {
        var loading = session.LoadAsync("https://test/a", true, TestContext.Current.CancellationToken); var command = await client.Next();
        client.Playlist(10); client.Reply(command); client.Loaded(10, "https://test/a"); return await loading;
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task AppendOrRemovalStopsAtFirstFailedAcknowledgement(bool removing)
    {
        var client = new FakeMpvClient(); await using var session = await client.Create(); client.Properties["playlist"] = Entries(10, 20, 30);
        var edit = removing ? session.RemovePlaylistEntriesAsync([10, 30]) : session.AppendPlaylistAsync(["https://test/a", "https://test/b"], Token);
        var first = await client.Next(); await session.ReadAsync(); Assert.False(client.Commands.Reader.TryRead(out _));
        client.Reply(first, -1); await Assert.ThrowsAsync<InvalidOperationException>(() => edit);
        Assert.False(client.Commands.Reader.TryRead(out _));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task StopCancelsRemainingAppendOrRemoval(bool removing)
    {
        var client = new FakeMpvClient(); await using var session = await client.Create(); client.Properties["playlist"] = Entries(10, 20, 30);
        var edit = removing ? session.RemovePlaylistEntriesAsync([10, 30]) : session.AppendPlaylistAsync(["https://test/a", "https://test/b"], Token);
        var first = await client.Next(); var stop = session.StopAsync(Token); var command = await client.Next();
        Assert.Equal("stop", command.Arguments[0]); client.Reply(command); await stop; client.Reply(first);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => edit); Assert.False(client.Commands.Reader.TryRead(out _));
    }
    [Fact]
    public async Task RemovalRechecksStableIdentityAfterEachReply()
    {
        var client = new FakeMpvClient(); await using var session = await client.Create(); client.Properties["playlist"] = Entries(10, 20, 30);
        var edit = session.RemovePlaylistEntriesAsync([10, 30]); var first = await client.Next();
        client.Properties["playlist"] = Entries(20, 10); client.Reply(first);
        var next = await client.Next(); Assert.Equal(new[] { "playlist-remove", "1" }, next.Arguments); client.Reply(next); await edit;
    }
    [Fact]
    public async Task EditionWaitsForDeleteReplyAndBlocksOldResumeUntilReload()
    {
        var client = new FakeMpvClient(); await using var session = await client.Create(); var media = await Load(client, session);
        var edit = session.SetMediaPropertyAsync(media.RequestId, media.PlaylistEntryId, "edition", "1"); var delete = await client.Next();
        Assert.Equal("delete-watch-later-config", delete.Arguments[0]);
        var save = session.SaveResumeAsync(media.RequestId, media.PlaylistEntryId);
        await session.ReadAsync(); Assert.False(client.Commands.Reader.TryRead(out _)); client.Reply(delete);
        var change = await client.Next(); Assert.Equal(new[] { "set", "edition", "1" }, change.Arguments);
        await session.ReadAsync(); Assert.False(client.Commands.Reader.TryRead(out _)); client.Reply(change); await edit;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => save); Assert.False(client.Commands.Reader.TryRead(out _));
        client.Loaded(10, "https://test/a"); var next = await session.ReadAsync(); Assert.True(next.RequestId > media.RequestId);
        save = session.SaveResumeAsync(next.RequestId, next.PlaylistEntryId); var checkpoint = await client.Next();
        Assert.Equal("write-watch-later-config", checkpoint.Arguments[0]); client.Reply(checkpoint); await save;
    }
    [Fact]
    public async Task EditionDoesNotChangeReplacementMediaAfterDeleteReply()
    {
        var client = new FakeMpvClient(); await using var session = await client.Create(); var media = await Load(client, session);
        var edit = session.ChangeMediaPropertyAsync(media.RequestId, media.PlaylistEntryId, "edition", "cycle"); var delete = await client.Next();
        var stop = session.StopAsync(Token); client.Reply(await client.Next()); await stop; client.Reply(delete);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => edit); Assert.False(client.Commands.Reader.TryRead(out _));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task FailedEditionReleasesResumeGuard(bool immediateFailure)
    {
        var client = new FakeMpvClient(); await using var session = await client.Create(); var media = await Load(client, session);
        var edit = session.SetMediaPropertyAsync(media.RequestId, media.PlaylistEntryId, "edition", "1"); var delete = await client.Next();
        if (immediateFailure) client.SendFailure = new InvalidOperationException("native send failed"); client.Reply(delete);
        if (!immediateFailure) client.Reply(await client.Next(), -1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => edit);
        var save = session.SaveResumeAsync(media.RequestId, media.PlaylistEntryId); client.Reply(await client.Next()); await save;
    }
    [Fact]
    public async Task DirectLoopAndSleepWritesInvalidateWarmPollCache()
    {
        var client = new FakeMpvClient(); await using var session = await client.Create(); await Load(client, session);
        await session.PollAsync(1, 1); await session.SetLoopAsync(PlaybackLoop.Playlist);
        var state = (await session.PollAsync(1, 1)).Snapshot; Assert.Equal("inf", state.PlaylistLoop); Assert.Equal("no", state.FileLoop);
        await session.SetLoopAsync(PlaybackLoop.Off); Assert.Equal("no", (await session.PollAsync(1, 1)).Snapshot.PlaylistLoop);
        client.Properties["pause"] = false; await session.ReadAsync(); await session.PollAsync(1, 1);
        await session.PauseForSystemSleepAsync(); Assert.True((await session.PollAsync(1, 1)).Snapshot.Paused);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task AsyncWriteReplyInvalidatesValuesPolledBeforeCompletion(bool toggle)
    {
        var client = new FakeMpvClient(); await using var session = await client.Create(); await Load(client, session);
        client.Properties["pause"] = false; await session.PollAsync(1, 1);
        Task write = toggle ? session.TogglePauseAsync() : session.SetPausedAsync(true); var command = await client.Next();
        Assert.False((await session.PollAsync(1, 1)).Snapshot.Paused); // A read before the native command takes effect.
        client.Properties["pause"] = true; client.Reply(command); await write;
        Assert.True((await session.PollAsync(1, 1)).Snapshot.Paused);
    }
    [Fact]
    public async Task ChapterResumesOnlyAfterSeekAndDoesNotUnpauseReplacement()
    {
        var client = new FakeMpvClient(); await using var session = await client.Create(); var media = await Load(client, session);
        var chapter = session.PlayChapterAsync(media.RequestId, media.PlaylistEntryId, 12); var seek = await client.Next();
        Assert.True(client.Flag("pause")); var stop = session.StopAsync(Token); client.Reply(await client.Next()); await stop; client.Reply(seek);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => chapter); Assert.True(client.Flag("pause"));
    }

    [Fact]
    public async Task CancelledScreenshotDrainsReplyRemovesOutputAndReusesReadyDecoder()
    {
        string source = FilePath("video.mkv"); await File.WriteAllTextAsync(source, "video", Token);
        var client = new FakeMpvClient();
        await using var capture = new ThumbnailFrameCapture(new("", "", ""), client.Create);
        using var driverCancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int shots = 0;
        var driver = Task.Run(async () =>
        {
            await foreach (var command in client.Commands.Reader.ReadAllAsync(driverCancellation.Token))
            {
                switch (command.Arguments[0])
                {
                    case "set": client.Properties[command.Arguments[1]] = command.Arguments[2]; break;
                    case "loadfile": client.Playlist(10); client.Properties["video-format"] = "h264"; client.Loaded(10, source); break;
                    case "seek": client.Properties["seeking"] = false; client.Properties["time-pos"] = command.Arguments[1]; break;
                    case "screenshot-to-file":
                        if (++shots == 2) { entered.TrySetResult(); await release.Task.WaitAsync(Token); }
                        await File.WriteAllTextAsync(command.Arguments[1], "pixels", Token); break;
                }
                client.Reply(command);
            }
        }, Token);
        try
        {
            await capture.CaptureAsync(source, 1, FilePath("first.png"), Token).WaitAsync(TimeSpan.FromSeconds(5), Token);
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
            var cancelled = capture.CaptureAsync(source, 2, FilePath("cancelled.png"), cancel.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token); cancel.Cancel();
            Assert.False(cancelled.IsCompleted);
            var latest = capture.CaptureAsync(source, 3, FilePath("latest.png"), Token);
            Assert.False(latest.IsCompleted); release.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
            await latest.WaitAsync(TimeSpan.FromSeconds(5), Token);
            Assert.Equal(1, capture.SessionsCreated); Assert.False(client.Disposed); Assert.False(File.Exists(FilePath("cancelled.png")));
            Assert.Equal(2, Directory.GetFiles(_directory, "*.png").Length);
        }
        finally
        {
            release.TrySetResult(); driverCancellation.Cancel();
            try { await driver; } catch (OperationCanceledException) { }
        }
    }
}

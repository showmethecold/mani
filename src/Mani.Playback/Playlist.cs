// Port of IINA 1.5.1/build 180 PlayerCore playlist operations / MPVPlaylistItem.
// Copyright (c) 2016-2026 lhc and contributors. GPL-3.0; licenses/IINA-GPL-3.0.txt.
using System.Globalization;

namespace Mani.Playback;

public sealed record PlaylistEntry(long Id, string Path, string Title, bool Current, bool Playing);
public sealed record PlaylistSnapshot(IReadOnlyList<PlaylistEntry> Entries, bool Shuffled);

public sealed partial class PlaybackSession
{
    private bool _playlistShuffled, _playlistStopped;
    private long _playlistEpoch;
    private readonly SemaphoreSlim _playlistEdits = new(1, 1);
    private async Task EditPlaylistAsync(Func<Task> edit)
    {
        await _playlistEdits.WaitAsync().ConfigureAwait(false);
        try { await edit().ConfigureAwait(false); }
        finally { _playlistEdits.Release(); }
    }
    private void RequirePlaylistEpoch(long epoch)
    {
        if (_playlistEpoch != epoch) throw new OperationCanceledException("Playlist was replaced during editing.");
    }
    private PlaylistEntry[] ReadPlaylist() => (_client.Node("playlist") as IReadOnlyList<object?> ?? [])
        .OfType<IReadOnlyDictionary<string, object?>>()
        .Where(v => v.GetValueOrDefault("id") is long)
        .Select(v => new PlaylistEntry((long)v["id"]!, v.GetValueOrDefault("filename") as string ?? "",
            v.GetValueOrDefault("title") as string ?? "", v.GetValueOrDefault("current") is true,
            v.GetValueOrDefault("playing") is true)).ToArray();
    public Task<PlaylistSnapshot> GetPlaylistAsync() => Invoke(() => { Pump(); return new PlaylistSnapshot(Array.AsReadOnly(ReadPlaylist()), _playlistShuffled); });

    public Task AppendPlaylistAsync(IEnumerable<string> paths, CancellationToken token = default, long? beforeId = null)
    {
        var sources = paths.ToArray();
        foreach (var path in sources) { ArgumentException.ThrowIfNullOrWhiteSpace(path); if (path.Contains('\0')) throw new ArgumentException("Invalid source."); }
        return EditPlaylistAsync(async () =>
        {
            var context = await Invoke(() => (_playlistEpoch, Ids: ReadPlaylist().Select(e => e.Id).ToHashSet())).ConfigureAwait(false);
            foreach (var path in sources) await Invoke(() =>
            {
                token.ThrowIfCancellationRequested(); RequirePlaylistEpoch(context._playlistEpoch);
                _playlistShuffled = false;
                return SendCommand(["loadfile", path, "append"], token);
            }).Unwrap().ConfigureAwait(false);
            if (beforeId is null) return;
            var added = await Invoke(() =>
            {
                RequirePlaylistEpoch(context._playlistEpoch);
                return ReadPlaylist().Where(e => !context.Ids.Contains(e.Id)).Select(e => e.Id).ToArray();
            }).ConfigureAwait(false);
            foreach (long id in added)
            {
                token.ThrowIfCancellationRequested();
                await MovePlaylistEntryCoreAsync(id, beforeId, context._playlistEpoch).ConfigureAwait(false);
            }
        });
    }
    public async Task<MediaReady> PlayPlaylistEntryAsync(long entryId, bool paused = false, CancellationToken token = default)
    {
        Interlocked.Increment(ref _sourceRequest);
        var load = await Invoke(() =>
        {
            Pump(); token.ThrowIfCancellationRequested();
            var entry = ReadPlaylist().FirstOrDefault(e => e.Id == entryId) ?? throw new OperationCanceledException("Playlist entry no longer exists.");
            _currentLoad?.Cancel(); _queuedLoad?.Cancel(); CancelSeeks();
            var request = new MediaLoad(++_generation, entry.Path, paused, token) { PlaylistEntry = entryId };
            _currentLoad = _queuedLoad = request;
            _playlistStopped = false;
            _loading = true; _terminal = PlaybackState.Idle; _error = _loadFailure = "";
            _metadata.Invalidate(); DispatchLoad();
            return request;
        }).ConfigureAwait(false);
        return await load.Ready.Task.ConfigureAwait(false);
    }
    public Task RemovePlaylistEntriesAsync(IEnumerable<long> ids)
    {
        var selected = ids.ToHashSet();
        return EditPlaylistAsync(async () =>
        {
            var context = await Invoke(() => (_playlistEpoch, Ids: ReadPlaylist().Where(e => selected.Contains(e.Id)).Select(e => e.Id).Reverse().ToArray())).ConfigureAwait(false);
            foreach (long id in context.Ids) await Invoke(() =>
            {
                RequirePlaylistEpoch(context._playlistEpoch);
                int index = Array.FindIndex(ReadPlaylist(), e => e.Id == id);
                if (index < 0) return Task.CompletedTask;
                _playlistShuffled = false;
                return (Task)SendCommand(["playlist-remove", index.ToString(CultureInfo.InvariantCulture)]);
            }).Unwrap().ConfigureAwait(false);
        });
    }
    public Task ClearPlaylistAsync() => EditPlaylistAsync(() => Invoke(() => { _playlistShuffled = false; return SendCommand(["playlist-clear"]); }).Unwrap());
    public Task MovePlaylistEntryAsync(long id, long? beforeId) => EditPlaylistAsync(() => MovePlaylistEntryCoreAsync(id, beforeId));
    private Task MovePlaylistEntryCoreAsync(long id, long? beforeId, long? epoch = null) => Invoke(() =>
    {
        if (epoch is long expected) RequirePlaylistEpoch(expected);
        var entries = ReadPlaylist(); int from = Array.FindIndex(entries, e => e.Id == id);
        int to = beforeId is null ? entries.Length : Array.FindIndex(entries, e => e.Id == beforeId);
        if (from < 0 || to < 0 || from == to || from + 1 == to) return Task.CompletedTask;
        _playlistShuffled = false;
        return SendCommand(["playlist-move", from.ToString(CultureInfo.InvariantCulture), to.ToString(CultureInfo.InvariantCulture)]);
    }).Unwrap();
    public Task TogglePlaylistShuffleAsync() => EditPlaylistAsync(() => Invoke(() =>
    {
        bool next = !_playlistShuffled;
        return SendCommand([next ? "playlist-shuffle" : "playlist-unshuffle"], reply: result => { if (result.Error >= 0) _playlistShuffled = next; });
    }).Unwrap());
    public Task ReorderPlaylistAsync(IEnumerable<long> order)
    {
        var ids = order.ToArray();
        return EditPlaylistAsync(() => ReorderPlaylistCoreAsync(ids));
    }
    private async Task ReorderPlaylistCoreAsync(long[] ids)
    {
        var context = await Invoke(() => (_playlistEpoch, Ids: ReadPlaylist().Select(e => e.Id).ToArray())).ConfigureAwait(false);
        if (ids.Length != context.Ids.Length || ids.Distinct().Count() != ids.Length || !ids.ToHashSet().SetEquals(context.Ids))
            throw new OperationCanceledException("Playlist changed before reordering.");
        if (ids.SequenceEqual(context.Ids)) return;
        // client.h explicitly permits reordering async commands. Keep acknowledged
        // moves sequential; skip already-correct neighbours before native dispatch.
        var order = context.Ids.ToList();
        for (int i = ids.Length - 1; i >= 0; i--)
        {
            int from = order.IndexOf(ids[i]);
            int to = i + 1 < ids.Length ? order.IndexOf(ids[i + 1]) : order.Count;
            if (from == to || from + 1 == to) continue;
            await MovePlaylistEntryCoreAsync(ids[i], i + 1 < ids.Length ? ids[i + 1] : null, context._playlistEpoch).ConfigureAwait(false);
            order.RemoveAt(from); order.Insert(to > from ? to - 1 : to, ids[i]);
        }
    }
    public Task MovePlaylistSelectionAsync(IEnumerable<long> selectedIds, int direction)
    {
        var selected = selectedIds.ToHashSet();
        return EditPlaylistAsync(async () =>
        {
            var entries = await GetPlaylistAsync().ConfigureAwait(false);
            var order = PlaylistOrder.MoveSelection(entries.Entries.Select(e => e.Id), selected, direction);
            await ReorderPlaylistCoreAsync(order).ConfigureAwait(false);
        });
    }
    public Task SortPlaylistAsync(bool byPath, bool descending) => EditPlaylistAsync(async () =>
    {
        var original = await GetPlaylistAsync().ConfigureAwait(false);
        var sorted = original.Entries.OrderBy(e => byPath ? e.Path : HistoryBrowser.Filename(e.Path), StringComparer.Ordinal).Select(e => e.Id).ToArray();
        if (descending) Array.Reverse(sorted);
        await ReorderPlaylistCoreAsync(sorted).ConfigureAwait(false);
    });
    private void AcceptAutomaticPlaylistStart(long entryId)
    {
        if (_loadingCommand is not null || _queuedLoad is not null || _currentLoad is { Ready.Task.IsCompleted: false } ||
            _playlistStopped || !ReadPlaylist().Any(e => e.Id == entryId)) return;
        _currentLoad = null;
        CancelSeeks();
        _entryOwners[entryId] = ++_generation;
        _terminal = PlaybackState.Idle; _error = _loadFailure = "";
        SetNative("ab-loop-a", "no"); SetNative("ab-loop-b", "no");
    }
}

// Windows one-step buttons use the same ordered selection as IINA's IndexSet drag.
public static class PlaylistOrder
{
    public static long[] MoveSelection(IEnumerable<long> order, IEnumerable<long> selected, int direction)
    {
        var ids = order.ToArray(); var set = selected.ToHashSet();
        if (direction < 0)
            for (int i = 1; i < ids.Length; i++) { if (set.Contains(ids[i]) && !set.Contains(ids[i - 1])) (ids[i - 1], ids[i]) = (ids[i], ids[i - 1]); }
        else if (direction > 0)
            for (int i = ids.Length - 2; i >= 0; i--) { if (set.Contains(ids[i]) && !set.Contains(ids[i + 1])) (ids[i], ids[i + 1]) = (ids[i + 1], ids[i]); }
        return ids;
    }
}

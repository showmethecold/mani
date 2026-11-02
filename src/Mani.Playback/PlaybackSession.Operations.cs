// Control order and ticket invalidation adapted from IINA 1.5.1/build 180 PlayerCore.
// Copyright (c) 2016 lhc. GPL-3.0; see licenses/IINA-GPL-3.0.txt.
using System.Globalization;

namespace Mani.Playback;

public sealed partial class PlaybackSession
{
    private long _generation, _engineGeneration, _engineEntry;
    private long _sourceRequest;
    private MediaLoad? _currentLoad, _queuedLoad, _loadingCommand;
    private readonly Dictionary<long, long> _entryOwners = [];
    private readonly List<MpvEvent> _loadEvents = [];
    private int _lifecycle = (int)PlaybackState.Idle;
    private bool _seeking;
    private PlaybackState _terminal = PlaybackState.Idle;
    public PlaybackState State => (PlaybackState)Volatile.Read(ref _lifecycle);

    // Compatibility contract: OpenAsync completes on the loadfile reply, not FILE_LOADED.
    public async Task OpenAsync(string path, bool paused = false, CancellationToken cancellationToken = default)
    {
        var load = await QueueLoadAsync(path, paused, cancellationToken).ConfigureAwait(false);
        await load.Accepted.Task.ConfigureAwait(false);
    }
    // Media readiness is explicit and remains distinct from decode restart or presentation.
    public async Task<MediaReady> LoadAsync(string path, bool paused = false, CancellationToken cancellationToken = default)
    {
        var load = await QueueLoadAsync(path, paused, cancellationToken).ConfigureAwait(false);
        return await load.Ready.Task.ConfigureAwait(false);
    }
    private async Task<MediaLoad> QueueLoadAsync(string source, bool paused, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        if (source.Contains('\0')) throw new ArgumentException("NUL is not valid in a media source.", nameof(source));
        long request = Interlocked.Increment(ref _sourceRequest);
        if (!(Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"))
        {
            source = Path.GetFullPath(source);
            bool exists = await Task.Run(() => File.Exists(source), token).WaitAsync(token).ConfigureAwait(false);
            if (request != Volatile.Read(ref _sourceRequest)) throw new OperationCanceledException("Open was superseded.");
            if (!exists) throw new FileNotFoundException("Media file not found.", source);
        }
        return await Invoke(() =>
        {
            token.ThrowIfCancellationRequested();
            if (request != Volatile.Read(ref _sourceRequest)) throw new OperationCanceledException("Open was superseded.");
            _currentLoad?.Cancel();
            _queuedLoad?.Cancel();
            CancelSeeks();
            var load = new MediaLoad(++_generation, source, paused, token);
            _playlistEpoch++;
            Journal("media-requested", new { requestId = load.Id, network = Uri.TryCreate(source, UriKind.Absolute, out var uri) && !uri.IsFile });
            _currentLoad = _queuedLoad = load;
            _playlistStopped = false;
            _loading = true;
            _terminal = PlaybackState.Idle;
            _error = _loadFailure = "";
            _metadata.Invalidate();
            Volatile.Write(ref _lifecycle, (int)PlaybackState.Loading);
            DispatchLoad();
            return load;
        }, "open", source).ConfigureAwait(false);
    }
    private void DispatchLoad()
    {
        if (_loadingCommand is not null || _queuedLoad is not { } load) return;
        _queuedLoad = null;
        if (load.Token.IsCancellationRequested) { CancelLoadIfRequested(); return; }
        _loadingCommand = load;
        try
        {
            // IINA pauses before loadfile and scopes A-B endpoints to one media item.
            SetNative("pause", load.Paused ? "yes" : "no");
            SetNative("ab-loop-a", "no");
            SetNative("ab-loop-b", "no");
            string[] command;
            if (load.PlaylistEntry is long target)
            {
                int index = Array.FindIndex(ReadPlaylist(), e => e.Id == target);
                if (index < 0) throw new OperationCanceledException("Playlist entry no longer exists.");
                _entryOwners[target] = load.Id;
                command = ["playlist-play-index", index.ToString(CultureInfo.InvariantCulture)];
            }
            else { _playlistShuffled = false; command = ["loadfile", load.Source, "replace"]; }
            ObserveFailure(SendCommand(command, reply: result =>
            {
                if (result.Error < 0)
                {
                    var error = new InvalidOperationException(result.Message);
                    load.Fail(error);
                    if (load.Id == _generation) { _loading = false; _loadFailure = error.Message; _terminal = PlaybackState.Failed; }
                }
                else
                {
                    // No later loadfile is submitted before this reply. Capture native IDs
                    // now, even when START_FILE has not arrived yet (including same-path opens).
                    if (load.PlaylistEntry is null && _client.Node("playlist") is IReadOnlyList<object?> entries)
                        foreach (var entry in entries.OfType<IReadOnlyDictionary<string, object?>>())
                            if (entry.GetValueOrDefault("id") is long id) _entryOwners.TryAdd(id, load.Id);
                    load.Accepted.TrySetResult();
                }
                foreach (var value in _loadEvents)
                {
                    if (value.Id == 6) _entryOwners.TryAdd(value.EntryId, load.Id);
                    HandleEvent(value);
                }
                _loadEvents.Clear();
                _loadingCommand = null;
                DispatchLoad();
            }));
        }
        catch (Exception error)
        {
            _loadingCommand = null;
            load.Fail(error);
            if (load.Id == _generation) { _loading = false; _loadFailure = error.Message; _terminal = PlaybackState.Failed; }
        }
    }
    private void HandleEvent(MpvEvent value)
    {
        switch (value.Id)
        {
            case 6:
                AcceptAutomaticPlaylistStart(value.EntryId);
                _engineEntry = value.EntryId;
                _engineGeneration = _entryOwners.GetValueOrDefault(value.EntryId);
                // Replaced playlist entries cannot start after this newer entry. Bound the
                // ownership map even when rapid replacement skipped their START/END pair.
                if (_engineGeneration == _generation)
                    foreach (var key in _entryOwners.Where(pair => pair.Value < _generation).Select(pair => pair.Key).ToArray())
                        _entryOwners.Remove(key);
                _metadata.Invalidate();
                if (_engineGeneration == _generation) { _loading = true; _seeking = false; }
                break;
            case 8:
                _metadata.Invalidate();
                if (_engineGeneration != _generation) break;
                _loading = false;
                _terminal = PlaybackState.Idle;
                _fileLoadedCount++;
                _currentLoad?.Ready.TrySetResult(new(_generation, _engineEntry));
                break;
            case 7:
                var owner = _entryOwners.GetValueOrDefault(value.EntryId);
                // A fast playlist can be expanded before the loadfile reply: the
                // reply already owns its children, while its removed parent is
                // absent from the node. Never replace those known owners with 0.
                if (value.EndReason == 5 && owner != 0)
                    for (int i = 0; i < value.InsertCount; i++) _entryOwners.TryAdd(value.InsertId + i, owner);
                _entryOwners.Remove(value.EntryId);
                if (owner != _generation || owner == 0) break;
                _metadata.Invalidate();
                if (value.EndReason == 5) break;
                _loading = _seeking = false;
                _terminal = value.Error < 0 ? PlaybackState.Failed : value.EndReason == 0 ? PlaybackState.Ended : PlaybackState.Stopped;
                if (value.Error < 0)
                {
                    _error = _loadFailure = value.Message;
                    _currentLoad?.Fail(new InvalidOperationException(value.Message));
                }
                else _currentLoad?.Cancel();
                break;
            case 17:
            case 18: _metadata.Invalidate(); break;
            case 20: if (_engineGeneration == _generation) _seeking = true; break;
            case 21:
                _playbackRestarts++;
                if (_engineGeneration == _generation) _seeking = false;
                break;
            case 22:
                if (value.Property is { } property)
                {
                    if (property.Name == "playlist") Interlocked.Increment(ref _playlistRevision);
                    if (property.Name == "audio-device-list") _audioDevicesDirty = true;
                    // Coalesced observations carry no media ID. Reconcile at reads;
                    // never apply old metadata while a replacement is pending.
                    if (_engineGeneration == _generation && !_loading)
                        _metadata.Handle(value.Reply, property);
                }
                break;
        }
    }
    private void CancelLoadIfRequested()
    {
        if (_activeSeek is { Token.IsCancellationRequested: true } active) active.Completion.TrySetCanceled(active.Token);
        if (_queuedSeek is { Token.IsCancellationRequested: true } queued) queued.Completion.TrySetCanceled(queued.Token);
        if (_currentLoad is not { } load || load.Ready.Task.IsCompleted || !load.Token.IsCancellationRequested) return;
        load.Cancel();
        _queuedLoad = null;
        ++_generation;
        ++_playlistEpoch;
        _loading = false;
        _terminal = PlaybackState.Stopped;
        _playlistStopped = true;
        CancelSeeks();
        // abort(loadfile) cannot cancel the subsequent network open. stop is necessary.
        ObserveFailure(SendCommand(["stop"]));
    }
    private static void ObserveFailure(Task task) => _ = task.ContinueWith(t => _ = t.Exception,
        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _sourceRequest);
        return Invoke(() =>
        {
        cancellationToken.ThrowIfCancellationRequested();
        _currentLoad?.Cancel();
        _queuedLoad?.Cancel();
        _queuedLoad = null;
        ++_generation;
        ++_playlistEpoch;
        CancelSeeks();
        _loading = false;
        _terminal = PlaybackState.Stopped;
        _playlistStopped = true;
        _metadata.Invalidate();
        return SendCommand(["stop"], cancellationToken);
        }, "stop").Unwrap();
    }

    public Task SetPausedAsync(bool paused) => SetAsync("pause", paused ? "yes" : "no");
    public async Task<bool> TogglePauseAsync()
    {
        var result = await Invoke(() =>
        {
            if (Flag("idle-active") || _loading) return (Task.CompletedTask, Flag("pause"));
            bool paused = Flag("eof-reached") ? false : !Flag("pause");
            if (Flag("eof-reached")) return (RestartFromEndAsync(_generation, _engineEntry), paused);
            return ((Task)SendCommand(["set", "pause", paused ? "yes" : "no"]), paused);
        }, "toggle-pause").ConfigureAwait(false);
        await result.Item1.ConfigureAwait(false);
        return await Invoke(() => Flag("pause")).ConfigureAwait(false);
    }
    private async Task RestartFromEndAsync(long request, long entry)
    {
        await SendCommand(["seek", "0", "absolute+exact"]).ConfigureAwait(false);
        await Invoke(() => { RequireMedia(request, entry); return SendCommand(["set", "pause", "no"]); }).Unwrap().ConfigureAwait(false);
    }
    public Task SetSpeedAsync(double speed)
    {
        if (!double.IsFinite(speed)) throw new ArgumentOutOfRangeException(nameof(speed));
        return SetAsync("speed", Math.Max(0.01, speed).ToString(CultureInfo.InvariantCulture));
    }
    public Task SetVolumeAsync(double volume)
    {
        if (!double.IsFinite(volume)) throw new ArgumentOutOfRangeException(nameof(volume));
        return Invoke(() => SendCommand(["set", "volume", Math.Clamp(volume, 0,
            Math.Clamp(Number("volume-max"), 100, 1000)).ToString(CultureInfo.InvariantCulture)])).Unwrap();
    }
    public Task StepFrameAsync(bool backward = false) => CommandAsync(backward ? "frame-back-step" : "frame-step");
    public Task CycleAbLoopAsync() => CommandAsync("ab-loop");
    public Task SetLoopAsync(PlaybackLoop loop) => Invoke(() =>
    {
        switch (loop)
        {
            case PlaybackLoop.Off: SetNative("loop-playlist", "no"); SetNative("loop-file", "no"); break;
            case PlaybackLoop.File: SetNative("loop-file", "inf"); break;
            case PlaybackLoop.Playlist: SetNative("loop-playlist", "inf"); SetNative("loop-file", "no"); break;
            default: throw new ArgumentOutOfRangeException(nameof(loop));
        }
        return true;
    });

    private sealed record SeekRequest(double Position, long Generation, CancellationToken Token, TaskCompletionSource Completion);
    private SeekRequest? _queuedSeek, _activeSeek;
    public Task SeekAsync(double seconds, CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(seconds) || seconds < 0) throw new ArgumentOutOfRangeException(nameof(seconds));
        return Invoke(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            _queuedSeek?.Completion.TrySetCanceled();
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _queuedSeek = new(seconds, _generation, cancellationToken, completion);
            return completion.Task;
        }, "seek", seconds.ToString(CultureInfo.InvariantCulture)).Unwrap();
    }
    private void DispatchSeek()
    {
        if (_activeSeek is not null || _queuedSeek is not { } seek) return;
        _queuedSeek = null;
        if (seek.Token.IsCancellationRequested || seek.Generation != _generation)
        { seek.Completion.TrySetCanceled(); return; }
        if (_loading || Flag("idle-active"))
        { seek.Completion.TrySetException(new InvalidOperationException("No loaded media to seek.")); return; }
        _activeSeek = seek;
        ObserveFailure(SendCommand(["seek", seek.Position.ToString(CultureInfo.InvariantCulture), "absolute+exact"],
            reply: result =>
            {
                _activeSeek = null;
                if (seek.Token.IsCancellationRequested || seek.Generation != _generation) seek.Completion.TrySetCanceled();
                else if (result.Error < 0) seek.Completion.TrySetException(new InvalidOperationException(result.Message));
                else seek.Completion.TrySetResult();
            }));
    }
    private void CancelSeeks(Exception? error = null)
    {
        foreach (var seek in new[] { _queuedSeek, _activeSeek })
            if (error is null) seek?.Completion.TrySetCanceled(); else seek?.Completion.TrySetException(error);
        _queuedSeek = null;
        _seeking = false;
    }
}

using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Mani.Playback;

public sealed record PlaybackOptions(string DllPath, string DllSha256, string LogPath,
    bool Headless = false, bool KeepOpen = true, bool LoadAutoProfiles = true);

public sealed record PlaybackSnapshot(string Version, string Path, string Title, bool Loading,
    bool Idle, bool Paused, bool Ended, double Position, double Duration, double Volume,
    string VideoOutput, string HardwareDecoder, string VideoFormat, string ColorPrimaries,
    string Transfer, string DroppedFrames, string Error, string AudioOutput, string AudioDevice,
    string AudioBuffer, string AvSync, bool PausedForCache)
{
    // Unlike Error (last diagnostic log), this is an engine END_FILE failure.
    public string LoadFailure { get; init; } = "";
    public long FileLoadedCount { get; init; }
    public ColorOutputTarget? ColorOutput { get; init; }
    public PlaybackState State { get; init; }
    public long RequestId { get; init; }
    public long PlaylistEntryId { get; init; }
    public double Speed { get; init; } = 1;
    public bool Seeking { get; init; }
    public bool Muted { get; init; }
    public string FileLoop { get; init; } = "no";
    public string PlaylistLoop { get; init; } = "no";
    public string AbLoopA { get; init; } = "no";
    public string AbLoopB { get; init; } = "no";
}

public sealed record SleepPauseResult(bool Applied, bool Paused, double Position, long FileLoadedCount);

// The session owns one client and one event consumer. No native call runs on the UI thread.
public sealed partial class PlaybackSession : IAsyncDisposable
{
    private sealed record Work(Action Run, Action<Exception> Fail);
    private readonly BlockingCollection<Work> _queue = new();
    private readonly object _gate = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _worker;
    private readonly PlaybackOptions _options;
    private long _playlistRevision;
    public long PlaylistRevision => Interlocked.Read(ref _playlistRevision);
    private bool _stopping;
    public bool IsAvailable { get { lock (_gate) return !_stopping; } }
    private readonly ManualResetEventSlim _releaseNative = new();
    private bool _nativeReleased;
    private Exception? _failure;
    private readonly IMpvClient _client;
    private readonly MpvRequests _requests;
    private bool _loading;
    private string _error = "";
    private string _loadFailure = "";
    private string _version = "";
    private (int Width, int Height) _size;
    private long _playbackRestarts, _fileLoadedCount;
    private DisplayColorState? _displayColor;
    private ColorOutputTarget? _colorOutput;
    private bool _forceSdrOutput;
    private readonly ObservedMetadata _metadata = new();
    private readonly Func<string, string> _queryMetadata;
    private PlaybackTimingCapture? _timing;
    private SessionTraceLog? _journal;
    private readonly string _sessionId = Guid.NewGuid().ToString("N");
    private void Journal(string action, object details) => _journal?.Append(action, new { sessionId = _sessionId, mediaRequestId = _generation, details });

    private PlaybackSession(PlaybackOptions options, IMpvClient? client = null)
    {
        _options = options;
        _client = client ?? new MpvClient();
        _requests = new MpvRequests(_client, Journal);
        _queryMetadata = Get;
        _worker = Task.Factory.StartNew(Run, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }
    public static async Task<PlaybackSession> CreateAsync(PlaybackOptions options)
    {
        return await CreateAsync(options, new MpvClient()).ConfigureAwait(false);
    }
    internal static async Task<PlaybackSession> CreateAsync(PlaybackOptions options, IMpvClient client)
    {
        var session = new PlaybackSession(options, client);
        try { await session._ready.Task.ConfigureAwait(false); return session; }
        catch { await session.DisposeAsync().ConfigureAwait(false); throw; }
    }
    private Task<T> Invoke<T>(Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_stopping) return Task.FromException<T>(_failure ?? new ObjectDisposedException(nameof(PlaybackSession)));
            _queue.Add(new Work(() =>
            {
                try { completion.TrySetResult(action()); }
                catch (OperationCanceledException error) { completion.TrySetCanceled(error.CancellationToken); }
                catch (Exception error) { completion.TrySetException(error); }
            }, error => completion.TrySetException(error)));
        }
        return completion.Task;
    }
    private Task<T> Invoke<T>(Func<T> action, string operation, string? detail = null, string[]? arguments = null)
    {
        var capture = Volatile.Read(ref _timing);
        return capture is null ? Invoke(action) : InvokeTimed(action, capture, operation,
            arguments is null ? detail : System.Text.Json.JsonSerializer.Serialize(arguments));
    }
    private Task<T> InvokeTimed<T>(Func<T> action, PlaybackTimingCapture capture, string operation, string? detail)
    {
        long id = capture.Request(operation, detail);
        return ExecuteTimedAsync();
        async Task<T> ExecuteTimedAsync()
        {
            try
            {
                return await Invoke(() =>
                {
                    capture.Add("dispatched", id, operation);
                    var value = action();
                    capture.Add("accepted", id, operation);
                    return value;
                }).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                capture.Add("command-error", id, operation, error.Message);
                throw;
            }
        }
    }
    private void Run()
    {
        try
        {
            _client.Initialize(_options);
            if (!string.IsNullOrWhiteSpace(_options.LogPath))
                _journal = new SessionTraceLog(_options.LogPath + ".session.jsonl");
            Journal("session-ready", new { });
            _version = Get("mpv-version");
            _ready.TrySetResult();
            while (true)
            {
                lock (_gate) { if (_stopping) break; }
                _requests.CancelPending();
                CancelLoadIfRequested();
                Pump();
                ReconcileAudioDevice();
                DispatchSeek();
                if (_queue.TryTake(out var work, 20))
                {
                    lock (_gate)
                    {
                        if (_stopping) { work.Fail(new ObjectDisposedException(nameof(PlaybackSession))); break; }
                    }
                    work.Run();
                }
            }
        }
        catch (Exception error)
        {
            _failure = error;
            _ready.TrySetException(error);
        }
        finally
        {
            lock (_gate) { _stopping = true; _queue.CompleteAdding(); }
            while (_queue.TryTake(out var work)) work.Fail(_failure ?? new ObjectDisposedException(nameof(PlaybackSession)));
            Volatile.Read(ref _timing)?.Close("session-closed");
            Volatile.Write(ref _timing, null);
            var closed = _failure ?? new ObjectDisposedException(nameof(PlaybackSession));
            Journal("session-closing", new { error = _failure?.GetType().Name });
            _currentLoad?.Fail(closed);
            _queuedLoad?.Fail(closed);
            CancelSeeks(closed);
            _requests.Close(closed);
            if (_failure is null) _releaseNative.Wait();
            _client.Dispose();
            lock (_gate) { _nativeReleased = true; _releaseNative.Dispose(); }
            Journal("session-closed", new { });
            _journal?.Dispose();
            Volatile.Write(ref _lifecycle, (int)PlaybackState.Closed);
            _queue.Dispose();
        }
    }
    private void Pump()
    {
        for (int i = 0; i < 128; i++)
        {
            var data = _client.WaitEvent();
            if (data.Id == 0) return;
            if (data.Id is 6 or 7 or 8 or 20 or 21)
                Journal("engine-event", new { eventId = data.Id, entryId = data.EntryId, code = data.Error, reason = data.EndReason });
            if (_timing is { } capture && data.Id is 1 or 6 or 7 or 8 or 17 or 18 or 20 or 21 or 24)
                capture.Add("event", eventId: data.Id, playlistEntryId: data.Id is 6 or 7 ? data.EntryId : null,
                    error: data.Error, endReason: data.Id == 7 ? data.EndReason : null);
            if (data.Id == 1) throw new InvalidOperationException("mpv unexpectedly shut down.");
            if (data.Id == 24) throw new InvalidOperationException("mpv event queue overflow; restart the session.");
            if (data.Id == 5) { _requests.Complete(data); continue; }
            if (data.Id == 2) { _error = data.Message; continue; }
            if (_loadingCommand is not null && data.Id is 6 or 7 or 8 or 20 or 21)
                _loadEvents.Add(data);
            else HandleEvent(data);
        }
        _metadata.Invalidate();
    }
    // All writes run on the owning worker. Invalidate again on async completion:
    // a poll between dispatch and reply may have refreshed the previous value.
    private void SetNative(string name, string value)
    {
        _metadata.Invalidate();
        _client.Set(name, value);
    }
    private Task<object?> SendCommand(string[] arguments, CancellationToken token = default, Action<MpvEvent>? reply = null)
    {
        _metadata.Invalidate();
        return _requests.Send(arguments, token, result =>
        {
            _metadata.Invalidate();
            reply?.Invoke(result);
        });
    }
    private string Get(string name) => _client.Get(name);
    private bool Flag(string name) => _client.Flag(name);
    private double Number(string name) => _client.Number(name);
    // Engine playback-restart is a decode/readiness milestone, not a DWM presentation timestamp.
    public Task<long> GetPlaybackRestartCountAsync() => Invoke(() => { Pump(); return _playbackRestarts; });
    public Task<IReadOnlyDictionary<string, string>> GetPropertiesAsync(params string[] names) => Invoke<IReadOnlyDictionary<string, string>>(() =>
        names.ToDictionary(name => name, Get));
    public Task CommandAsync(params string[] arguments) => CommandAsync(CancellationToken.None, arguments);
    public Task CommandAsync(CancellationToken cancellationToken, params string[] arguments)
    {
        arguments = (string[])arguments.Clone();
        if (arguments is ["stop"]) return StopAsync(cancellationToken);
        if (arguments.Length is 2 or 3 && arguments[0] == "loadfile" && (arguments.Length == 2 || arguments[2] == "replace"))
            return OpenAsync(arguments[1], cancellationToken: cancellationToken);
        return Invoke(() => { _metadata.Invalidate(); return SendCommand(arguments, cancellationToken); },
            arguments.Length == 0 ? "empty-command" : arguments[0], arguments: arguments).Unwrap();
    }
    public async Task ClearAbLoopAsync()
    {
        await SetAsync("ab-loop-a", "no").ConfigureAwait(false);
        await SetAsync("ab-loop-b", "no").ConfigureAwait(false);
    }
    // IINA's active-core guard; native observation remains authoritative.
    public Task<SleepPauseResult> PauseForSystemSleepAsync() => Invoke(() =>
    {
        Pump();
        bool active = !Flag("idle-active");
        if (active) SetNative("pause", "yes");
        return new SleepPauseResult(active, Flag("pause"), Number("time-pos"), _fileLoadedCount);
    }, "sleep-pause");
    public Task SetAsync(string name, string value) => name == "glsl-shaders" && value.Length == 0
        ? CommandAsync("change-list", name, "clr", "") : CommandAsync("set", name, value);
    // Display data is supplied by the window host; the native composition chain has no HWND.
    public Task ConfigureDisplayAsync(DisplayColorState display) => Invoke(() =>
    {
        _displayColor = display;
        Read();
        return true;
    }, "configure-display");
    public Task SetForceSdrOutputAsync(bool force) => Invoke(() =>
    {
        _forceSdrOutput = force;
        Read();
        return true;
    }, "force-sdr");
    public Task<bool> GetForceSdrOutputAsync() => Invoke(() => _forceSdrOutput);
    private void RefreshColorOutput(PlaybackSnapshot state)
    {
        if (_options.Headless || _displayColor is null || state.Loading || state.Idle ||
            state.VideoOutput != "gpu-next" || state.ColorPrimaries.Length == 0 || state.Transfer.Length == 0) return;
        var target = ColorOutputPolicy.Select(state.ColorPrimaries, state.Transfer, _displayColor, _forceSdrOutput);
        if (target == _colorOutput) return;
        // All target changes share the native worker. Keep strict negotiation enabled and let
        // libplacebo own SetColorSpace/metadata; never change the DXGI chain behind its back.
        _colorOutput = null;
        foreach (var (name, value) in new[] {
            ("target-prim", target.Primaries), ("target-trc", target.Transfer), ("target-peak", target.Peak),
            ("target-colorspace-hint-mode", "target"), ("target-colorspace-hint-strict", "yes"),
            ("target-colorspace-hint", "yes") })
            SetNative(name, value);
        _colorOutput = target;
    }
    public Task ResizeAsync(int width, int height) => Invoke(() => { Resize(width, height); return true; }, "resize");
    private void Resize(int width, int height)
    {
        // A zero-sized panel has not been laid out yet; keep the existing composition size.
        if (_options.Headless || width <= 0 || height <= 0) return;
        var size = (Width: Math.Clamp(width, 1, 16384), Height: Math.Clamp(height, 1, 16384));
        if (size == _size) return;
        SetNative("d3d11-composition-size", $"{size.Width}x{size.Height}");
        _size = size;
    }
    public Task<PlaybackSnapshot> ReadAsync() => Invoke(() => Read());
    private PlaybackSnapshot Read(bool observed = false)
    {
        Pump();
        if (observed) _metadata.BeginRead(Environment.TickCount64);
        bool StateFlag(string name) => observed ? Metadata(name, true) == "yes" : Flag(name);
        double StateNumber(string name) => observed ?
            double.TryParse(Metadata(name, true), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : 0 : Number(name);
        var state = new PlaybackSnapshot(_version, Metadata("path", observed), Metadata("media-title", observed), _loading,
            StateFlag("idle-active"), StateFlag("pause"), StateFlag("eof-reached"),
            Number("time-pos"), Number("duration"), StateNumber("volume"), Metadata("current-vo", observed),
            Metadata("hwdec-current", observed), Metadata("video-format", observed), Metadata("video-params/primaries", observed),
            Metadata("video-params/gamma", observed), Get("frame-drop-count"), _error, Metadata("current-ao", observed),
            Metadata("audio-device", observed), Metadata("audio-buffer", observed), Get("avsync"), StateFlag("paused-for-cache"))
            { LoadFailure = _loadFailure, FileLoadedCount = _fileLoadedCount,
                RequestId = _generation, PlaylistEntryId = _engineEntry,
                Seeking = _seeking || StateFlag("seeking"), Speed = StateNumber("speed"), Muted = StateFlag("mute"),
                FileLoop = Metadata("loop-file", observed), PlaylistLoop = Metadata("loop-playlist", observed), AbLoopA = Metadata("ab-loop-a", observed), AbLoopB = Metadata("ab-loop-b", observed) };
        if (_loading || _engineGeneration != _generation || _terminal is PlaybackState.Stopped or PlaybackState.Failed)
            state = state with { Path = "", Title = "", Position = 0, Duration = 0, VideoFormat = "",
                ColorPrimaries = "", Transfer = "", Seeking = false, Ended = false, PlaylistEntryId = 0 };
        var phase = _loadFailure.Length > 0 ? PlaybackState.Failed : _loading ? PlaybackState.Loading :
            state.Ended ? PlaybackState.Ended : state.Idle || _terminal == PlaybackState.Stopped ? _terminal :
            state.PausedForCache ? PlaybackState.Buffering : state.Seeking ? PlaybackState.Seeking :
            state.Paused ? PlaybackState.Paused : PlaybackState.Playing;
        lock (_gate) { if (!_stopping) Volatile.Write(ref _lifecycle, (int)phase); }
        state = state with { State = phase };
        RefreshColorOutput(state);
        return state with { ColorOutput = _colorOutput };
    }
    private string Metadata(string name, bool observed) => observed ? _metadata.Read(name, _queryMetadata) : Get(name);
    public Task<SwapChainLease?> AcquireSwapChainAsync() => Invoke(AcquireSwapChain);
    private SwapChainLease? AcquireSwapChain()
    {
        if (_options.Headless) return null;
        var address = _client.Integer("display-swapchain");
        if (address == 0) return null;
        return new SwapChainLease((nint)address);
    }
    // One worker dispatch per UI tick. Still query the current swapchain every time:
    // media/VO changes can replace it even when the panel's size has not changed.
    public Task<PlaybackPoll> PollAsync(int width, int height) => PollAsync(width, height, observed: true);
    // Same dispatch/lease path with fresh metadata for local paired diagnostics only.
    internal Task<PlaybackPoll> PollAsync(int width, int height, bool observed) => Invoke(() =>
    {
        Resize(width, height);
        return new PlaybackPoll(Read(observed), AcquireSwapChain());
    });
    // Read-only test observation; no Pump/direct read/reconciliation is performed here.
    internal Task<string?> ReadCachedMetadataForDiagnosticsAsync(string name) => Invoke(() => _metadata.Peek(name));
    // Explicit diagnostic scope. Ordinary playback does not retain an event timeline.
    // Caller must settle the previous operation; native seek/restart events have no request ID.
    public Task<PlaybackTimingCapture> BeginTimingCaptureAsync(int capacity = 256) => Invoke(() =>
    {
        if (_timing is not null) throw new InvalidOperationException("A timing capture is already active.");
        Pump();
        if (_loading || Flag("seeking")) throw new InvalidOperationException("Settle playback before timing.");
        var capture = new PlaybackTimingCapture(capacity);
        Volatile.Write(ref _timing, capture);
        return capture;
    });
    public Task<PlaybackTimingSnapshot> EndTimingCaptureAsync(PlaybackTimingCapture capture, bool cancelled = false) => Invoke(() =>
    {
        if (!ReferenceEquals(_timing, capture)) throw new InvalidOperationException("Timing capture does not belong to this active scope.");
        Pump();
        Volatile.Write(ref _timing, null);
        capture.Close(cancelled ? "cancelled" : "stopped");
        return capture.Snapshot();
    });

    // Reject work and settle pending tasks before the host drains polls and detaches video.
    // Native destruction still waits for DisposeAsync, after the host releases its leases.
    public void BeginClose()
    {
        lock (_gate)
        {
            if (_stopping) return;
            _stopping = true;
            Volatile.Write(ref _lifecycle, (int)PlaybackState.Closing);
        }
    }
    // Caller must detach its panel and release acquired swapchain leases first.
    public ValueTask DisposeAsync()
    {
        BeginClose();
        lock (_gate) { if (!_nativeReleased) _releaseNative.Set(); }
        return new ValueTask(_worker);
    }
}

// The UI owns the lease until attachment completes, including early-return/error paths.
public sealed record PlaybackPoll(PlaybackSnapshot Snapshot, SwapChainLease? SwapChain) : IDisposable
{
    public void Dispose() => SwapChain?.Dispose();
}

// Hold a COM reference while the pointer is travelling to the UI thread.
public sealed class SwapChainLease : IDisposable
{
    private nint _pointer;
    public nint Pointer => _pointer;
    internal SwapChainLease(nint pointer) { Marshal.AddRef(pointer); _pointer = pointer; }
    public void Dispose()
    {
        var pointer = Interlocked.Exchange(ref _pointer, 0);
        if (pointer != 0) Marshal.Release(pointer);
    }
}

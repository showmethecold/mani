namespace Mani.Playback;

// IINA 1.5.1 FFmpegController retains one file's decoding context while sampling.
// Copyright (c) 2016-2026 lhc and contributors, GPL-3.0. Windows uses an independent
// libmpv client; serialized ownership and idle release never touch the main player.
public sealed class ThumbnailFrameCapture(PlaybackOptions options, TimeSpan? idleTimeout = null) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeSpan _idleTimeout = idleTimeout ?? TimeSpan.FromSeconds(30);
    private PlaybackSession? _session;
    private (string Path, long Length, long Modified)? _media;
    private CancellationTokenSource? _idle;
    private bool _disposed;
    private int _sessionsCreated;
    private readonly Func<Task<PlaybackSession>> _createSession = () => PlaybackSession.CreateAsync(options with
        { Headless = true, KeepOpen = true, LoadAutoProfiles = false, LogPath = "" });
    internal ThumbnailFrameCapture(PlaybackOptions options, Func<Task<PlaybackSession>> createSession, TimeSpan? idleTimeout = null)
        : this(options, idleTimeout) => _createSession = createSession;
    public int SessionsCreated => Volatile.Read(ref _sessionsCreated);

    public async Task<string> CaptureAsync(string path, double seconds, string destination, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _idle?.Cancel();
            _idle?.Dispose();
            _idle = null;
            var file = new FileInfo(path);
            var media = (Path.GetFullPath(path), file.Length, file.LastWriteTimeUtc.Ticks);
            if (_session is null)
            {
                _session = await _createSession().ConfigureAwait(false);
                Interlocked.Increment(ref _sessionsCreated);
                await FrameCapture.ConfigureAsync(_session, 240).ConfigureAwait(false);
            }
            bool reload = _media != media;
            if (reload) _media = null;
            var result = await FrameCapture.CaptureAsync(_session, path, seconds, destination, cancellationToken, reload,
                loaded: () => _media = media).ConfigureAwait(false);
            ArmIdleRelease();
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && _media is not null && _session is { IsAvailable: true })
        {
            ArmIdleRelease();
            throw;
        }
        catch
        {
            await ReleaseAsync().ConfigureAwait(false);
            throw;
        }
        finally { _gate.Release(); }
    }

    private void ArmIdleRelease()
    {
        _idle = new CancellationTokenSource();
        _ = ReleaseWhenIdleAsync(_idle.Token);
    }

    private async Task ReleaseWhenIdleAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(_idleTimeout, token).ConfigureAwait(false);
            await _gate.WaitAsync(token).ConfigureAwait(false);
            try { if (!token.IsCancellationRequested) await ReleaseAsync().ConfigureAwait(false); }
            finally { _gate.Release(); }
        }
        catch (OperationCanceledException) { }
    }

    private async Task ReleaseAsync()
    {
        var session = _session;
        _session = null; _media = null;
        if (session is not null) await session.DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _disposed = true;
            _idle?.Cancel(); _idle?.Dispose(); _idle = null;
            await ReleaseAsync().ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }
}

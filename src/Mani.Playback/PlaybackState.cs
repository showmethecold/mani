// Lifecycle and ticket rules adapted from IINA 1.5.1/build 180 PlaybackInfo/PlayerCore.
// Copyright (c) 2016 lhc. GPL-3.0; see licenses/IINA-GPL-3.0.txt.
namespace Mani.Playback;

public enum PlaybackState { Idle, Loading, Buffering, Playing, Paused, Seeking, Ended, Stopped, Failed, Closing, Closed }
public enum PlaybackLoop { Off, File, Playlist }
public sealed record MediaReady(long RequestId, long PlaylistEntryId);

internal sealed class MediaLoad(long id, string source, bool paused, CancellationToken token)
{
    internal long Id { get; } = id;
    internal string Source { get; } = source;
    internal bool Paused { get; } = paused;
    internal CancellationToken Token { get; } = token;
    internal long? PlaylistEntry { get; init; }
    internal TaskCompletionSource Accepted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource<MediaReady> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal void Cancel()
    {
        Accepted.TrySetCanceled();
        Ready.TrySetCanceled();
    }
    internal void Fail(Exception error)
    {
        Accepted.TrySetException(error);
        Ready.TrySetException(error);
        // Either public contract may be used without awaiting the other one.
        _ = Accepted.Task.Exception;
        _ = Ready.Task.Exception;
    }
}

using Mani.Playback;

namespace Mani_App;

// Adapted from IINA 1.5.1 PlayerCore.syncUI's time / playButton / volume split.
// IINA: Copyright (c) 2016 lhc, GPL v3 (licenses/IINA-GPL-3.0.txt).
// MPVController.handleEvent keeps LOG_MESSAGE diagnostic-only; END_FILE drives failure state.
// Windows projection/cache scope: docs/m1/ui-refresh-optimization.md and independent-review-followup.md.
[Flags]
internal enum PlaybackUiChanges
{
    None = 0, Empty = 1, Title = 2, Playback = 4, Seeking = 8, Volume = 16, Status = 32,
    All = Empty | Title | Playback | Seeking | Volume | Status
}

internal readonly record struct PlaybackUiState(
    bool Empty, string Title, bool CanPlay, string PlayLabel, bool CanSeek, double Volume, string Status)
{
    public static PlaybackUiState From(PlaybackSnapshot state, string notice) => new(
        string.IsNullOrEmpty(state.Path), string.IsNullOrEmpty(state.Title) ? "No file open" : state.Title,
        !state.Idle && !state.Loading && state.State is not (PlaybackState.Failed or PlaybackState.Stopped or PlaybackState.Closing or PlaybackState.Closed), state.Paused || state.Ended ? "Play" : "Pause",
        state.Duration > 0 && !state.Loading && !state.Idle, state.Volume,
        notice.Length > 0 ? notice : !string.IsNullOrWhiteSpace(state.LoadFailure) ? state.LoadFailure :
        state.Loading ? "Loading..." : state.PausedForCache ? "Buffering..." : state.Seeking ? "Seeking..." :
        state.State == PlaybackState.Stopped ? "Stopped" :
        state.Ended ? "Playback ended" : state.Idle ? "Open a local video to start playback" :
        state.Paused ? "Paused" : "Playing");
}

internal sealed class PlaybackUiCache
{
    private PlaybackUiState? _previous;
    private bool _volumeDirty;
    public void Invalidate() { _previous = null; _volumeDirty = true; }
    public void InvalidateVolume() => _volumeDirty = true;

    // UI-thread only. Invalidate if applying the returned changes fails or controls are changed directly.
    public (PlaybackUiState State, PlaybackUiChanges Changes) Update(
        PlaybackSnapshot snapshot, string notice, bool volumeReady)
    {
        var state = PlaybackUiState.From(snapshot, notice);
        var changes = PlaybackUiChanges.All;
        if (_previous is { } previous)
        {
            changes = PlaybackUiChanges.None;
            if (previous.Empty != state.Empty) changes |= PlaybackUiChanges.Empty;
            if (previous.Title != state.Title) changes |= PlaybackUiChanges.Title;
            if (previous.CanPlay != state.CanPlay || previous.PlayLabel != state.PlayLabel) changes |= PlaybackUiChanges.Playback;
            if (previous.CanSeek != state.CanSeek) changes |= PlaybackUiChanges.Seeking;
            if (previous.Volume != state.Volume) changes |= PlaybackUiChanges.Volume;
            if (previous.Status != state.Status) changes |= PlaybackUiChanges.Status;
        }
        if (!volumeReady)
        {
            _volumeDirty = true;
            changes &= ~PlaybackUiChanges.Volume;
        }
        else
        {
            if (_volumeDirty) changes |= PlaybackUiChanges.Volume;
            _volumeDirty = false;
        }
        _previous = state;
        return (state, changes);
    }
}

using Mani.Playback;

namespace Mani_App.Diagnostics;

// Follow IINA's separation of LOG_MESSAGE diagnostics from playback lifecycle events.
// A playback-restart plus panel attachment is not proof of a presented DWM frame.
internal static class UnattendedPlaybackReadiness
{
    public static bool IsReady(PlaybackSnapshot state, string requestedPath, bool panelAttached, bool playbackRestarted, bool expectedPaused = true)
    {
        if (state.LoadFailure.Length > 0) throw new IOException(state.LoadFailure);
        return !state.Idle && !state.Loading && !state.Ended && state.Paused == expectedPaused &&
            state.Path == requestedPath && state.VideoFormat.Length > 0 && state.Duration > 0 &&
            state.VideoOutput == "gpu-next" && panelAttached && playbackRestarted;
    }
}

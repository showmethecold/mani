// IINA 1.5.1/build 180 PlayerWindowController, PlayerCore.pauseChanged and
// MainWindowController fullscreen rules. Copyright (c) 2016-2026 lhc and
// contributors. GPL-3.0; see licenses/IINA-GPL-3.0.txt.
namespace Mani_App;

internal enum FloatingPlayback { Empty, Loading, Playing, Paused }

internal sealed class WindowFloatingState
{
    private (long Request, long Entry, bool Paused)? _observed;
    public bool Requested { get; private set; }
    public bool Applied { get; private set; }
    public bool Automatic { get; private set; }
    public bool Fullscreen { get; private set; }
    public FloatingPlayback Playback { get; private set; }
    public bool CanSetManual => !Fullscreen && Playback is FloatingPlayback.Playing or FloatingPlayback.Paused;

    public void SetManual(bool value)
    {
        if (!CanSetManual) return;
        Requested = Applied = value;
    }
    public void SetAutomatic(bool value)
    {
        if (Automatic == value) return;
        Automatic = value;
        // IINA applies preference changes immediately only while playing.
        if (Playback == FloatingPlayback.Playing) SetManual(value);
    }
    public void Observe(FloatingPlayback playback, long request = 0, long entry = 0)
    {
        Playback = playback;
        if (playback == FloatingPlayback.Empty)
        {
            _observed = null;
            Requested = Applied = false;
            return;
        }
        if (playback == FloatingPlayback.Loading) return;
        var next = (request, entry, playback == FloatingPlayback.Paused);
        if (_observed == next) return;
        _observed = next;
        if (Automatic) SetManual(playback == FloatingPlayback.Playing);
    }
    public void EnterFullscreen()
    {
        Fullscreen = true;
        Applied = false; // updateOnTopStatus: false -- retain the requested state.
    }
    public void ExitFullscreen()
    {
        if (!Fullscreen) return;
        Fullscreen = false;
        // Preserve IINA's playing-only restoration, including the paused case.
        Applied = Playback == FloatingPlayback.Playing && Requested;
    }
}

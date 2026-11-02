// IINA InitialWindowController/open actions and PlayerWindowController's loaded-title guard.
// Copyright (c) 2016-2026 lhc and contributors. GPL-3.0; licenses/IINA-GPL-3.0.txt.
using Mani.Playback;

namespace Mani_App;

internal enum PlayerSurface { Welcome, Loading, Failed, Stopped, Media }
internal readonly record struct PlayerSurfaceState(PlayerSurface Surface, string Title, double Position, double Duration)
{
    public bool HasMedia => Surface == PlayerSurface.Media;
    public static PlayerSurfaceState From(PlaybackSnapshot? snapshot, bool opening = false)
    {
        var surface = opening || snapshot is { Loading: true } ? PlayerSurface.Loading : snapshot switch
        {
            { State: PlaybackState.Failed } => PlayerSurface.Failed,
            { State: PlaybackState.Stopped } => PlayerSurface.Stopped,
            null or { Idle: true } or { Path.Length: 0 } => PlayerSurface.Welcome,
            _ => PlayerSurface.Media
        };
        bool media = surface == PlayerSurface.Media;
        return new(surface, media ? snapshot!.Title : "", media ? snapshot!.Position : 0, media ? snapshot!.Duration : 0);
    }
}

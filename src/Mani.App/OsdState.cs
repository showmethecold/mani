// Ported from IINA 1.5.1/build 180 OSDMessage, PlayerCore.sendOSD and
// MainWindowController.displayOSD/hideOSD. Windows observes existing snapshots.
// Copyright (c) 2016-2026 lhc and contributors. GPL-3.0; licenses/IINA-GPL-3.0.txt.
using Mani.Playback;
using System.Globalization;

namespace Mani_App;

internal enum OsdKind
{
    File, Pause, Resume, Seek, ShowTime, Volume, Mute, Unmute, Speed, Stop,
    VideoTrack, AudioTrack, SubtitleTrack, AudioDelay, SubDelay, SecondarySubDelay,
    SubHidden, SubVisible, SecondarySubHidden, SecondarySubVisible, SubPosition, SubScale,
    AbLoopA, AbLoopB, AbLoopClear, FileLoop, PlaylistLoop, LoopOff, Screenshot, Navigation
}
internal sealed record OsdMessage(OsdKind Kind, string Title = "", double Position = 0, double Duration = 0, double Value = 0)
{
    public bool HasTime => Kind is OsdKind.Pause or OsdKind.Resume or OsdKind.Seek or OsdKind.ShowTime or OsdKind.AbLoopA or OsdKind.AbLoopB;
    public double? Progress => Kind switch
    {
        OsdKind.Volume => Math.Clamp(double.IsFinite(Value) ? Value / 100 : 0, 0, 1),
        OsdKind.AudioDelay or OsdKind.SubDelay or OsdKind.SecondarySubDelay =>
            Math.Clamp(double.IsFinite(Value) ? (Value + 10) / 20 : .5, 0, 1),
        OsdKind.Seek or OsdKind.ShowTime when double.IsFinite(Duration) && Duration > 0 => Math.Clamp(double.IsFinite(Position) ? Position / Duration : 0, 0, 1),
        _ => null
    };
    public static OsdMessage From(OsdKind kind, PlaybackSnapshot state) =>
        new(kind, state.Title, state.Position, state.Duration, kind == OsdKind.Volume ? state.Volume : state.Speed);
    // IINA VideoTime.stringRepresentationWithPrecision(3), including carry.
    public static string PreciseTime(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0 || seconds >= long.MaxValue - 60.0) return "--:--:--";
        long whole = (long)seconds;
        string field = (seconds % 60).ToString("00.000", CultureInfo.InvariantCulture);
        if (field.StartsWith("60", StringComparison.Ordinal)) { whole += 60; field = "00.000"; }
        long hours = whole / 3600, minutes = whole % 3600 / 60;
        return (hours > 0 ? hours.ToString(CultureInfo.InvariantCulture) + ":" : "") +
            minutes.ToString("00", CultureInfo.InvariantCulture) + ":" + field;
    }
}

internal readonly record struct OsdUpdate(OsdMessage? Message, bool Clear = false);

// IINA PlayerCore.getLoopMode/syncAbLoop and MPVController loop feedback.
// mpv's explicit "no" keeps a valid loop point at time zero distinguishable.
internal static class LoopFeedback
{
    public static bool Enabled(string value) => value == "inf" ||
        (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long count) && count != 0);
    private static bool Point(string value) => double.TryParse(value, NumberStyles.Float,
        CultureInfo.InvariantCulture, out double seconds) && double.IsFinite(seconds) && seconds >= 0;
    public static OsdKind? Changed(PlaybackSnapshot before, PlaybackSnapshot after)
    {
        if (before.AbLoopA != after.AbLoopA || before.AbLoopB != after.AbLoopB)
            return Point(after.AbLoopA) && Point(after.AbLoopB) ? OsdKind.AbLoopB :
                Point(after.AbLoopA) || Point(after.AbLoopB) ? OsdKind.AbLoopA : OsdKind.AbLoopClear;
        if (before.FileLoop != after.FileLoop || before.PlaylistLoop != after.PlaylistLoop)
            return Enabled(after.FileLoop) ? OsdKind.FileLoop :
                Enabled(after.PlaylistLoop) || after.PlaylistLoop == "force" ? OsdKind.PlaylistLoop : OsdKind.LoopOff;
        return null;
    }
}

internal static class OsdPreferences
{
    public static bool Allows(OsdKind kind, PlayerSettings settings) => settings.EnableOsd && !(kind switch
    {
        OsdKind.File => settings.DisableOsdFileStart,
        OsdKind.Pause or OsdKind.Resume => settings.DisableOsdPauseResume,
        OsdKind.Seek => settings.DisableOsdSeek,
        OsdKind.Speed => settings.DisableOsdSpeed,
        _ => false
    });
    public static double SecondarySize(double size) => Math.Clamp(size * .5, 11, 25);
}

internal sealed class OsdPlaybackObserver
{
    private PlaybackSnapshot? _previous;
    private (long Request, long File)? _seek;
    private long _readyAfter;
    public void Reset() { _previous = null; _seek = null; }
    public void SeekCompleted(PlaybackSnapshot state) => _seek = (state.RequestId, state.FileLoadedCount);
    public bool CanShowFeedback(PlaybackSnapshot state, long now) =>
        _previous is { } previous && previous.RequestId == state.RequestId && previous.FileLoadedCount == state.FileLoadedCount &&
        PlayerSurfaceState.From(state).HasMedia && now >= _readyAfter;
    public OsdUpdate Observe(PlaybackSnapshot state, long now)
    {
        var previous = _previous;
        _previous = state;
        bool hadMedia = previous is not null && PlayerSurfaceState.From(previous).HasMedia;
        if (!PlayerSurfaceState.From(state).HasMedia)
        {
            _seek = null;
            return new(hadMedia && state.State == PlaybackState.Stopped ? new(OsdKind.Stop) : null, hadMedia);
        }
        if (!hadMedia || previous!.RequestId != state.RequestId || previous.FileLoadedCount != state.FileLoadedCount)
        {
            _seek = null;
            _readyAfter = now + 200; // IINA playbackRestarted's loading-noise guard.
            return new(OsdMessage.From(OsdKind.File, state), true);
        }
        if (now < _readyAfter) { _seek = null; return default; }
        bool sought = !state.Seeking && (_seek == (state.RequestId, state.FileLoadedCount) || previous!.Seeking);
        if (!state.Seeking) _seek = null;
        // Coalesced engine snapshots have no event order. Present one message;
        // explicit loop state wins over its accompanying seek, then seek,
        // pause and changed controls use the existing priority.
        OsdKind? kind = LoopFeedback.Changed(previous!, state) ?? (sought ? OsdKind.Seek :
            previous!.Muted != state.Muted ? (state.Muted ? OsdKind.Mute : OsdKind.Unmute) :
            previous.Volume != state.Volume ? OsdKind.Volume :
            previous.Speed != state.Speed ? OsdKind.Speed :
            previous.Paused != state.Paused ? (state.Paused ? OsdKind.Pause : OsdKind.Resume) : (OsdKind?)null);
        return new(kind is { } value ? OsdMessage.From(value, state) : null);
    }
}

internal sealed class OsdState
{
    internal const long HoldDuration = 1000, FadeDuration = 500;
    private long _deadline;
    public OsdMessage? Message { get; private set; }
    public double Opacity { get; private set; }
    public bool Visible => Message is not null && Opacity > 0;
    public long NextTick(long now) => Math.Max(1, now < _deadline ? _deadline - now : 25);
    public void Show(OsdMessage message, long now, long hold = HoldDuration)
    { Message = message; Opacity = 1; _deadline = now + Math.Clamp(hold, 0, int.MaxValue); }
    public void Clear() { Message = null; Opacity = 0; }
    public void RefreshTime(PlaybackSnapshot snapshot)
    {
        if (Message is { HasTime: true }) Message = Message with { Position = snapshot.Position, Duration = snapshot.Duration };
    }
    public void Tick(long now, bool animate)
    {
        if (Message is null || now < _deadline) return;
        double progress = animate ? Math.Clamp((now - _deadline) / (double)FadeDuration, 0, 1) : 1;
        Opacity = 1 - progress * progress * (3 - 2 * progress);
        if (Opacity == 0) Clear();
    }
}

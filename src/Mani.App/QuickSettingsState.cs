// Port of IINA 1.5.1/build 180 SidebarTrackSelector, MPVTrack and SpeedView.
// Copyright (c) 2016-2026 lhc and contributors. GPL-3.0; licenses/IINA-GPL-3.0.txt.
using System.Globalization;
using Mani.Playback;

namespace Mani_App;

internal enum QuickPane { Layout, Video, Audio, Subtitles, Playlist, Chapters }
internal sealed record QuickTrack(long Id, string Label);

internal readonly record struct DelayMedia(long Request, long Entry, int Revision);

// Port of AudioDelayView/SubDelayView preview, quantization and release commit.
internal sealed class DelayEdit
{
    private DelayMedia _media;
    private double _original;
    private bool _audio;
    public bool Active { get; private set; }
    public double Value { get; private set; }
    public static double Position(double value) => Math.Clamp(value, -5, 5);
    public static string FormatSeconds(double value)
    {
        double rounded = Math.Round(value, 6, MidpointRounding.AwayFromZero);
        return (rounded == 0 ? 0 : rounded).ToString("0.######", CultureInfo.InvariantCulture) + "s";
    }
    public static double Snap(double value, bool audio)
    {
        if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
        double steps = audio ? 20 : 2;
        return Math.Round(Position(value) * steps, MidpointRounding.AwayFromZero) / steps;
    }
    public void Begin(DelayMedia media, double value, bool audio)
    {
        _media = media;
        _original = value;
        _audio = audio;
        Value = value;
        Active = true;
    }
    public double Preview(double value) => Value = Snap(value, _audio);
    public double? Finish(DelayMedia media, bool commit)
    {
        bool apply = Active && commit && media == _media && Value != _original;
        Active = false;
        return apply ? Value : null;
    }
}

internal static class QuickSettings
{
    public static bool ToggleOff(bool shown, QuickPane current, QuickPane requested) => shown && current == requested;

    public static string TrackType(string property) => property switch
    {
        "vid" => "video", "aid" => "audio", "sid" or "secondary-sid" => "sub",
        _ => throw new ArgumentOutOfRangeException(nameof(property))
    };

    // A cycle on an absent type can persist "no" into the next file despite
    // no visible change now. Existing tracks, including deselected ones, cycle
    // in mpv; only an actually missing type is a no-op at the UI boundary.
    public static bool CanCycleTrack(MediaInfo info, string property) =>
        info.Tracks.Any(track => track.Type == TrackType(property));

    public static QuickTrack[] Tracks(MediaInfo info, string property, string none, string noTitle, string defaultLabel)
    {
        string type = TrackType(property);
        return [new(0, none), .. info.Tracks.Where(t => t.Type == type).Select(t => new QuickTrack(t.Id, Label(t, noTitle, defaultLabel)))];
    }

    public static string Label(MediaTrack track, string noTitle, string defaultLabel)
    {
        List<string> details = [];
        if (!string.IsNullOrEmpty(track.Codec)) details.Add(track.Codec);
        if (track.Type == "video")
        {
            if (track.Width is { } w && track.Height is { } h) details.Add($"{w}×{h}");
            if (track.FramesPerSecond is { } fps) details.Add(fps.ToString("0.###", CultureInfo.InvariantCulture) + "fps");
        }
        if (track.Type == "audio")
        {
            if (track.ChannelCount is { } ch) details.Add($"{ch}ch");
            if (track.SampleRate is { } hz) details.Add((hz / 1000d).ToString("0.###", CultureInfo.InvariantCulture) + "kHz");
        }
        if (track.Default) details.Add(defaultLabel);
        string language = track.Language is { Length: > 0 } lang && lang != "und" ? $"[{lang}] " : "";
        return $"#{track.Id} {language}{track.Title ?? noTitle}" + (details.Count > 0 ? $" ({string.Join(", ", details)})" : "");
    }

    public static int SelectedIndex(QuickTrack[] tracks, string selected)
    {
        if (selected is "no" or "0") return 0;
        return long.TryParse(selected, CultureInfo.InvariantCulture, out long id) ? Array.FindIndex(tracks, t => t.Id == id) : -1;
    }

    // Explain mpv's refusal after reading back the result. A hidden subtitle
    // still owns its track; audio/video IDs belong to separate track groups.
    public static bool SubtitleConflict(MediaInfo actual, string property, long requested) => requested > 0 && property switch
    {
        "sid" => IsTrack(actual.SecondarySubtitleTrack, requested) && !IsTrack(actual.SubtitleTrack, requested),
        "secondary-sid" => IsTrack(actual.SubtitleTrack, requested) && !IsTrack(actual.SecondarySubtitleTrack, requested),
        _ => false
    };

    private static bool IsTrack(string selected, long id) =>
        long.TryParse(selected, CultureInfo.InvariantCulture, out long value) && value == id;

    public static double SpeedFromStep(double step) => Math.Round(.25 * Math.Pow(64, Math.Clamp(step, 0, 24) / 24), 2, MidpointRounding.AwayFromZero);
    public static double StepFromSpeed(double speed) => Math.Clamp(Math.Log(Math.Max(.01, speed) / .25) / Math.Log(64) * 24, 0, 24);
    public static double ConstrainSpeed(double speed)
    {
        if (!double.IsFinite(speed)) throw new ArgumentOutOfRangeException(nameof(speed));
        speed = Math.Max(.01, speed);
        // NumberFormatter.halfDown: a tie rounds toward zero, other values to nearest.
        if (speed > 1_000_000_000) return speed;
        decimal scaled = (decimal)speed * 1_000_000m;
        decimal floor = decimal.Floor(scaled);
        return (double)((scaled - floor > .5m ? floor + 1 : floor) / 1_000_000m);
    }

    public static bool Matches(MediaInfo info, PlaybackSnapshot snapshot) =>
        info.RequestId == snapshot.RequestId && info.PlaylistEntryId == snapshot.PlaylistEntryId &&
        info.Path == snapshot.Path && !snapshot.Loading && !snapshot.Idle;
}

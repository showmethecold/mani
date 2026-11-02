// IINA 1.5.1/build 180 OSDMessage/MPVTrack and property-change feedback port.
// Copyright (c) 2016-2026 lhc and contributors. GPL-3.0; licenses/IINA-GPL-3.0.txt.
using System.Globalization;
using Mani.Playback;

namespace Mani_App;

// The existing core exposes accepted reads, not individual UI property events.
// Retain only the last operation and compare its actual before/after values.
internal sealed record QuickOsdFeedback(long Request, long Entry, string Path, string Property, string Before)
{
    public static QuickOsdFeedback? Begin(string property, MediaInfo info, IReadOnlyDictionary<string, string> values)
    {
        string? before = Selected(info, property) ?? (property is "audio-delay" or "sub-delay" or "secondary-sub-delay" or
            "sub-visibility" or "secondary-sub-visibility" ? values.GetValueOrDefault(property) : null);
        return before is null ? null : new(info.RequestId, info.PlaylistEntryId, info.Path, property, before);
    }

    public OsdMessage? Complete(MediaInfo actual, IReadOnlyDictionary<string, string> values,
        string none, string noTitle, string defaultLabel)
    {
        if (actual.RequestId != Request || actual.PlaylistEntryId != Entry || actual.Path != Path) return null;
        if (Selected(actual, Property) is { } selected)
        {
            if (!TrackId(Before, out var before) || !TrackId(selected, out var id) || before == id) return null;
            OsdKind kind = Property switch { "vid" => OsdKind.VideoTrack, "aid" => OsdKind.AudioTrack, _ => OsdKind.SubtitleTrack };
            if (id == 0) return new(kind, "#0 " + none);
            var track = actual.Tracks.FirstOrDefault(t => t.Id == id && t.Type == QuickSettings.TrackType(Property));
            return track is null ? null : new(kind, QuickSettings.Label(track, noTitle, defaultLabel));
        }
        if (!values.TryGetValue(Property, out var value)) return null;
        if (Property is "sub-visibility" or "secondary-sub-visibility")
        {
            if (Before is not ("yes" or "no") || value is not ("yes" or "no") || value == Before) return null;
            return new(Property == "sub-visibility" ?
                (value == "yes" ? OsdKind.SubVisible : OsdKind.SubHidden) :
                (value == "yes" ? OsdKind.SecondarySubVisible : OsdKind.SecondarySubHidden));
        }
        if (!Number(Before, out var previous) || !Number(value, out var number) || previous == number) return null;
        OsdKind? delay = Property switch
        {
            "audio-delay" => OsdKind.AudioDelay, "sub-delay" => OsdKind.SubDelay,
            "secondary-sub-delay" => OsdKind.SecondarySubDelay,
            "sub-pos" => OsdKind.SubPosition, "sub-scale" => OsdKind.SubScale, _ => null
        };
        return delay is { } type ? new(type, Value: number) : null;
    }

    public static string FormatDelay(double value) => Math.Abs(value).ToString("#,0.######", CultureInfo.InvariantCulture);
    private static bool Number(string value, out double number) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number) && double.IsFinite(number);
    private static bool TrackId(string value, out long id)
    {
        if (value == "no") { id = 0; return true; }
        return long.TryParse(value, CultureInfo.InvariantCulture, out id) && id >= 0;
    }
    private static string? Selected(MediaInfo info, string property) => property switch
    {
        "vid" => info.VideoTrack, "aid" => info.AudioTrack, "sid" => info.SubtitleTrack,
        "secondary-sid" => info.SecondarySubtitleTrack, _ => null
    };
}

// IINA PlaybackInfo.isAudio, PlayerCore music mode/metadata and MiniPlayer geometry.
// Copyright (c) 2016-2026 lhc and contributors. GPL-3.0; licenses/IINA-GPL-3.0.txt.
using System.Text.Json;

namespace Mani_App;

internal enum MusicMediaKind { Unknown, Audio, Video }
internal sealed class MusicModeState
{
    public bool Active { get; private set; }
    public bool ManualOverride { get; private set; }
    public void Switch(bool active, bool automatically)
    {
        if (Active == active) return;
        if (!automatically) ManualOverride = !ManualOverride;
        Active = active;
    }
    public bool? TrackListChanged(MusicMediaKind kind, bool enabled, bool fullscreen)
    {
        if (!enabled || ManualOverride) return null;
        if (kind == MusicMediaKind.Audio && !Active && !fullscreen) return true;
        if (kind == MusicMediaKind.Video && Active) return false;
        return null;
    }
}

internal sealed record MusicMediaInfo(MusicMediaKind Kind, string Title, string ArtistAlbum,
    VideoSize DisplaySize, bool DefaultArtwork, string Tracks)
{
    public static MusicMediaInfo Read(string path, string title, IReadOnlyDictionary<string, string> properties, string? chapterTitle = null)
    {
        string Get(string key) => properties.GetValueOrDefault(key) ?? "";
        bool Selected(string key) => int.TryParse(Get(key), out int id) && id > 0;
        bool network = Uri.TryCreate(path, UriKind.Absolute, out var uri) && !uri.IsFile;
        bool audio = false, realVideo = false, artwork = false;
        VideoSize? artworkSize = null;
        string tracks = Get("track-list");
        try
        {
            using var document = JsonDocument.Parse(tracks.Length == 0 ? "[]" : tracks);
            foreach (var track in document.RootElement.EnumerateArray())
            {
                string type = track.TryGetProperty("type", out var value) ? value.GetString() ?? "" : "";
                if (type == "audio") audio = true;
                if (type != "video") continue;
                bool album = track.TryGetProperty("albumart", out var art) && art.ValueKind == JsonValueKind.True;
                realVideo |= !album;
                if (!album || artwork) continue;
                artwork = true;
                if (track.TryGetProperty("demux-w", out var w) && w.TryGetInt32(out int width) && width > 0 &&
                    track.TryGetProperty("demux-h", out var h) && h.TryGetInt32(out int height) && height > 0)
                    artworkSize = new(width, height);
            }
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        { audio = realVideo = artwork = false; }
        var kind = network || realVideo ? MusicMediaKind.Video : audio || artwork ? MusicMediaKind.Audio : MusicMediaKind.Unknown;
        bool chapters = int.TryParse(Get("chapters"), out int count) && count > 0;
        string artist = chapters && Get("chapter-metadata/by-key/performer").Length > 0 ? Get("chapter-metadata/by-key/performer") : Get("metadata/by-key/artist");
        string albumName = Get("metadata/by-key/album");
        string detail = artist.Length > 0 && albumName.Length > 0 ? artist + " - " + albumName : artist + albumName;
        bool subtitles = Get("sub-visibility") == "yes" && Selected("sid") || Get("secondary-sub-visibility") == "yes" && Selected("secondary-sid");
        return new(kind, chapters && chapterTitle is { Length: > 0 } ? chapterTitle : title, detail,
            kind == MusicMediaKind.Audio ? artworkSize ?? new(1, 1) : VideoSize.FromProperties(properties) ?? new(16, 9),
            kind == MusicMediaKind.Audio && !artwork && !subtitles, tracks);
    }
}

internal static class MusicModeLayout
{
    public const double Width = 300, BarHeight = 72;
    // Windows text scaling has no AppKit equivalent. Keep IINA's default 40/32
    // rows, reserving room for both metadata lines when system text is enlarged.
    public static (double Header, double Timeline) ControlRows(double textScale)
    {
        double scale = double.IsFinite(textScale) ? Math.Clamp(textScale, 1, 4) : 1;
        return (Math.Max(40, Math.Ceiling(32 * scale)), Math.Max(32, Math.Ceiling(16 * scale)));
    }
    public static WindowFrame Size(WindowFrame frame, VideoSize video, bool artwork, WindowFrame work, VideoSize chrome, double scale, int edge = 2, double barHeight = BarHeight)
    {
        double bar = barHeight * scale;
        double wanted = frame.Width - chrome.Width;
        if (artwork && edge is 3 or 6) wanted = (frame.Height - chrome.Height - bar) * video.Aspect;
        double maximum = Math.Max(1, work.Width - chrome.Width);
        // Keep the controls usable for unusually tall artwork; the renderer can letterbox it.
        if (artwork) maximum = Math.Min(maximum, Math.Max(Width * scale, Math.Max(1, work.Height - chrome.Height - bar) * video.Aspect));
        double width = Math.Clamp(wanted, Math.Min(Width * scale, maximum), maximum);
        double height = Math.Min(work.Height, (artwork ? width / video.Aspect : 0) + bar + chrome.Height);
        width += chrome.Width;
        double x = edge is 1 or 4 or 7 ? frame.Right - width : frame.X;
        double y = edge is 3 or 4 or 5 ? frame.Bottom - height : frame.Y;
        return WindowSizing.Constrain(new(x, y, width, height), work);
    }
    public static WindowFrame Initial(WindowFrame main, VideoSize video, bool artwork, WindowFrame work, VideoSize chrome, double scale, double barHeight = BarHeight)
    {
        var frame = Size(new(main.X, main.Y, Width * scale + chrome.Width, main.Height), video, artwork, work, chrome, scale, barHeight: barHeight);
        return WindowSizing.Constrain(frame with { X = main.X + (main.Width - frame.Width) / 2 }, work);
    }
}

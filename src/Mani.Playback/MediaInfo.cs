// Model fields/title fallback adapted from IINA 1.5.1/build 180 MPVTrack and MPVChapter.
// Copyright (c) 2016 lhc. GPL-3.0; see licenses/IINA-GPL-3.0.txt.
namespace Mani.Playback;

public sealed record MediaTrack(long Id, string Type, string? Title, string? Language,
    bool Default, bool Forced, bool Selected, bool External, bool Image, bool AlbumArt,
    long? SourceId, string? ExternalFilename, string? Codec, string? Decoder,
    long? Width, long? Height, double? FramesPerSecond, long? ChannelCount,
    string? Channels, long? SampleRate, IReadOnlyDictionary<string, object?> Properties)
{
    internal static MediaTrack? Parse(IReadOnlyDictionary<string, object?> values)
    {
        if (values.GetValueOrDefault("id") is not long id || values.GetValueOrDefault("type") is not string type ||
            type is not ("audio" or "video" or "sub")) return null;
        string? Text(string key) => values.GetValueOrDefault(key) as string;
        long? Integer(string key) => values.GetValueOrDefault(key) as long?;
        bool Flag(string key) => values.GetValueOrDefault(key) is true;
        string? title = Text("title");
        if (string.IsNullOrWhiteSpace(title) && values.GetValueOrDefault("metadata") is IReadOnlyDictionary<string, object?> metadata)
            title = metadata.GetValueOrDefault("name") as string;
        return new(id, type, title, Text("lang"), Flag("default"), Flag("forced"), Flag("selected"),
            Flag("external"), Flag("image"), Flag("albumart"), Integer("src-id"), Text("external-filename"),
            Text("codec"), Text("decoder-desc"), Integer("demux-w"), Integer("demux-h"),
            values.GetValueOrDefault("demux-fps") as double?, Integer("demux-channel-count"),
            Text("demux-channels"), Integer("demux-samplerate"), values);
    }
}
public sealed record MediaChapter(int Index, string Title, double StartTime)
{
    public bool HasTitle { get; init; } = true;
}
public sealed record MediaEdition(int Index, string Title, bool Default);
public sealed record MediaInfo(long RequestId, long PlaylistEntryId, string Path, string Title, double Duration,
    IReadOnlyList<MediaTrack> Tracks, IReadOnlyList<MediaChapter> Chapters,
    long Chapter, string VideoTrack, string AudioTrack, string SubtitleTrack, string SecondarySubtitleTrack,
    IReadOnlyDictionary<string, object?> Metadata)
{
    public IReadOnlyList<MediaEdition> Editions { get; init; } = [];
    public long Edition { get; init; }
}

// A normal transition can occur between a UI snapshot and the queued metadata read.
public sealed class MediaUnavailableException() : InvalidOperationException("No loaded media information is available.");

public sealed partial class PlaybackSession
{
    public Task<MediaInfo> GetMediaInfoAsync() => Invoke(ReadMediaInfo);
    public Task<MediaInfo> GetMediaInfoAsync(long request, long entry) => Invoke(() => { RequireMedia(request, entry); return ReadMediaInfo(); });
    private MediaInfo ReadMediaInfo()
    {
        var state = Read();
        if (state.Loading || state.Path.Length == 0) throw new MediaUnavailableException();
        var tracks = (_client.Node("track-list") as IReadOnlyList<object?> ?? [])
            .OfType<IReadOnlyDictionary<string, object?>>().Select(MediaTrack.Parse).OfType<MediaTrack>().ToArray();
        var chapters = (_client.Node("chapter-list") as IReadOnlyList<object?> ?? [])
            .OfType<IReadOnlyDictionary<string, object?>>().Select((chapter, index) => new MediaChapter(index,
                chapter.GetValueOrDefault("title") as string ?? $"Chapter {index}",
                chapter.GetValueOrDefault("time") is double time ? time : chapter.GetValueOrDefault("time") is long integer ? integer : 0)
                { HasTitle = chapter.GetValueOrDefault("title") is string { Length: > 0 } }).ToArray();
        var metadata = _client.Node("metadata") as IReadOnlyDictionary<string, object?> ??
            System.Collections.ObjectModel.ReadOnlyDictionary<string, object?>.Empty;
        var result = new MediaInfo(state.RequestId, state.PlaylistEntryId, state.Path, state.Title, state.Duration,
            Array.AsReadOnly(tracks), Array.AsReadOnly(chapters), _client.Integer("chapter"), Get("vid"), Get("aid"),
            Get("sid"), Get("secondary-sid"), metadata)
            {
                Edition = _client.Integer("edition"),
                Editions = Array.AsReadOnly((_client.Node("edition-list") as IReadOnlyList<object?> ?? [])
                    .OfType<IReadOnlyDictionary<string, object?>>()
                    .Select((e, index) => new MediaEdition(index, e.GetValueOrDefault("title") as string ?? "", e.GetValueOrDefault("default") is true)).ToArray())
            };
        // These queries can span an engine transition; never return a mixed-media model.
        var position = _client.Integer("playlist-playing-pos");
        if (_client.Integer($"playlist/{position}/id") != state.PlaylistEntryId)
            throw new OperationCanceledException("Media changed while reading its information.");
        return result;
    }
}

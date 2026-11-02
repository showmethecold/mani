using Mani.Playback;

namespace Mani.UnitTests;

internal static class PlaybackFixtures
{
    // Synthetic state: no archived reports, machine-specific paths, or native session.
    public static PlaybackSnapshot Playing() => new(
        Version: "test", Path: "sample.mkv", Title: "Sample", Loading: false,
        Idle: false, Paused: false, Ended: false, Position: 12, Duration: 120,
        Volume: 50, VideoOutput: "gpu-next", HardwareDecoder: "d3d11va",
        VideoFormat: "h264", ColorPrimaries: "bt.709", Transfer: "bt.1886",
        DroppedFrames: "0", Error: "", AudioOutput: "wasapi", AudioDevice: "auto",
        AudioBuffer: "0.2", AvSync: "0", PausedForCache: false);

    public static PlaybackTimingSnapshot Timing(bool seek = false)
    {
        var entries = new List<PlaybackTimingEntry>
        {
            new(1, 100, "capture-start"),
            new(2, 110, "requested", 1, seek ? "seek" : "open", "sample.mkv"),
            new(3, 120, "dispatched", 1),
            new(4, 130, "accepted", 1)
        };
        if (seek)
            entries.Add(new(5, 140, "event", EventId: 20));
        else
        {
            entries.Add(new(5, 140, "event", EventId: 6, PlaylistEntryId: 7));
            entries.Add(new(6, 150, "event", EventId: 8));
        }
        entries.Add(new(entries.Count + 1, 180, "event", EventId: 21));
        entries.Add(new(entries.Count + 1, 190, "capture-stop"));
        return new(Guid.Empty, 1000, 32, true, "stopped", 0, entries.ToArray());
    }
}

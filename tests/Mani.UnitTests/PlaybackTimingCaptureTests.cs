using Mani.Playback;

namespace Mani.UnitTests;

public sealed class PlaybackTimingCaptureTests
{
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(4097)]
    public void InvalidCapacitiesAreRejected(int capacity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PlaybackTimingCapture(capacity));
    }

    [Fact]
    public void OverflowRemainsBoundedAndIsReported()
    {
        var capture = new PlaybackTimingCapture(8);
        for (int i = 0; i < 20; i++) capture.Add("event", eventId: 17);
        capture.Close("stopped");
        var snapshot = capture.Snapshot();
        Assert.Equal(8, snapshot.Entries.Length);
        Assert.True(snapshot.DroppedEntries > 0);
        Assert.True(snapshot.Closed);
        Assert.Equal("blocked", PlaybackTimingAnalysis.Analyze(snapshot).Status);
    }

    [Fact]
    public void SnapshotsDoNotExposeMutableCaptureStorage()
    {
        var capture = new PlaybackTimingCapture(32);
        var external = capture.Snapshot();
        external.Entries[0] = external.Entries[0] with { Kind = "corrupted" };
        var actual = capture.Snapshot();
        Assert.Equal("capture-start", actual.Entries[0].Kind);
        Assert.Equal(external.CaptureId, actual.CaptureId);
        Assert.NotSame(external.Entries, actual.Entries);
    }

    [Fact]
    public void ClosingIsTerminalAndPreservesTheFirstReason()
    {
        var capture = new PlaybackTimingCapture(32);
        capture.Close("cancelled");
        capture.Close("stopped");
        capture.Add("ignored-after-close");
        var snapshot = capture.Snapshot();
        Assert.Equal("cancelled", snapshot.CloseReason);
        Assert.Equal(new[] { "capture-start", "capture-stop" }, snapshot.Entries.Select(e => e.Kind));
    }

    [Fact]
    public void RequestsHaveDistinctOperationIdentities()
    {
        var capture = new PlaybackTimingCapture(32);
        Assert.Equal(1, capture.Request("open", "a.mkv"));
        Assert.Equal(2, capture.Request("seek", "12"));
        Assert.Equal(new long?[] { 1, 2 }, capture.Snapshot().Entries.Skip(1).Select(e => e.OperationId));
    }

    [Fact]
    public void ConcurrentWritersPreserveACompleteOrderedSequence()
    {
        var capture = new PlaybackTimingCapture(512);
        Parallel.For(0, 256, i => capture.Add("event", detail: i.ToString()));
        capture.Close("stopped");
        var snapshot = capture.Snapshot();
        Assert.Equal(258, snapshot.Entries.Length);
        Assert.Equal(0, snapshot.DroppedEntries);
        Assert.Equal(Enumerable.Range(1, 258).Select(i => (long)i), snapshot.Entries.Select(e => e.Sequence));
        Assert.All(snapshot.Entries.Zip(snapshot.Entries.Skip(1)), pair => Assert.True(pair.Second.Qpc >= pair.First.Qpc));
    }
}

using Mani.Playback;

namespace Mani.UnitTests;

public sealed class PlaybackTimingAnalysisTests
{
    [Fact]
    public void OpenTimingSeparatesQueueCommandAndReadinessIntervals()
    {
        var result = PlaybackTimingAnalysis.Analyze(PlaybackFixtures.Timing());
        Assert.Equal("measured", result.Status);
        Assert.Empty(result.Blockers);
        Assert.Equal(10, result.QueueMilliseconds);
        Assert.Equal(10, result.CommandMilliseconds);
        Assert.Equal(70, result.RequestToReadyMilliseconds);
        Assert.Equal(60, result.DispatchToReadyMilliseconds);
        Assert.Null(result.SeekEventToReadyMilliseconds);
        Assert.False(result.PerformanceAccepted);
        Assert.Contains("not a presentation timestamp", result.Endpoint);
    }

    [Fact]
    public void SeekTimingPreservesTheSeparateSeekToRestartInterval()
    {
        var result = PlaybackTimingAnalysis.Analyze(PlaybackFixtures.Timing(seek: true));
        Assert.Equal("measured", result.Status);
        Assert.Equal(40, result.SeekEventToReadyMilliseconds);
        Assert.False(result.PerformanceAccepted);
    }

    [Theory]
    [InlineData("cancelled")]
    [InlineData("session-closed")]
    [InlineData("active")]
    [InlineData("overflow")]
    [InlineData("invalid-clock")]
    [InlineData("missing-timeline")]
    [InlineData("truncated-boundary")]
    [InlineData("missing-file-loaded")]
    [InlineData("missing-start-file")]
    [InlineData("missing-playlist-identity")]
    [InlineData("load-error")]
    [InlineData("command-error")]
    [InlineData("mixed-operation-identity")]
    [InlineData("overlapping-operations")]
    [InlineData("nonmonotonic-clock")]
    [InlineData("stale-event")]
    [InlineData("wrong-sequence")]
    [InlineData("unsupported-operation")]
    public void InvalidOpenEvidenceCannotProduceLatencyValues(string mutation)
    {
        var trace = PlaybackFixtures.Timing();
        var invalid = mutation switch
        {
            "cancelled" => trace with { CloseReason = "cancelled" },
            "session-closed" => trace with { CloseReason = "session-closed" },
            "active" => trace with { Closed = false },
            "overflow" => trace with { DroppedEntries = 1 },
            "invalid-clock" => trace with { Frequency = 0 },
            "missing-timeline" => trace with { Entries = [] },
            "truncated-boundary" => trace with { Entries = trace.Entries[..^1] },
            "missing-file-loaded" => Change(trace, e => e.EventId == 8 ? e with { EventId = 17 } : e),
            "missing-start-file" => Change(trace, e => e.EventId == 6 ? e with { EventId = 17 } : e),
            "missing-playlist-identity" => Change(trace, e => e.EventId == 6 ? e with { PlaylistEntryId = null } : e),
            "load-error" => Change(trace, e => e.EventId == 8 ? e with { EventId = 7, Error = -1, EndReason = 4 } : e),
            "command-error" => Change(trace, e => e.Kind == "accepted" ? e with { Kind = "command-error" } : e),
            "mixed-operation-identity" => Change(trace, e => e.Kind == "accepted" ? e with { OperationId = 2 } : e),
            "overlapping-operations" => Change(trace, e => e.EventId == 8 ? e with { Kind = "requested", OperationId = 2, Operation = "seek" } : e),
            "nonmonotonic-clock" => Change(trace, e => e.Kind == "accepted" ? e with { Qpc = 1 } : e),
            "stale-event" => Change(trace, e => e.Kind == "dispatched" ? e with { Kind = "event", EventId = 21 } : e),
            "wrong-sequence" => Change(trace, e => e.Sequence == 4 ? e with { Sequence = 1 } : e),
            "unsupported-operation" => Change(trace, e => e.Kind == "requested" ? e with { Operation = "stop" } : e),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation))
        };
        AssertBlocked(invalid);
    }

    [Theory]
    [InlineData("missing-seek")]
    [InlineData("media-changed")]
    [InlineData("seek-after-restart")]
    public void InvalidSeekEvidenceCannotProduceLatencyValues(string mutation)
    {
        var trace = PlaybackFixtures.Timing(seek: true);
        var invalid = mutation switch
        {
            "missing-seek" => Change(trace, e => e.EventId == 20 ? e with { EventId = 17 } : e),
            "media-changed" => Change(trace, e => e.EventId == 20 ? e with { EventId = 6 } : e),
            "seek-after-restart" => Change(trace, e => e.EventId == 20 ? e with { EventId = 21 } : e.EventId == 21 ? e with { EventId = 20 } : e),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation))
        };
        AssertBlocked(invalid);
    }

    private static PlaybackTimingSnapshot Change(PlaybackTimingSnapshot trace, Func<PlaybackTimingEntry, PlaybackTimingEntry> change) =>
        trace with { Entries = trace.Entries.Select(change).ToArray() };

    private static void AssertBlocked(PlaybackTimingSnapshot trace)
    {
        var result = PlaybackTimingAnalysis.Analyze(trace);
        Assert.Equal("blocked", result.Status);
        Assert.NotEmpty(result.Blockers);
        Assert.Null(result.QueueMilliseconds);
        Assert.Null(result.CommandMilliseconds);
        Assert.Null(result.RequestToReadyMilliseconds);
        Assert.Null(result.DispatchToReadyMilliseconds);
        Assert.Null(result.SeekEventToReadyMilliseconds);
        Assert.False(result.PerformanceAccepted);
    }
}

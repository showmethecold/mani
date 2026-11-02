using System.Diagnostics;

namespace Mani.Playback;

// Event boundaries ported from IINA 1.5.1 MPVController's seek/restart timing.
// IINA: Copyright (c) 2016 lhc, GPL v3 (licenses/IINA-GPL-3.0.txt).
// Bounded Windows diagnostic adaptation: docs/m1/timing-capability.md.
public sealed record PlaybackTimingEntry(long Sequence, long Qpc, string Kind,
    long? OperationId = null, string? Operation = null, string? Detail = null,
    int? EventId = null, long? PlaylistEntryId = null, int? Error = null, int? EndReason = null);

public sealed record PlaybackTimingSnapshot(Guid CaptureId, long Frequency, int Capacity,
    bool Closed, string? CloseReason, long DroppedEntries, PlaybackTimingEntry[] Entries);

// No event pointers, callbacks, or file writes. All stored values are copied.
public sealed class PlaybackTimingCapture
{
    private readonly object _gate = new();
    private readonly List<PlaybackTimingEntry> _entries = new();
    private readonly Guid _id = Guid.NewGuid();
    private readonly int _capacity;
    private long _sequence, _operationId, _dropped;
    private bool _closed;
    private string? _closeReason;
    internal PlaybackTimingCapture(int capacity)
    {
        if (capacity is < 8 or > 4096) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
        Add("capture-start");
    }

    internal long Request(string operation, string? detail)
    {
        lock (_gate)
        {
            long id = ++_operationId;
            Add("requested", id, operation, detail);
            return id;
        }
    }
    internal void Add(string kind, long? operationId = null, string? operation = null,
        string? detail = null, int? eventId = null, long? playlistEntryId = null,
        int? error = null, int? endReason = null)
    {
        lock (_gate)
        {
            if (_closed) return;
            if (_entries.Count >= _capacity) { _dropped++; return; }
            _entries.Add(new(++_sequence, Stopwatch.GetTimestamp(), kind, operationId, operation,
                detail, eventId, playlistEntryId, error, endReason));
        }
    }
    internal void Close(string reason)
    {
        lock (_gate)
        {
            if (_closed) return;
            Add("capture-stop");
            _closed = true;
            _closeReason = reason;
        }
    }
    public PlaybackTimingSnapshot Snapshot()
    {
        lock (_gate) return new(_id, Stopwatch.Frequency, _capacity, _closed, _closeReason,
            _dropped, _entries.ToArray());
    }
}

public sealed record PlaybackTimingResult(string Status, string[] Blockers,
    string? Operation, long? OperationId, double? QueueMilliseconds,
    double? CommandMilliseconds, double? RequestToReadyMilliseconds,
    double? DispatchToReadyMilliseconds, double? SeekEventToReadyMilliseconds)
{
    public string Endpoint => "Client-observed MPV_EVENT_PLAYBACK_RESTART; not a presentation timestamp.";
    public bool PerformanceAccepted => false;
}

public static class PlaybackTimingAnalysis
{
    public static PlaybackTimingResult Analyze(PlaybackTimingSnapshot trace)
    {
        var blockers = new List<string>();
        var entries = trace.Entries;
        void Require(bool valid, string reason) { if (!valid) blockers.Add(reason); }
        Require(trace.Frequency > 0, "Invalid clock frequency.");
        Require(trace.Closed && trace.CloseReason == "stopped", "Capture was not explicitly completed.");
        Require(trace.DroppedEntries == 0, "Capture overflowed.");
        Require(entries.Length >= 5 && entries.Length <= trace.Capacity, "Incomplete or oversized capture.");
        Require(entries.Length > 0 && entries[0].Kind == "capture-start" &&
            entries[^1].Kind == "capture-stop", "Capture boundaries are missing.");
        Require(entries.Select((x, i) => x.Sequence == i + 1 && x.Qpc > 0).All(x => x) &&
            entries.Zip(entries.Skip(1)).All(x => x.Second.Qpc >= x.First.Qpc), "Non-monotonic event timeline.");
        var requests = entries.Where(x => x.Kind == "requested").ToArray();
        Require(requests.Length == 1, "Exactly one isolated operation is required.");
        var request = requests.FirstOrDefault();
        Require(request?.OperationId is > 0 && entries.Where(x => x.Kind is "requested" or "dispatched" or "accepted")
            .All(x => x.OperationId == request.OperationId), "Operation identity is missing or mixed.");
        Require(request?.Operation is "open" or "seek", "Only open and seek operations are measurable.");
        var dispatched = entries.Where(x => x.Kind == "dispatched" && x.OperationId == request?.OperationId).ToArray();
        var accepted = entries.Where(x => x.Kind == "accepted" && x.OperationId == request?.OperationId).ToArray();
        Require(dispatched.Length == 1 && accepted.Length == 1, "Command dispatch or acceptance is missing.");
        Require(!entries.Any(x => x.Kind == "command-error"), "Command failed.");
        var dispatch = dispatched.FirstOrDefault();
        var accept = accepted.FirstOrDefault();
        Require(request is not null && dispatch is not null && accept is not null &&
            request.Sequence < dispatch.Sequence && dispatch.Sequence < accept.Sequence, "Invalid command order.");

        var events = entries.Where(x => x.Kind == "event" && x.Sequence > (accept?.Sequence ?? long.MaxValue)).ToArray();
        var restarts = events.Where(x => x.EventId == 21).ToArray();
        Require(restarts.Length == 1, "Expected exactly one playback restart.");
        var restart = restarts.FirstOrDefault();
        Require(!events.Any(x => x.EventId is 1 or 24 || x.Error < 0), "Native failure or event queue overflow.");
        PlaybackTimingEntry? seek = null;
        if (request?.Operation == "open")
        {
            var starts = events.Where(x => x.EventId == 6).ToArray();
            var loaded = events.Where(x => x.EventId == 8).ToArray();
            Require(starts.Length == 1 && loaded.Length == 1 &&
                starts[0].PlaylistEntryId is >= 0 && starts[0].Sequence < loaded[0].Sequence && loaded[0].Sequence < restart?.Sequence,
                "Open lacks an ordered start-file/file-loaded/restart sequence.");
            if (starts.Length == 1)
                Require(!events.Any(x => x.EventId == 7 && x.Sequence > starts[0].Sequence),
                    "New media ended during measurement.");
        }
        else if (request?.Operation == "seek")
        {
            var seeks = events.Where(x => x.EventId == 20).ToArray();
            Require(seeks.Length == 1 && seeks[0].Sequence < restart?.Sequence, "Seek lacks exactly one seek event before restart.");
            Require(!events.Any(x => x.EventId is 6 or 7 or 8), "Media changed or ended during seek.");
            seek = seeks.FirstOrDefault();
        }
        // A stale event before the dispatch/acceptance boundary makes attribution ambiguous.
        Require(!entries.Any(x => x.Kind == "event" && x.EventId is 6 or 8 or 20 or 21 &&
            x.Sequence < (accept?.Sequence ?? long.MaxValue)), "Playback was not settled before the command.");
        if (blockers.Count > 0)
            return new("blocked", blockers.ToArray(), request?.Operation, request?.OperationId, null, null, null, null, null);
        double Ms(PlaybackTimingEntry a, PlaybackTimingEntry b) => (b.Qpc - a.Qpc) * 1000.0 / trace.Frequency;
        return new("measured", [], request!.Operation, request.OperationId,
            Ms(request, dispatch!), Ms(dispatch!, accept!), Ms(request, restart!), Ms(dispatch!, restart!),
            seek is null ? null : Ms(seek, restart!));
    }
}


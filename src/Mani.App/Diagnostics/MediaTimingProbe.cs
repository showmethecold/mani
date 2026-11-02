using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mani.Playback;

namespace Mani_App.Diagnostics;

// Reuses IINA 1.5.1 seek/restart boundaries through PlaybackTiming and exact
// commands through PlaybackSession. Windows batch: docs/m1/media-timing.md.
internal sealed record TimingMedia(string id, string path, long length, string sha256);
internal sealed record TimingController(int pid, long creationFileTime)
{
    public void RequireAlive()
    {
        if (pid <= 0 || creationFileTime <= 0) throw new InvalidDataException("Invalid controller identity.");
        using var process = Process.GetProcessById(pid);
        if (process.HasExited || process.StartTime.ToUniversalTime().ToFileTimeUtc() != creationFileTime)
            throw new InvalidDataException("Controller process exited or its PID was reused.");
    }
}
internal sealed record MediaTimingRequest(int schema_version, string automationId,
    DateTimeOffset requestedAtUtc, TimingMedia[] media,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? referenceEnvironment = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? scope = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TimingController? traceController = null)
{
    public const string FirstOpenTraceScope = "first-open-trace";
    [JsonIgnore] public bool IsFirstOpenTrace => scope == FirstOpenTraceScope;

    public void Validate(DateTimeOffset now)
    {
        if (schema_version != 1 || !Guid.TryParseExact(automationId, "N", out var id) ||
            id.ToString("N") != automationId || (now - requestedAtUtc).TotalSeconds is < -5 or > 600 ||
            (scope is not null && scope != FirstOpenTraceScope) ||
            (IsFirstOpenTrace ? media is not { Length: 1 } || media[0]?.id != "1080p" ||
                referenceEnvironment is null || traceController is null || traceController.pid <= 0 || traceController.creationFileTime <= 0
                : media is not { Length: 2 } || media[0]?.id != "1080p" || media[1]?.id != "4k" || traceController is not null))
            throw new InvalidDataException("Invalid or expired media timing request.");
        if (referenceEnvironment is JsonElement expected) TimingWindowPreparation.Read(expected);
        foreach (var item in media)
            if (string.IsNullOrWhiteSpace(item.path) || !Path.IsPathFullyQualified(item.path) ||
                item.path.StartsWith(@"\\") || item.length <= 0 || item.sha256 is not { Length: 64 } ||
                item.sha256.Any(c => !"0123456789abcdef".Contains(c)))
                throw new InvalidDataException("Expected pinned local media paths and SHA-256 identities.");
        if (!IsFirstOpenTrace && string.Equals(Path.GetFullPath(media[0].path), Path.GetFullPath(media[1].path), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Two different media files are required.");
    }

    public TimingStep[] Plan(double duration) => IsFirstOpenTrace ? [MediaTimingProbe.Plan(duration)[0]] : MediaTimingProbe.Plan(duration);

    public static MediaTimingRequest Claim(string directory)
    {
        if (Directory.GetFiles(directory, "pending-*.json").Length != 1)
            throw new InvalidOperationException("Run one unattended test at a time.");
        string claimed = Path.Combine(directory, "claimed-media-timing-" + Guid.NewGuid().ToString("N") + ".json");
        File.Move(Path.Combine(directory, "pending-media-timing.json"), claimed);
        var request = JsonSerializer.Deserialize<MediaTimingRequest>(File.ReadAllText(claimed))
            ?? throw new InvalidDataException("Empty media timing request.");
        request.Validate(DateTimeOffset.UtcNow);
        return request;
    }
}

// Reuse the existing window-check geometry path; only diagnostic preflight is new.
// No screen/HDR/DPI changes and no moving a window during timed playback.
internal static class TimingWindowPreparation
{
    internal sealed record Rectangle(int X, int Y, int Width, int Height, string Monitor);

    public static Rectangle Read(JsonElement expected)
    {
        try
        {
            var position = expected.GetProperty("position");
            var size = expected.GetProperty("size");
            string monitor = expected.GetProperty("display").GetProperty("monitor").GetString()!;
            var rect = new Rectangle(position.GetProperty("X").GetInt32(), position.GetProperty("Y").GetInt32(),
                size.GetProperty("Width").GetInt32(), size.GetProperty("Height").GetInt32(), monitor);
            if (expected.GetProperty("presenter").GetString() != "Overlapped" ||
                expected.GetProperty("windowState").GetString() != "Restored" ||
                expected.GetProperty("webEnabled").GetBoolean() || expected.GetProperty("blurEnabled").GetBoolean() ||
                string.IsNullOrWhiteSpace(monitor) || monitor.Length > 256 || rect.Width <= 0 || rect.Height <= 0)
                throw new InvalidDataException("Expected a restored reference window with overlays disabled.");
            return rect;
        }
        catch (Exception error) when (error is InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        { throw new InvalidDataException("Invalid reference window environment.", error); }
    }

    public static void RequireSameWorkArea(Rectangle rect, string currentMonitor, int x, int y, int width, int height)
    {
        if (rect.Monitor != currentMonitor || width <= 0 || height <= 0 ||
            rect.X < x || rect.Y < y || (long)rect.X + rect.Width > (long)x + width ||
            (long)rect.Y + rect.Height > (long)y + height)
            throw new InvalidOperationException("Reference window must fit the current monitor work area. No cross-display move is allowed.");
    }

    public static bool Matches(JsonElement actual, JsonElement expected) => JsonElement.DeepEquals(actual, expected);
}

internal sealed record TimingStep(string Id, string Operation, bool Paused, double? Target);
internal sealed record TimingOperation(TimingStep Step, string Status, string? Failure,
    PlaybackTimingSnapshot Trace, PlaybackTimingResult Timing, PlaybackSnapshot Before,
    PlaybackSnapshot? After, long LogStart, long LogEnd);

internal static class MediaTimingProbe
{
    public static TimingStep[] Plan(double duration)
    {
        if (!double.IsFinite(duration) || duration < 6) throw new InvalidDataException("Media is too short.");
        double end = Math.Min(120, duration * 0.7);
        return [new("open-1", "open", true, null), new("open-2", "open", true, null),
            new("open-3", "open", true, null),
            .. new[] { true, false }.SelectMany(paused => new[] { end / 4, end, end / 2 }
                .Select((target, i) => new TimingStep($"{(paused ? "paused" : "playing")}-seek-{i + 1}", "seek", paused, target)))];
    }

    public static async Task SettleAsync(PlaybackSession session, string? path, bool paused,
        CancellationToken cancellation)
    {
        var timeout = Stopwatch.StartNew();
        long previous = -1;
        long stableSince = Stopwatch.GetTimestamp();
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            var state = await session.ReadAsync();
            var props = await session.GetPropertiesAsync("seeking");
            long restarts = await session.GetPlaybackRestartCountAsync();
            bool ready = path is null ? state.Idle : !state.Idle && !state.Loading && !state.Ended &&
                !state.PausedForCache && state.LoadFailure.Length == 0 && state.Path == path && state.Paused == paused;
            bool seekSettled = props["seeking"] == "no" || (path is null && props["seeking"].Length == 0);
            if (!ready || !seekSettled || restarts != previous) stableSince = Stopwatch.GetTimestamp();
            if (ready && seekSettled && Stopwatch.GetElapsedTime(stableSince).TotalMilliseconds >= 500) return;
            previous = restarts;
            if (timeout.Elapsed.TotalSeconds > 20) throw new TimeoutException("Playback did not settle before measurement: " + JsonSerializer.Serialize(new { state, props, restarts, path, paused }));
            await Task.Delay(50, cancellation);
        }
    }

    public static async Task<TimingOperation> MeasureAsync(PlaybackSession session, string path,
        TimingStep step, Func<long> logOffset, CancellationToken cancellation, Action? captureClosed = null)
    {
        var before = await session.ReadAsync();
        long start = logOffset();
        string? failure = null;
        var capture = await session.BeginTimingCaptureAsync();
        PlaybackTimingSnapshot trace;
        try
        {
            cancellation.ThrowIfCancellationRequested();
            if (step.Operation == "open") await session.OpenAsync(path, paused: step.Paused);
            else if (step.Operation == "seek" && step.Target is double target)
                await session.CommandAsync("seek", target.ToString("R", CultureInfo.InvariantCulture), "absolute+exact");
            else throw new InvalidDataException("Invalid timing operation.");
            var timeout = Stopwatch.StartNew();
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                var events = capture.Snapshot().Entries;
                if (events.Any(e => e.Kind == "event" && (e.EventId is 1 or 24 || e.Error < 0)))
                    throw new InvalidOperationException("Native failure during timing.");
                if (events.Any(e => e.Kind == "event" && e.EventId == 21)) break;
                if (timeout.Elapsed.TotalSeconds > 20) throw new TimeoutException("No playback restart was observed.");
                await Task.Delay(10, cancellation);
            }
        }
        catch (Exception error) { failure = error.ToString(); }
        finally
        {
            trace = await session.EndTimingCaptureAsync(capture, cancelled: failure is not null);
            captureClosed?.Invoke(); // Trace stop signal before boundary reads; ordinary timing has no callback.
        }
        // Boundary reads and file I/O happen after closing the event scope.
        var timing = PlaybackTimingAnalysis.Analyze(trace);
        var after = await session.ReadAsync();
        if (after.Path != path || after.Idle || after.Loading || after.Ended || after.Paused != step.Paused ||
            after.PausedForCache || after.LoadFailure.Length > 0)
            failure ??= "Playback boundary changed or did not become ready.";
        if (step.Operation == "seek" && (after.FileLoadedCount != before.FileLoadedCount ||
            after.Position < step.Target - 0.2 || after.Position > step.Target + (step.Paused ? 0.2 : 1.0)))
            failure ??= "Seek changed media or missed its exact target.";
        if (step.Operation == "open" && after.FileLoadedCount != before.FileLoadedCount + 1)
            failure ??= "Expected exactly one newly loaded file.";
        if (timing.Status != "measured") failure ??= string.Join("; ", timing.Blockers);
        return new(step, failure is null ? "measured" : "blocked", failure, trace, timing, before, after, start, logOffset());
    }
}

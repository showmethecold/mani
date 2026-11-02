using System.Diagnostics;
using System.Globalization;
using Mani.Playback;
using System.Text.Json;

namespace Mani_App.Diagnostics;

// IINA assessment and Windows measurement boundaries: docs/m1/overhead-attribution.md.
internal sealed record OverheadRequest(int schema_version, string automationId, DateTimeOffset requestedAtUtc, bool diagnosticOnly = false, string inputCondition = OverheadInput.Policy)
{
    public static OverheadRequest Claim(string directory)
    {
        var pending = Path.Combine(directory, "pending-overhead.json");
        var claimed = Path.Combine(directory, "claimed-overhead-" + Guid.NewGuid().ToString("N") + ".json");
        File.Move(pending, claimed);
        var request = JsonSerializer.Deserialize<OverheadRequest>(File.ReadAllText(claimed))
            ?? throw new InvalidDataException("Empty overhead request.");
        if (request.inputCondition != OverheadInput.Policy || request.schema_version != 1 || !Guid.TryParseExact(request.automationId, "N", out var id) ||
            id.ToString("N") != request.automationId)
            throw new InvalidDataException("Invalid overhead request.");
        var age = (DateTimeOffset.UtcNow - request.requestedAtUtc).TotalSeconds;
        if (age < -5 || age > 600) throw new InvalidDataException("Overhead request expired.");
        return request;
    }
}

internal sealed class OverheadPollGate
{
    public static readonly string[] Plan = ["normal", "engine", "quiet", "quiet", "engine", "normal"];
    public const int MeasurementSeconds = 20;
    public string Mode { get; private set; } = "normal";
    public bool Measuring { get; private set; }
    public long Polls { get; private set; }
    public long UiUpdates { get; private set; }
    public long SkippedTicks { get; private set; }

    public void SetMode(string mode)
    {
        if (!Plan.Contains(mode)) throw new ArgumentException("Unknown overhead mode.");
        if (Measuring) throw new InvalidOperationException("Drain measurement before changing polling mode.");
        Mode = mode;
    }
    public void Begin()
    {
        Polls = UiUpdates = SkippedTicks = 0;
        Measuring = true;
    }
    public void End() => Measuring = false;
    public void Reset() { End(); Mode = "normal"; }
    public bool BeginPoll()
    {
        if (Mode == "quiet") { if (Measuring) SkippedTicks++; return false; }
        if (Measuring) Polls++;
        return true;
    }
    public bool BeginUiUpdate()
    {
        if (Mode != "normal") return false;
        if (Measuring) UiUpdates++;
        return true;
    }
}

internal sealed record OverheadRuntime(double QpcSeconds, long AllocatedBytes, long ManagedEstimateBytes,
    long LastGcIndex, long HeapSizeAtLastGc, long CommittedAtLastGc, long FragmentationAtLastGc,
    int Gen0, int Gen1, int Gen2)
{
    // Boundary-only diagnostic counters. A GC count alone does not measure its pause duration.
    public double TotalGcPauseSeconds { get; init; }
    public double LastGcPauseSeconds { get; init; }
    public static OverheadRuntime Capture()
    {
        var gc = GC.GetGCMemoryInfo();
        double lastPause = 0;
        foreach (var pause in gc.PauseDurations) lastPause += pause.TotalSeconds;
        return new(Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency,
            GC.GetTotalAllocatedBytes(precise: true), GC.GetTotalMemory(forceFullCollection: false),
            gc.Index, gc.HeapSizeBytes, gc.TotalCommittedBytes, gc.FragmentedBytes,
            GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2))
        {
            TotalGcPauseSeconds = GC.GetTotalPauseDuration().TotalSeconds,
            LastGcPauseSeconds = lastPause
        };
    }

    // Only lifecycle boundaries, outside timed resource windows. No Threads enumeration or forced GC.
    public static object MemoryCheckpoint(string label)
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        return new { label, atUtc = DateTimeOffset.UtcNow, process.PrivateMemorySize64, process.WorkingSet64,
            process.HandleCount, runtime = Capture() };
    }
}

// Video output includes per-frame HDR measurements, not just configuration.
// Preserve raw properties in evidence; compare every field except these six observations.
internal static class OverheadSettings
{
    public static readonly string[] Names = ["speed", "mute", "volume", "video-sync", "hwdec", "glsl-shaders", "vf", "af",
        "sid", "secondary-sid", "video-params", "video-out-params", "d3d11-output-mode"];
    private static readonly HashSet<string> DynamicOutput = ["scene-max-r", "scene-max-g", "scene-max-b",
        "scene-avg", "max-pq-y", "avg-pq-y"];
    private static readonly string[] RequiredVideo = ["pixelformat", "hw-pixelformat", "w", "h", "dw", "dh", "primaries", "gamma"];

    public static IReadOnlyDictionary<string, JsonElement> Stable(IReadOnlyDictionary<string, string> settings)
    {
        if (settings.Count != Names.Length || Names.Any(k => !settings.ContainsKey(k)))
            throw new InvalidDataException("Incomplete overhead settings.");
        var stable = new Dictionary<string, JsonElement>();
        foreach (string name in Names)
        {
            if (name is not ("video-params" or "video-out-params"))
            {
                stable[name] = JsonSerializer.SerializeToElement(settings[name]);
                continue;
            }
            using var document = JsonDocument.Parse(settings[name]);
            var value = document.RootElement;
            if (value.ValueKind != JsonValueKind.Object || RequiredVideo.Any(k => !value.TryGetProperty(k, out _)))
                throw new InvalidDataException("Incomplete video parameters: " + name);
            var fields = new Dictionary<string, JsonElement>();
            var seen = new HashSet<string>();
            foreach (var property in value.EnumerateObject())
            {
                if (!seen.Add(property.Name)) throw new InvalidDataException("Duplicate video parameter: " + property.Name);
                if (name == "video-out-params" && DynamicOutput.Contains(property.Name))
                {
                    if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetDouble(out var number) ||
                        !double.IsFinite(number) || number < 0)
                        throw new InvalidDataException("Invalid dynamic output measurement: " + property.Name);
                    continue;
                }
                fields.Add(property.Name, property.Value.Clone());
            }
            stable[name] = JsonSerializer.SerializeToElement(fields);
        }
        return stable;
    }

    public static string[] Changes(IReadOnlyDictionary<string, string> before, IReadOnlyDictionary<string, string> after)
    {
        var first = Stable(before);
        var last = Stable(after);
        var changes = new List<string>();
        foreach (string name in Names)
        {
            if (name is "video-params" or "video-out-params")
            {
                var keys = first[name].EnumerateObject().Select(p => p.Name)
                    .Union(last[name].EnumerateObject().Select(p => p.Name));
                foreach (string key in keys)
                    if (!first[name].TryGetProperty(key, out var a) || !last[name].TryGetProperty(key, out var b) ||
                        !JsonElement.DeepEquals(a, b)) changes.Add(name + "/" + key);
            }
            else if (!JsonElement.DeepEquals(first[name], last[name])) changes.Add(name);
        }
        return changes.ToArray();
    }
}

internal static class OverheadWindow
{
    public static string[] Problems(PlaybackSnapshot before, PlaybackSnapshot after,
        OverheadRuntime runtimeBefore, OverheadRuntime runtimeAfter)
    {
        var problems = new List<string>();
        double elapsed = runtimeAfter.QpcSeconds - runtimeBefore.QpcSeconds;
        double advance = after.Position - before.Position;
        if (!double.IsFinite(elapsed) || elapsed < 19 || elapsed > 23)
            problems.Add(FormattableString.Invariant($"Measurement duration ({elapsed:F3} s)"));
        if (!double.IsFinite(advance) || advance < 19 || advance > 23)
            problems.Add(FormattableString.Invariant($"Position advance ({advance:F3} s over {elapsed:F3} s)"));
        if (!long.TryParse(before.DroppedFrames, NumberStyles.None, CultureInfo.InvariantCulture, out long first) ||
            !long.TryParse(after.DroppedFrames, NumberStyles.None, CultureInfo.InvariantCulture, out long last) || last < first)
            problems.Add("Dropped-frame counters are invalid");
        else if (last != first)
            problems.Add($"Dropped frames (+{last - first})");
        return problems.ToArray();
    }
}

using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Mani.Playback;

namespace Mani_App.Diagnostics;

// Diagnostic orchestration only; rendering and channel properties belong to libmpv.
// Fixed IINA source/platform assessment: docs/m1/subtitle-validation.md.
internal static class SubtitleVisualProbe
{
    internal sealed record Stage(string Id, string Label, string Hint, string PrimaryMode, string SecondaryMode,
        double PrimaryPosition, double SecondaryPosition, double PrimaryDelay, double SecondaryDelay, bool Animate);
    internal static readonly Stage[] Stages = [
        new("preserve", "1/9 - Preserve both ASS tracks", "Yellow PRIMARY and cyan SECONDARY should move and fade independently.", "no", "no", 100, 100, 0, 0, true),
        new("secondary-plain", "2/9 - Secondary plain text", "SECONDARY should be plain and still. PRIMARY keeps its yellow moving style.", "no", "strip", 100, 20, 0, 0, true),
        new("primary-plain", "3/9 - Primary plain text", "PRIMARY should be plain and still. SECONDARY keeps its cyan moving style.", "strip", "no", 80, 100, 0, 0, true),
        new("plain-baseline", "4/9 - Both plain text", "PRIMARY is low; SECONDARY is high. Both should be readable and still.", "strip", "strip", 80, 20, 0, 0, false),
        new("secondary-position", "5/9 - Move secondary only", "SECONDARY moves down. PRIMARY stays in the same place.", "strip", "strip", 80, 40, 0, 0, false),
        new("primary-position", "6/9 - Move primary only", "PRIMARY moves up. SECONDARY stays in the same place.", "strip", "strip", 60, 40, 0, 0, false),
        new("secondary-delay", "7/9 - Delay secondary only", "At this paused frame: PRIMARY 03 and SECONDARY 02.", "strip", "strip", 60, 40, 0, 2, false),
        new("primary-delay", "8/9 - Delay primary only", "At this paused frame: PRIMARY 02 and SECONDARY 03.", "strip", "strip", 60, 40, 2, 0, false),
        new("preserve-restored", "9/9 - Restore ASS styles", "Both colored tracks should move again with matching cue numbers.", "no", "no", 100, 100, 0, 0, true)
    ];
    internal static readonly string[] Settings = [
        "sub-ass-override", "secondary-sub-ass-override", "sub-delay", "secondary-sub-delay",
        "sub-pos", "secondary-sub-pos", "sub-scale", "secondary-sub-scale", "sub-visibility", "secondary-sub-visibility",
        "speed", "screenshot-format", "screenshot-high-bit-depth"
    ];
    internal sealed class Result
    {
        public string Status { get; set; } = "running";
        public string? Failure { get; set; }
        public List<string> CleanupFailures { get; } = [];
        public Dictionary<string, string> FixtureHashes { get; } = [];
        public IReadOnlyDictionary<string, string>? Before { get; set; }
        public IReadOnlyDictionary<string, string>? After { get; set; }
        public string? PrimaryId { get; set; }
        public string? SecondaryId { get; set; }
        public List<object> Observations { get; } = [];
        public List<string> CompletedStages { get; } = [];
        public PlaybackSnapshot? FinalState { get; set; }
        public bool VisualAccepted => false;
    }
    internal static bool EqualValue(string a, string b) => a == b ||
        (double.TryParse(a, CultureInfo.InvariantCulture, out var x) && double.TryParse(b, CultureInfo.InvariantCulture, out var y) && Math.Abs(x - y) < 0.00001);

    internal static async Task<Result> RunAsync(PlaybackSession session, string assets, string directory,
        bool realTime, Func<Stage, Task> progress, Func<Task<object?>> hostCapture, CancellationToken cancellation,
        Func<Result, Task>? checkpoint = null)
    {
        var result = new Result();
        bool opened = false;
        string mediaPath = "";
        long loadedCount = 0;
        async Task Save() { if (checkpoint is not null) await checkpoint(result); }
        async Task CheckState(bool paused)
        {
            var state = await session.ReadAsync();
            if (state.Path != mediaPath || state.Idle || state.Loading || state.Ended || state.Paused != paused ||
                state.LoadFailure.Length > 0 || state.FileLoadedCount != loadedCount)
                throw new InvalidOperationException("Subtitle fixture playback changed unexpectedly: " + JsonSerializer.Serialize(state));
        }
        async Task Seek(double position)
        {
            await session.SetAsync("pause", "yes");
            long restarts = await session.GetPlaybackRestartCountAsync();
            await session.CommandAsync("seek", position.ToString(CultureInfo.InvariantCulture), "absolute+exact");
            var clock = Stopwatch.StartNew();
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                var state = await session.ReadAsync();
                if (state.LoadFailure.Length > 0) throw new IOException(state.LoadFailure);
                if (state.Paused && Math.Abs(state.Position - position) < 0.07 &&
                    await session.GetPlaybackRestartCountAsync() > restarts) break;
                if (clock.Elapsed.TotalSeconds > 12) throw new TimeoutException("Subtitle test seek did not settle.");
                await Task.Delay(30, cancellation);
            }
            await Task.Delay(200, cancellation); // Let libass redraw the paused output.
            await CheckState(true);
        }
        async Task Screenshot(string name)
        {
            cancellation.ThrowIfCancellationRequested();
            await session.CommandAsync("screenshot-to-file", Path.Combine(directory, name), "window");
            if (!File.Exists(Path.Combine(directory, name))) throw new IOException("Subtitle screenshot was not saved.");
        }
        async Task Capture(Stage stage, string point)
        {
            await CheckState(true);
            var capture = await DisplaySignalProbe.CaptureAsync(session);
            var names = Settings.Concat(["sid", "secondary-sid", "sub-text", "secondary-sub-text", "track-list"]).ToArray();
            var properties = await session.GetPropertiesAsync(names);
            if (properties["sid"] != result.PrimaryId || properties["secondary-sid"] != result.SecondaryId)
                throw new InvalidOperationException("Subtitle channel selection changed.");
            foreach (var pair in StageSettings(stage))
                if (!EqualValue(properties[pair.Key], pair.Value))
                    throw new InvalidOperationException("Subtitle setting mismatch: " + pair.Key);
            foreach (var (key, label, delay) in new[] { ("sub-text", "PRIMARY", stage.PrimaryDelay), ("secondary-sub-text", "SECONDARY", stage.SecondaryDelay) })
            {
                int cue = (int)Math.Floor((capture.State.Position - delay) / 4) + 1;
                if (!properties[key].Contains($"{label} {cue:00}", StringComparison.Ordinal))
                    throw new InvalidOperationException($"Unexpected {label} cue at {capture.State.Position:F3}: {properties[key]}");
            }
            var host = await hostCapture();
            string screenshot = $"{stage.Id}-{point}.png";
            await Screenshot(screenshot);
            result.Observations.Add(new { stage = stage.Id, point, atUtc = DateTimeOffset.UtcNow, capture, properties, host, screenshot });
            await Save();
        }
        try
        {
            Directory.CreateDirectory(directory);
            await Save();
            using var display = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(assets, "display-media.json"), cancellation));
            using var subtitles = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(assets, "subtitle-fixtures.json"), cancellation));
            async Task<string> Verify(JsonElement entry)
            {
                string path = Path.Combine(assets, entry.GetProperty("file").GetString()!);
                await using var input = File.OpenRead(path);
                string hash = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellation));
                if (!hash.Equals(entry.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Subtitle test fixture hash mismatch: " + Path.GetFileName(path));
                result.FixtureHashes[Path.GetFileName(path)] = hash;
                return path;
            }
            mediaPath = await Verify(display.RootElement.GetProperty("hdr-av"));
            string primary = await Verify(subtitles.RootElement.GetProperty("tracks").GetProperty("primary"));
            string secondary = await Verify(subtitles.RootElement.GetProperty("tracks").GetProperty("secondary"));
            result.Before = await session.GetPropertiesAsync(Settings);
            if (result.Before.Any(p => p.Value.Length == 0)) throw new InvalidOperationException("A required subtitle test option is unavailable.");
            long beforeLoads = (await session.ReadAsync()).FileLoadedCount;
            long beforeRestarts = await session.GetPlaybackRestartCountAsync();
            opened = true;
            await session.OpenAsync(mediaPath, paused: true);
            var loadClock = Stopwatch.StartNew();
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                var capture = await DisplaySignalProbe.CaptureAsync(session);
                if (UnattendedPlaybackReadiness.IsReady(capture.State, mediaPath, capture.Swapchain is not null,
                    await session.GetPlaybackRestartCountAsync() > beforeRestarts) && capture.State.FileLoadedCount > beforeLoads &&
                    DisplaySignalProbe.OutputMatchesPolicy(capture))
                { loadedCount = capture.State.FileLoadedCount; break; }
                if (loadClock.Elapsed.TotalSeconds > 20) throw new TimeoutException("Subtitle test video did not become ready.");
                await Task.Delay(50, cancellation);
            }
            // Same libmpv load/select path as the existing manual subtitle diagnostic.
            await session.CommandAsync("sub-add", primary, "select");
            result.PrimaryId = (await session.GetPropertiesAsync("sid"))["sid"];
            await session.CommandAsync("sub-add", secondary, "select");
            result.SecondaryId = (await session.GetPropertiesAsync("sid"))["sid"];
            if (result.PrimaryId == result.SecondaryId || result.PrimaryId is "" or "no" || result.SecondaryId is "" or "no")
                throw new InvalidOperationException("Two distinct ASS subtitle tracks were not loaded.");
            await session.SetAsync("sid", result.PrimaryId);
            await session.SetAsync("secondary-sid", result.SecondaryId);
            await session.SetAsync("speed", "1");
            await session.SetAsync("screenshot-format", "png");
            await session.SetAsync("screenshot-high-bit-depth", "no");
            foreach (var stage in Stages)
            {
                cancellation.ThrowIfCancellationRequested();
                foreach (var pair in StageSettings(stage)) await session.SetAsync(pair.Key, pair.Value);
                await Seek(8.75);
                await progress(stage);
                await Capture(stage, "start");
                if (!realTime)
                {
                    // Fixed-frame isolation for pixel analysis only; never flicker visible user playback.
                    foreach (var (suffix, primaryVisible, secondaryVisible) in new[] { ("none", false, false), ("primary", true, false), ("secondary", false, true) })
                    {
                        await session.SetAsync("sub-visibility", primaryVisible ? "yes" : "no");
                        await session.SetAsync("secondary-sub-visibility", secondaryVisible ? "yes" : "no");
                        await Task.Delay(120, cancellation);
                        await Screenshot($"{stage.Id}-{suffix}.png");
                    }
                    await session.SetAsync("sub-visibility", "yes");
                    await session.SetAsync("secondary-sub-visibility", "yes");
                    await Seek(10.75);
                    await Capture(stage, "later");
                }
                else if (stage.Animate)
                {
                    await session.SetAsync("pause", "no");
                    for (int sample = 0; sample < 2; sample++)
                    {
                        double from = (await session.ReadAsync()).Position;
                        await Task.Delay(2000, cancellation);
                        await CheckState(false);
                        if ((await session.ReadAsync()).Position - from < 1.5)
                            throw new InvalidOperationException("Subtitle animation playback did not advance.");
                        await session.SetAsync("pause", "yes");
                        await Task.Delay(100, cancellation);
                        await Capture(stage, sample == 0 ? "middle" : "end");
                        if (sample == 0) await session.SetAsync("pause", "no");
                    }
                }
                else await Task.Delay(3000, cancellation);
                result.CompletedStages.Add(stage.Id);
                await Save();
            }
            result.Status = "completed";
        }
        catch (OperationCanceledException) { result.Status = "cancelled"; }
        catch (Exception error) { result.Status = "failed"; result.Failure = error.ToString(); }
        finally
        {
            async Task Cleanup(string label, Func<Task> action)
            { try { await action(); } catch (Exception error) { result.CleanupFailures.Add(label + ": " + error); } }
            if (opened)
            {
                await Cleanup("pause", () => session.SetAsync("pause", "yes"));
                await Cleanup("clear primary", () => session.SetAsync("sid", "no"));
                await Cleanup("clear secondary", () => session.SetAsync("secondary-sid", "no"));
                foreach (var id in new[] { result.PrimaryId, result.SecondaryId }.Where(x => x is not null and not "" and not "no").Distinct())
                    await Cleanup("remove test track " + id, () => session.CommandAsync("sub-remove", id!));
            }
            if (result.Before is not null)
                foreach (var p in result.Before) await Cleanup("restore " + p.Key, () => session.SetAsync(p.Key, p.Value));
            await Cleanup("verify restoration", async () =>
            {
                result.After = await session.GetPropertiesAsync(Settings);
                result.FinalState = await session.ReadAsync();
                if (result.Before is not null && result.Before.Any(p => !EqualValue(result.After[p.Key], p.Value)))
                    throw new InvalidOperationException("Subtitle test settings were not restored.");
                if (opened && !result.FinalState.Paused) throw new InvalidOperationException("Subtitle fixture was not left paused.");
            });
            if (result.CleanupFailures.Count > 0) result.Status = "failed";
            await Save();
        }
        return result;
    }

    private static Dictionary<string, string> StageSettings(Stage stage) => new()
    {
        ["sub-ass-override"] = stage.PrimaryMode, ["secondary-sub-ass-override"] = stage.SecondaryMode,
        ["sub-pos"] = stage.PrimaryPosition.ToString(CultureInfo.InvariantCulture),
        ["secondary-sub-pos"] = stage.SecondaryPosition.ToString(CultureInfo.InvariantCulture),
        ["sub-delay"] = stage.PrimaryDelay.ToString(CultureInfo.InvariantCulture),
        ["secondary-sub-delay"] = stage.SecondaryDelay.ToString(CultureInfo.InvariantCulture),
        ["sub-scale"] = "1", ["secondary-sub-scale"] = "1", ["sub-visibility"] = "yes", ["secondary-sub-visibility"] = "yes"
    };
}

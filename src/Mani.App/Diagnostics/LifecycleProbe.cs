using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Mani.Playback;

namespace Mani_App.Diagnostics;

// Exercise the existing libmpv commands/events; no alternative playback implementation.
// Source assessment and scope: docs/m1/lifecycle-validation.md.
internal static class LifecycleProbe
{
    // The UI projects END_FILE failure; arbitrary LOG_MESSAGE text stays diagnostic-only.
    // Match the current file generation and structured failure, never a coincidental log message.
    internal static bool IsFailureShown(PlaybackSnapshot state, PlaybackSnapshot? shown, string displayedStatus)
    {
        if (state.LoadFailure.Length == 0 || !state.Idle || state.Loading || shown is null ||
            !shown.Idle || shown.Loading || shown.Path != state.Path ||
            shown.FileLoadedCount != state.FileLoadedCount || shown.LoadFailure != state.LoadFailure)
            return false;
        return displayedStatus == shown.LoadFailure;
    }

    internal static string Fixture(string assets, string manifest, string key)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(assets, manifest)));
        var entry = json.RootElement.GetProperty(key);
        var path = Path.Combine(assets, entry.GetProperty("file").GetString()!);
        using var stream = File.OpenRead(path);
        var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        if (hash != entry.GetProperty("sha256").GetString()) throw new InvalidDataException("Lifecycle fixture hash differs.");
        return path;
    }

    internal static async Task<PlaybackSnapshot> WaitAsync(PlaybackSession session,
        Func<PlaybackSnapshot, bool> predicate, CancellationToken cancellation)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            var state = await session.ReadAsync();
            if (predicate(state)) return state;
            if (clock.Elapsed.TotalSeconds > 30)
                throw new TimeoutException("Lifecycle state did not settle: " + JsonSerializer.Serialize(state));
            await Task.Delay(50, cancellation);
        }
    }

    internal static async Task<PlaybackSnapshot> OpenReadyAsync(PlaybackSession session, string path,
        Func<PlaybackSnapshot, bool> hostReady, CancellationToken cancellation)
    {
        var before = await session.ReadAsync();
        var restart = await session.GetPlaybackRestartCountAsync();
        await session.OpenAsync(path, paused: true);
        return await ReadyAsync(session, path, before.FileLoadedCount, restart, hostReady, cancellation);
    }

    private static async Task<PlaybackSnapshot> ReadyAsync(PlaybackSession session, string path,
        long loadedBefore, long restartBefore, Func<PlaybackSnapshot, bool> hostReady, CancellationToken cancellation)
    {
        await WaitAsync(session, state => state.LoadFailure.Length > 0 ||
            (state.FileLoadedCount > loadedBefore && state.Path == path && !state.Loading && !state.Idle), cancellation);
        // Polling in the real host supplies the surface and Windows display policy.
        var clock = Stopwatch.StartNew();
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            var restarts = await session.GetPlaybackRestartCountAsync();
            var state = await WaitAsync(session, s =>
                UnattendedPlaybackReadiness.IsReady(s, path, hostReady(s), true), cancellation);
            if (restarts > restartBefore) return state;
            await Task.Delay(50, cancellation);
            // WaitAsync handles the normal state deadline; this protects a missing restart event too.
            if (clock.Elapsed.TotalSeconds > 30) throw new TimeoutException("Playback-restart event was not received.");
        }
    }

    internal static async Task RunAsync(PlaybackSession session, string assets, string directory,
        Func<PlaybackSnapshot, bool> hostReady, Func<object?> captureHost, CancellationToken cancellation)
    {
        Directory.CreateDirectory(directory);
        string sdr = Fixture(assets, "baseline-media.json", "1080p");
        string large = Fixture(assets, "baseline-media.json", "4k");
        string hdr = Fixture(assets, "display-media.json", "hdr-av");
        var checks = new List<object>();
        string status = "running";
        string? failure = null;
        async Task SaveAsync() => await File.WriteAllTextAsync(Path.Combine(directory, "recovery.json"),
            JsonSerializer.Serialize(new { status, failure, checks, fixtures = new[] { sdr, large, hdr } },
                new JsonSerializerOptions { WriteIndented = true }));
        async Task CheckAsync(string id, PlaybackSnapshot state, object? details = null)
        {
            checks.Add(new { id, atUtc = DateTimeOffset.UtcNow, state, host = captureHost(), details });
            await SaveAsync();
        }
        try
        {
            await CheckAsync("initial-1080p", await OpenReadyAsync(session, sdr, hostReady, cancellation));
            await CheckAsync("switch-4k", await OpenReadyAsync(session, large, hostReady, cancellation));
            await session.SetAsync("ab-loop-a", "1");
            await session.SetAsync("ab-loop-b", "2");
            var before = await session.ReadAsync();
            var restart = await session.GetPlaybackRestartCountAsync();
            string[] requested = Enumerable.Range(0, 20).Select(i => i == 19 ? hdr : i % 2 == 0 ? sdr : large).ToArray();
            var tasks = requested.Select(path => session.OpenAsync(path, paused: true)).ToArray();
            int pendingAtSubmission = tasks.Count(task => !task.IsCompleted);
            await Task.WhenAll(tasks);
            var last = await ReadyAsync(session, hdr, before.FileLoadedCount, restart, hostReady, cancellation);
            await CheckAsync("rapid-last-wins", last, new { requested, pendingAtSubmission });
            var loop = await session.GetPropertiesAsync("ab-loop-a", "ab-loop-b");
            if (loop.Values.Any(value => value != "no")) throw new InvalidOperationException("A-B endpoints survived a file switch.");
            await CheckAsync("loop-cleared", last, loop);

            string missing = Path.Combine(directory, "missing-" + Guid.NewGuid().ToString("N") + ".mkv");
            try { await session.OpenAsync(missing); throw new InvalidOperationException("Missing file was accepted."); }
            catch (FileNotFoundException) { }
            var preserved = await session.ReadAsync();
            if (preserved.Path != last.Path || preserved.FileLoadedCount != last.FileLoadedCount || preserved.Idle)
                throw new InvalidOperationException("Missing file changed the active media.");
            await CheckAsync("missing-rejected", preserved);

            string bad = Path.Combine(directory, "invalid-media.mkv");
            await File.WriteAllBytesAsync(bad, new byte[4096], cancellation);
            await session.OpenAsync(bad, paused: true);
            var rejected = await WaitAsync(session, s => s.LoadFailure.Length > 0 && !s.Loading && s.Idle && hostReady(s), cancellation);
            await CheckAsync("invalid-rejected", rejected, new { badFileSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(bad))).ToLowerInvariant() });
            await CheckAsync("recovered-hdr", await OpenReadyAsync(session, hdr, hostReady, cancellation));
            await session.SetAsync("pause", "no");
            var playing = await WaitAsync(session, s => !s.Paused && s.Position >= .7 && hostReady(s), cancellation);
            if (!await session.TogglePauseAsync()) throw new InvalidOperationException("Pause did not engage.");
            var paused = await session.ReadAsync();
            await Task.Delay(350, cancellation);
            var still = await session.ReadAsync();
            if (!still.Paused || Math.Abs(still.Position - paused.Position) > .1) throw new InvalidOperationException("Paused clock advanced.");
            await CheckAsync("pause", still, new { playing.Position, pausedPosition = paused.Position });
            if (await session.TogglePauseAsync()) throw new InvalidOperationException("Resume did not engage.");
            var resumed = await WaitAsync(session, s => !s.Paused && s.Position >= still.Position + .7 && hostReady(s), cancellation);
            if (resumed.LoadFailure.Length > 0) throw new InvalidOperationException("Recovered file still reports a load failure.");
            await CheckAsync("resume", resumed);
            status = "completed";
        }
        catch (OperationCanceledException error) { status = "cancelled"; failure = error.Message; throw; }
        catch (Exception error) { status = "failed"; failure = error.ToString(); throw; }
        finally { await SaveAsync(); }
    }
}

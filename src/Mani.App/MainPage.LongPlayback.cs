using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Mani_App.Diagnostics;

namespace Mani_App;

public sealed partial class MainPage
{
    // Observation cadence/fields follow IINA 1.5.1 InspectorWindowController.updateInfo(dynamic:).
    // AppKit/Mach code is not copied. Reuse and platform assessment: docs/m1/long-playback.md.
    private async void LongPlayback_Click(object sender, RoutedEventArgs e) => await RunProbeAsync(RunLongPlaybackAsync);

    private async Task RunLongPlaybackAsync(CancellationToken cancellation)
    {
        const int duration = 1800;
        var session = _session!;
        await _pollTask;
        _pendingSeek = null;
        _pendingVolume = null;
        var before = await session.ReadAsync();
        if (before.Idle || before.Loading || before.VideoFormat.Length == 0 || !File.Exists(before.Path) || before.Duration - before.Position < duration + 10)
        {
            LabResults.Text = "Open a local movie with at least 30 minutes and 10 seconds remaining.";
            return;
        }
        if (_web is not null || _blurEnabled || _hostCycleAttempts != 0 || _performanceProbeCount != 0)
        {
            LabResults.Text = "Restart Mani for this test. Keep WebView2 and GPU blur off; do not run other performance or rebuild probes first.";
            return;
        }
        var settings = await session.GetPropertiesAsync("speed", "ab-loop-a", "ab-loop-b", "loop-file", "loop-playlist", "aid", "vid", "sid", "secondary-sid", "vf", "glsl-shaders");
        if (settings["speed"] != "1.000000" && settings["speed"] != "1")
        {
            LabResults.Text = "Use normal playback speed (1x) for this test.";
            return;
        }
        if (new[] { "ab-loop-a", "ab-loop-b", "loop-file", "loop-playlist" }.Any(k => settings[k] != "no"))
        {
            LabResults.Text = "Turn off A-B, file and playlist loops before this test.";
            return;
        }
        _performanceProbeCount++;
        var run = NewProbeDirectory("long-playback");
        _longPlaybackReportPath = Path.Combine(run, "host-long-playback.json");
        var samples = new List<JsonElement>();
        int sampleCount = 0;
        ExternalResourceProbe? external = null;
        JsonElement? externalResources = null;
        string status = "running", mediaHash = "";
        string? failure = null, restoreFailure = null;
        object? displayAtStart = null, displayAtEnd = null;
        IReadOnlyDictionary<string, string>? settingsAtEnd = null;
        var startPosition = before.Position;
        var file = new FileInfo(before.Path);
        var mediaSize = file.Length;
        var mediaModifiedUtc = file.LastWriteTimeUtc;
        using var process = Process.GetCurrentProcess();
        await using var journal = new StreamWriter(new FileStream(Path.Combine(run, "engine-samples.jsonl"), FileMode.CreateNew, FileAccess.Write, FileShare.Read));
        async Task SaveAsync()
        {
            var report = new { schema_version = 3, mode = "WinUI long playback", baselineCase = "long-playback",
                automationId = _unattendedRunId, finishPolicy = _unattendedRunId is null ? "restore-pause" : "pause",
                requestedDurationSeconds = duration, atUtc = DateTimeOffset.UtcNow, status, failure, restoreFailure,
                mediaPath = before.Path, media_sha256 = mediaHash, mediaSize, mediaModifiedUtc, startPosition,
                mediaDuration = before.Duration, settings, settingsAtEnd, displayAtStart, displayAtEnd,
                hostProcessId = Environment.ProcessId, hostStartedAtUtc = process.StartTime.ToUniversalTime(),
                hostAssemblyId = typeof(MainPage).Module.ModuleVersionId,
                playbackAssemblyId = typeof(Mani.Playback.PlaybackSession).Module.ModuleVersionId,
                dll_sha256 = ProbeOptions("unused").DllSha256, priorHostCycles = _hostCycleAttempts,
                resourceSampler = ExternalResourceProbe.Profile, externalRequestId = external?.RequestId,
                hostCreationFileTime = external?.CreationFileTime, externalResources, samples,
                engineSampleIntervalSeconds = 1, forcedGc = false,
                limitations = "One continuous real-media observation, not leak certification. No seeks or synthetic looping. GPU utilization, audible quality and first DWM frame are not measured. Original pause state is restored; position is not rewound." };
            var path = Path.Combine(run, "host-long-playback.json");
            await File.WriteAllTextAsync(path + ".tmp", JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(path + ".tmp", path, overwrite: true);
        }
        try
        {
            await SaveAsync();
            await session.SetAsync("pause", "yes");
            LabResults.Text = "Hashing the movie before measurement...";
            await using (var stream = File.OpenRead(before.Path)) mediaHash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellation));
            LabResults.Text = "Waiting for collector. Run python scripts/run_m1_long_playback.py in the project folder.";
            external = await ExternalResourceProbe.PrepareAsync(run, "long-playback", cancellation, requestedDurationSeconds: duration, automationId: _unattendedRunId);
            displayAtStart = DisplayDetails();
            startPosition = (await session.ReadAsync()).Position;
            await SaveAsync();
            await session.SetAsync("pause", "no");
            await external.StartAsync();
            var clock = Stopwatch.StartNew();
            double lastSample = 0, lastHeartbeat = -5;
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                if (clock.Elapsed.TotalSeconds - lastHeartbeat >= 5)
                {
                    await external.CheckHeartbeatAsync(cancellation);
                    lastHeartbeat = clock.Elapsed.TotalSeconds;
                    LabResults.Text = $"Measuring continuous playback: {Time(clock.Elapsed.TotalSeconds)} / 30:00. Keep the window and display unchanged. Use Cancel running probe to stop.";
                }
                var state = await session.ReadAsync();
                if (state.Idle || state.Ended || state.Paused || state.Path != before.Path)
                    throw new InvalidOperationException("Playback stopped, paused or changed during the long test.");
                var info = await session.GetPropertiesAsync("total-avsync-change", "mistimed-frame-count", "display-fps", "estimated-vf-fps", "estimated-display-fps");
                var seconds = clock.Elapsed.TotalSeconds;
                if (seconds - lastSample > 5) throw new InvalidOperationException("Playback observation was interrupted for over five seconds, possibly by suspension.");
                lastSample = seconds;
                var sample = new { seconds, state.Position, state.DroppedFrames, state.HardwareDecoder,
                    state.AvSync, state.AudioOutput, state.AudioDevice, state.AudioBuffer, state.PausedForCache, state.Error,
                    info, panelWidth = VideoPanel.ActualWidth * VideoPanel.CompositionScaleX,
                    panelHeight = VideoPanel.ActualHeight * VideoPanel.CompositionScaleY,
                    display = DisplayDetails() };
                // Stream evidence instead of retaining a growing object graph in the measured process.
                sampleCount++;
                await journal.WriteLineAsync(JsonSerializer.Serialize(sample));
                if (sampleCount % 10 == 0) await journal.FlushAsync(cancellation);
                if (seconds >= duration) break;
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(1, duration - seconds)), cancellation);
            }
            await external.StopAsync();
            await session.SetAsync("pause", "yes");
            externalResources = await external.CompleteAsync(cancellation);
            file.Refresh();
            if (file.Length != mediaSize || file.LastWriteTimeUtc != mediaModifiedUtc)
                throw new IOException("Media file changed during the test.");
            status = "completed";
        }
        catch (OperationCanceledException) { status = "cancelled"; throw; }
        catch (Exception error) { status = "failed"; failure = error.ToString(); throw; }
        finally
        {
            if (external is not null && externalResources is null)
            {
                try { await external.CancelAsync(); }
                catch (Exception error) { failure ??= error.ToString(); status = "failed"; }
            }
            try
            {
                displayAtEnd = DisplayDetails();
                settingsAtEnd = await session.GetPropertiesAsync(settings.Keys.ToArray());
                if (!_closing) await session.SetAsync("pause", (_unattendedRunId is not null || before.Paused) ? "yes" : "no");
            }
            catch (Exception error) { restoreFailure = error.ToString(); status = "failed"; }
            await journal.FlushAsync();
            // Materialize the final report only after the external measurement interval has ended.
            try
            {
                using var reader = new StreamReader(new FileStream(Path.Combine(run, "engine-samples.jsonl"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
                while (await reader.ReadLineAsync() is { } line)
                {
                    using var document = JsonDocument.Parse(line);
                    samples.Add(document.RootElement.Clone());
                }
            }
            catch (Exception error) { failure ??= error.ToString(); status = "failed"; }
            await SaveAsync();
            if (!_closing && status == "completed") LabResults.Text = "30-minute playback saved. The collector terminal will summarize the evidence. Original pause state restored; playback position retained.";
        }
    }
}

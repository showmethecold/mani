using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Mani_App.Diagnostics;
using Microsoft.UI.Xaml;

namespace Mani_App;

public sealed partial class MainPage
{
    private async void DisplayChecks_Click(object sender, RoutedEventArgs e) => await RunProbeAsync(RunDisplayChecksAsync);

    private async Task RunDisplayChecksAsync(CancellationToken cancellation)
    {
        if (_web is not null || _blurEnabled)
            throw new InvalidOperationException("Turn off WebView2 and GPU blur before running display checks.");
        var session = _session!;
        await _pollTask;
        _pendingSeek = _pendingVolume = null;
        var configuration = await session.GetPropertiesAsync("ab-loop-a", "ab-loop-b", "loop-file", "loop-playlist",
            "vf", "glsl-shaders", "icc-profile", "icc-profile-auto", "target-lut");
        if (new[] { "ab-loop-a", "ab-loop-b", "loop-file", "loop-playlist" }.Any(k => configuration[k] != "no") ||
            new[] { "vf", "glsl-shaders", "icc-profile", "target-lut" }.Any(k => configuration[k].Length != 0) ||
            configuration["icc-profile-auto"] != "no")
            throw new InvalidOperationException("Turn off loops, filters, shaders and custom ICC/LUT processing before running display checks.");
        await SyncDisplayColorAsync();
        var previousForceSdr = await session.GetForceSdrOutputAsync();
        bool? restoredForceSdr = null;
        var previousTargets = await session.GetPropertiesAsync(DisplaySignalProbe.TargetOptions);
        IReadOnlyDictionary<string, string>? restoredTargets = null;
        var before = await session.ReadAsync();
        var displayBefore = DisplayDetails();
        var directory = NewProbeDirectory("display-checks");
        var stages = new List<object>();
        string status = "running";
        string? failure = null, restoreFailure = null, logCopyFailure = null;
        var logPath = Path.Combine(_logDirectory, "mpv.log");
        long LogLength()
        {
            using var log = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return log.Length;
        }
        var logStart = LogLength();
        long logEnd = logStart;
        async Task SaveAsync()
        {
            var report = new { schema_version = 2, suite = "visible-display-checks", status, failure, restoreFailure, logCopyFailure,
                atUtc = DateTimeOffset.UtcNow, hostProcessId = Environment.ProcessId,
                hostAssemblyId = typeof(MainPage).Module.ModuleVersionId,
                playbackAssemblyId = typeof(Mani.Playback.PlaybackSession).Module.ModuleVersionId,
                dllSha256 = ProbeOptions("unused").DllSha256, before.Path, before.Position,
                finishPolicy = "leave-test-video-paused", configuration, previousTargets, restoredTargets, previousForceSdr, restoredForceSdr,
                displayBefore, stages, logStart, logEnd,
                limitations = "Collection completion is not HDR acceptance. Read effective video-target-params and per-stage mpv logs; texture bit depth and requested target options alone do not prove HDR signaling, correct luminance or DWM presentation. No system HDR toggle or display transition is automated." };
            var path = Path.Combine(directory, "result.json");
            await File.WriteAllTextAsync(path + ".tmp", JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(path + ".tmp", path, overwrite: true);
        }
        try
        {
            await SaveAsync();
            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Assets", "display-media.json"), cancellation));
            var fixtures = new Dictionary<string, (string Path, string Hash)>();
            foreach (var key in new[] { "sdr", "hdr" })
            {
                var entry = manifest.RootElement.GetProperty(key);
                var path = Path.Combine(AppContext.BaseDirectory, "Assets", entry.GetProperty("file").GetString()!);
                await using var stream = File.OpenRead(path);
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellation));
                if (!hash.Equals(entry.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Display test video hash mismatch: " + key);
                fixtures[key] = (path, hash);
            }
            _notice = "";
            foreach (var stage in DisplaySignalProbe.Stages)
            {
                cancellation.ThrowIfCancellationRequested();
                LabResults.Text = "Collecting display evidence: " + stage.Id + ". Keep the window and display unchanged.";
                var fixture = fixtures[stage.Fixture];
                long stageLogStart = LogLength();
                var displayAtStart = DisplayDetails();
                var observations = new List<object>();
                string stageStatus = "running";
                string? stageFailure = null;
                long previousLoads = -1;
                try
                {
                    await session.SetForceSdrOutputAsync(stage.ForceSdr);
                    var previousRestarts = await session.GetPlaybackRestartCountAsync();
                    previousLoads = (await session.ReadAsync()).FileLoadedCount;
                    await session.OpenAsync(fixture.Path, paused: true);
                    var clock = Stopwatch.StartNew();
                    while (true)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        var readyCapture = await DisplaySignalProbe.CaptureAsync(session);
                        var state = readyCapture.State;
                        if (state.FileLoadedCount > previousLoads && state.ColorPrimaries.Length > 0 && state.Transfer.Length > 0 &&
                            UnattendedPlaybackReadiness.IsReady(state, fixture.Path,
                            _bridge?.HasAttachedSwapChain == true && VideoPanel.ActualWidth > 0 && VideoPanel.ActualHeight > 0,
                            await session.GetPlaybackRestartCountAsync() > previousRestarts) &&
                            DisplaySignalProbe.OutputMatchesPolicy(readyCapture)) break;
                        if (clock.Elapsed.TotalSeconds > 20)
                            throw new TimeoutException("Display fixture or attached video panel did not become ready. Last diagnostic: " + state.Error);
                        await Task.Delay(50, cancellation);
                    }
                    for (int i = 0; i < 3; i++)
                    {
                        await Task.Delay(250, cancellation);
                        await _pollTask;
                        var capture = await DisplaySignalProbe.CaptureAsync(session);
                        if (capture.State.LoadFailure.Length != 0 || capture.State.Path != fixture.Path || !capture.State.Paused ||
                            capture.State.Idle || capture.State.Loading || capture.State.Ended || capture.Swapchain is null ||
                            _bridge?.HasAttachedSwapChain != true || !DisplaySignalProbe.OutputMatchesPolicy(capture))
                            throw new InvalidOperationException("Display fixture lost its ready, paused video output.");
                        observations.Add(new { atUtc = DateTimeOffset.UtcNow, panelAttached = true,
                            panelWidth = VideoPanel.ActualWidth * VideoPanel.CompositionScaleX,
                            panelHeight = VideoPanel.ActualHeight * VideoPanel.CompositionScaleY,
                            display = DisplayDetails(), capture });
                    }
                    stageStatus = "completed";
                }
                catch (OperationCanceledException) { stageStatus = "cancelled"; throw; }
                catch (Exception error) { stageStatus = "failed"; stageFailure = error.ToString(); throw; }
                finally
                {
                    stages.Add(new { stage.Id, stage.Fixture, stage.ForceSdr, status = stageStatus, failure = stageFailure,
                        mediaPath = fixture.Path, mediaSha256 = fixture.Hash, fileLoadedBefore = previousLoads, displayAtStart, observations,
                        logStart = stageLogStart, logEnd = LogLength() });
                    await SaveAsync();
                }
            }
            status = "completed";
        }
        catch (OperationCanceledException) { status = "cancelled"; throw; }
        catch (Exception error) { status = "failed"; failure = error.ToString(); throw; }
        finally
        {
            // Restore the diagnostic override, then recompute automatic output for the video
            // deliberately left paused. Previous targets can belong to a different HDR video.
            try
            {
                await session.SetForceSdrOutputAsync(previousForceSdr);
                await session.SetAsync("pause", "yes");
                await session.ReadAsync();
                restoredForceSdr = await session.GetForceSdrOutputAsync();
                restoredTargets = await session.GetPropertiesAsync(DisplaySignalProbe.TargetOptions);
                if (restoredForceSdr != previousForceSdr)
                    throw new InvalidOperationException("Display output override was not restored.");
            }
            catch (Exception error) { restoreFailure ??= error.ToString(); status = "failed"; }
            try
            {
                await using var source = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                logEnd = source.Length;
                source.Position = logStart;
                await using var copy = File.Create(Path.Combine(directory, "mpv-display.log"));
                var buffer = new byte[65536];
                long remaining = logEnd - logStart;
                while (remaining > 0)
                {
                    int count = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)));
                    if (count == 0) throw new EndOfStreamException("The display log changed while copying.");
                    await copy.WriteAsync(buffer.AsMemory(0, count));
                    remaining -= count;
                }
            }
            catch (Exception error) { logCopyFailure = error.ToString(); status = "failed"; }
            await SaveAsync();
            if (!_closing && status == "completed")
                LabResults.Text = "SDR / HDR evidence saved. The test video is paused. Run python scripts/analyze_m1_display.py to summarize. Visual HDR acceptance is still required.";
        }
    }
}

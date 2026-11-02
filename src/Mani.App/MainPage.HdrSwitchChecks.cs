using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Mani.Playback;
using Mani_App.Diagnostics;
using Microsoft.UI.Xaml;

namespace Mani_App;

public sealed partial class MainPage
{
    private async void HdrSwitchChecks_Click(object sender, RoutedEventArgs e)
    {
        bool playing = (sender as FrameworkElement)?.Tag as string == "playing";
        await RunProbeAsync(async cancellation =>
        {
            try { await RunHdrSwitchChecksAsync(cancellation, playing); }
            catch (OperationCanceledException)
            {
                if (!_closing) HdrSwitchStatus.Text = "Cancelled. Restore Windows HDR if you turned it off. Partial evidence was kept.";
                throw;
            }
            catch (Exception error)
            {
                if (!_closing) HdrSwitchStatus.Text = error.Message + " Restore Windows HDR if needed.";
                throw;
            }
        });
    }

    private JsonElement ReadSwitchDisplay() => JsonSerializer.SerializeToElement(DisplayDetails());
    private static string DisplayText(JsonElement display, string key) =>
        display.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";

    private async Task RunHdrSwitchChecksAsync(CancellationToken cancellation, bool playing)
    {
        if (_web is not null || _blurEnabled)
            throw new InvalidOperationException("Turn off WebView2 and GPU blur before this check.");
        var session = _session!;
        await _pollTask;
        _pendingSeek = _pendingVolume = null;
        var displayBefore = ReadSwitchDisplay();
        string monitor = DisplayText(displayBefore, "monitor");
        if (monitor.Length == 0 || DisplayText(displayBefore, "kind") != "HighDynamicRange")
            throw new InvalidOperationException("Start with Windows HDR enabled on the display containing Mani.");
        var previousForceSdr = await session.GetForceSdrOutputAsync();
        if (previousForceSdr) throw new InvalidOperationException("Disable the forced SDR test override first.");
        var configuration = await session.GetPropertiesAsync("ab-loop-a", "ab-loop-b", "loop-file", "loop-playlist",
            "vf", "glsl-shaders", "icc-profile", "icc-profile-auto", "target-lut", "speed", "mute", "volume", "audio-device");
        if (new[] { "ab-loop-a", "ab-loop-b", "loop-file", "loop-playlist", "icc-profile-auto" }.Any(k => configuration[k] != "no") ||
            new[] { "vf", "glsl-shaders", "icc-profile", "target-lut" }.Any(k => configuration[k].Length != 0))
            throw new InvalidOperationException("Turn off loops, filters, shaders and custom ICC/LUT processing first.");

        var directory = NewProbeDirectory("hdr-switch-checks");
        var logPath = Path.Combine(_logDirectory, "mpv.log");
        long LogLength()
        {
            using var log = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return log.Length;
        }
        long logStart = LogLength(), logEnd = logStart;
        string status = "running", mediaPath = "", mediaSha256 = "";
        string? failure = null, cleanupFailure = null, logCopyFailure = null;
        var stages = new List<object>();
        var timeline = new List<object>();
        var before = await session.ReadAsync();
        long fileLoadedBefore = before.FileLoadedCount;
        long? readyFileLoadedCount = null;
        JsonElement? displayAfter = null;
        bool? finalForceSdr = null, finalPaused = null;
        int sampleId = 0;
        int stableSamplesRequired = playing ? 12 : 3;
        IReadOnlyDictionary<string, string>? audioSettingsAfter = null;
        var whole = Stopwatch.StartNew();
        async Task SaveAsync()
        {
            var report = new { schema_version = playing ? 2 : 1, suite = "visible-hdr-switch-checks",
                playbackMode = playing ? "playing" : "paused", stableSamplesRequired, audioSettingsAfter, status, failure, cleanupFailure, logCopyFailure,
                atUtc = DateTimeOffset.UtcNow, hostProcessId = Environment.ProcessId,
                hostAssemblyId = typeof(MainPage).Module.ModuleVersionId,
                playbackAssemblyId = typeof(PlaybackSession).Module.ModuleVersionId, dllSha256 = ProbeOptions("unused").DllSha256,
                configuration, displayBefore, displayAfter, previousForceSdr, finalForceSdr, finalPaused,
                mediaPath, mediaSha256, fileLoadedBefore, readyFileLoadedCount, beforePath = before.Path,
                finishPolicy = "leave-hdr-fixture-paused-system-settings-user-controlled", stages, timeline, logStart, logEnd,
                limitations = "Observed system HDR modes and effective output only. No calibrated luminance, DWM timing, visual, audible continuity, or cross-monitor acceptance. The app never changes Windows HDR settings." };
            var path = Path.Combine(directory, "result.json");
            await File.WriteAllTextAsync(path + ".tmp", JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(path + ".tmp", path, overwrite: true);
        }
        try
        {
            await SaveAsync();
            if (playing && (configuration["mute"] != "no" || before.Volume <= 0 ||
                !double.TryParse(configuration["speed"], System.Globalization.CultureInfo.InvariantCulture, out var speed) || speed != 1))
                throw new InvalidOperationException("Unmute playback, set a comfortable nonzero volume and use normal playback speed before this check.");
            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Assets", "display-media.json"), cancellation));
            var fixture = manifest.RootElement.GetProperty(playing ? "hdr-av" : "hdr");
            mediaPath = Path.Combine(AppContext.BaseDirectory, "Assets", fixture.GetProperty("file").GetString()!);
            await using (var input = File.OpenRead(mediaPath))
                mediaSha256 = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellation));
            if (!mediaSha256.Equals(fixture.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("HDR test video hash mismatch.");
            _notice = "";
            long previousRestarts = await session.GetPlaybackRestartCountAsync();
            await session.OpenAsync(mediaPath, paused: !playing);
            foreach (var (id, hdr, prompt, timeoutSeconds) in new[] {
                ("hdr-initial", true, playing ? "Preparing moving HDR bars and a quiet steady tone. Keep Windows HDR on. Watch and listen for interruptions during the switches." : "Preparing the HDR sample. Keep Windows HDR on and leave this window on the same display.", 20),
                ("hdr-off", false, "Turn Windows HDR OFF for this display. Keep Mani visible on the same display; do not resize it. Waiting for SDR output...", 180),
                ("hdr-restored", true, "Turn Windows HDR back ON for this display. Keep Mani visible on the same display; do not resize it. Waiting for HDR output...", 180) })
            {
                HdrSwitchStatus.Text = prompt;
                LabResults.Text = prompt;
                long stageLogStart = LogLength();
                var observations = new List<object>();
                string stageStatus = "running";
                string? stageFailure = null;
                var clock = Stopwatch.StartNew();
                long lastSave = 0;
                try
                {
                    while (true)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        await Task.Delay(250, cancellation);
                        await _pollTask; // Observe the normal host policy; never inject a display mode here.
                        if (_web is not null || _blurEnabled) throw new InvalidOperationException("WebView2 or GPU blur was enabled during the check.");
                        JsonElement? display = null;
                        string? queryError = null;
                        try { display = ReadSwitchDisplay(); }
                        catch (Exception error) { queryError = error.Message; }
                        string currentMonitor = display is JsonElement d ? DisplayText(d, "monitor") : "";
                        string kind = display is JsonElement d2 ? DisplayText(d2, "kind") : "";
                        if (currentMonitor.Length > 0 && currentMonitor != monitor)
                            throw new InvalidOperationException("The window moved to a different display. Run this check on one display.");
                        var capture = await DisplaySignalProbe.CaptureAsync(session);
                        var state = capture.State;
                        if (state.LoadFailure.Length > 0) throw new IOException(state.LoadFailure);
                        if (readyFileLoadedCount is long loaded && state.FileLoadedCount != loaded)
                            throw new InvalidOperationException("The test video was reloaded during the HDR switch.");
                        bool attached = _bridge?.HasAttachedSwapChain == true && VideoPanel.ActualWidth > 0 && VideoPanel.ActualHeight > 0;
                        bool ready = state.FileLoadedCount > fileLoadedBefore && state.ColorPrimaries == "bt.2020" && state.Transfer == "pq" &&
                            UnattendedPlaybackReadiness.IsReady(state, mediaPath, attached,
                                await session.GetPlaybackRestartCountAsync() > previousRestarts, expectedPaused: !playing);
                        bool modeMatches = hdr ? kind == "HighDynamicRange" : kind is "StandardDynamicRange" or "WideColorGamut";
                        bool stable = ready && currentMonitor == monitor && modeMatches && state.ColorOutput?.Hdr == hdr &&
                            capture.Swapchain is not null && DisplaySignalProbe.OutputMatchesPolicy(capture) &&
                            (!playing || (!state.PausedForCache && state.AudioOutput.Length > 0 && state.AudioOutput != "null"));
                        int index = ++sampleId;
                        long sampleElapsedMs = whole.ElapsedMilliseconds;
                        var sampleTime = DateTimeOffset.UtcNow;
                        timeline.Add(new { sampleId = index, stage = id, atUtc = sampleTime, elapsedMs = sampleElapsedMs,
                            display, queryError, ready, stable, state.ColorOutput, state.Paused, state.FileLoadedCount,
                            state.Position, state.Duration, state.AudioOutput, state.AudioDevice, state.AudioBuffer,
                            state.AvSync, state.PausedForCache, state.DroppedFrames, state.HardwareDecoder, state.Error, state.LoadFailure, state.Volume,
                            target = capture.Engine["video-target-params"] });
                        if (stable)
                        {
                            readyFileLoadedCount ??= state.FileLoadedCount;
                            observations.Add(new { sampleId = index, atUtc = sampleTime,
                                elapsedMs = sampleElapsedMs, panelAttached = attached,
                                panelWidth = VideoPanel.ActualWidth * VideoPanel.CompositionScaleX,
                                panelHeight = VideoPanel.ActualHeight * VideoPanel.CompositionScaleY, display, capture });
                        }
                        else observations.Clear(); // Require consecutive, settled samples, not one transient match.
                        if (observations.Count == stableSamplesRequired) { stageStatus = "completed"; break; }
                        if (clock.Elapsed.TotalSeconds > timeoutSeconds)
                            throw new TimeoutException("Timed out waiting for " + id + ". Partial evidence was saved.");
                        if (clock.ElapsedMilliseconds - lastSave >= 2000)
                        {
                            lastSave = clock.ElapsedMilliseconds;
                            await SaveAsync();
                        }
                    }
                }
                catch (OperationCanceledException) { stageStatus = "cancelled"; throw; }
                catch (Exception error) { stageStatus = "failed"; stageFailure = error.ToString(); throw; }
                finally
                {
                    stages.Add(new { Id = id, expectedHdr = hdr, status = stageStatus, failure = stageFailure,
                        observations, logStart = stageLogStart, logEnd = LogLength() });
                    await SaveAsync();
                }
            }
            status = "completed";
        }
        catch (OperationCanceledException) { status = "cancelled"; throw; }
        catch (Exception error) { status = "failed"; failure = error.ToString(); throw; }
        finally
        {
            try
            {
                await session.SetAsync("pause", "yes");
                finalPaused = (await session.ReadAsync()).Paused;
                finalForceSdr = await session.GetForceSdrOutputAsync();
                audioSettingsAfter = await session.GetPropertiesAsync("mute", "volume", "speed", "audio-device");
                if (playing && audioSettingsAfter.Any(kv => configuration[kv.Key] != kv.Value))
                    throw new InvalidOperationException("Audio settings or playback speed changed during the check.");
                displayAfter = ReadSwitchDisplay();
                if (finalForceSdr != previousForceSdr || finalPaused != true)
                    throw new InvalidOperationException("Playback cleanup did not preserve automatic paused output.");
                if (status == "completed" && (DisplayText(displayAfter.Value, "kind") != "HighDynamicRange" ||
                    DisplayText(displayAfter.Value, "monitor") != monitor))
                    throw new InvalidOperationException("Windows HDR was not restored on the original display.");
            }
            catch (Exception error) { cleanupFailure = error.ToString(); status = "failed"; }
            try
            {
                await using var source = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                logEnd = source.Length;
                source.Position = logStart;
                await using var copy = File.Create(Path.Combine(directory, "mpv-hdr-switch.log"));
                byte[] buffer = new byte[65536];
                for (long remaining = logEnd - logStart; remaining > 0;)
                {
                    int count = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)));
                    if (count == 0) throw new EndOfStreamException("The HDR switch log changed while copying.");
                    await copy.WriteAsync(buffer.AsMemory(0, count));
                    remaining -= count;
                }
            }
            catch (Exception error) { logCopyFailure = error.ToString(); status = "failed"; }
            await SaveAsync();
            if (!_closing)
            {
                HdrSwitchStatus.Text = status == "completed"
                    ? "HDR switch evidence saved. Windows HDR is back on; the sample is paused. Visual checks remain separate."
                    : "Check incomplete. Partial evidence saved. Restore Windows HDR if you turned it off.";
                LabResults.Text = HdrSwitchStatus.Text;
            }
        }
    }
}

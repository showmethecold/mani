using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Mani.Playback;
using Mani_App.Diagnostics;
using Microsoft.UI.Xaml;

namespace Mani_App;

// Diagnostic-only evidence. Source/platform assessment: docs/m1/interaction-validation.md.
public sealed partial class MainPage
{
    private async void PrepareInteraction_Click(object sender, RoutedEventArgs e) => await RunProbeAsync(async cancellation =>
    {
        var session = _session!;
        bool openedMedia = false, openedWeb = false, hadBlur = _blurEnabled;
        if (!_closing) InteractionStatus.Text = "Preparing the test video and overlays...";
        try
        {
            await _pollTask;
            // Apply the user's last volume choice; never silently unmute or change listening level/speed.
            if (_pendingVolume is double volume)
            {
                _pendingVolume = null;
                await session.SetAsync("volume", volume.ToString(CultureInfo.InvariantCulture));
            }
            var settings = await session.GetPropertiesAsync("volume", "mute", "speed", "audio-device");
            if (settings["mute"] != "no" || !double.TryParse(settings["volume"], NumberStyles.Float,
                CultureInfo.InvariantCulture, out var level) || level <= 0)
            {
                InteractionStatus.Text = "Unmute and choose a comfortable volume, then click Prepare interaction checks again.";
                return;
            }
            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(
                Path.Combine(AppContext.BaseDirectory, "Assets", "display-media.json"), cancellation));
            var fixture = manifest.RootElement.GetProperty("hdr-av");
            string mediaPath = Path.Combine(AppContext.BaseDirectory, "Assets", fixture.GetProperty("file").GetString()!);
            string mediaSha256;
            await using (var input = File.OpenRead(mediaPath))
                mediaSha256 = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellation));
            if (!mediaSha256.Equals(fixture.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Interaction test video hash mismatch.");
            long loadedBefore = (await session.ReadAsync()).FileLoadedCount;
            long restartsBefore = await session.GetPlaybackRestartCountAsync();
            _notice = ""; _pendingSeek = null; _seeking = false; _adjustingVolume = false;
            cancellation.ThrowIfCancellationRequested();
            openedMedia = true;
            await session.OpenAsync(mediaPath, paused: false);
            var clock = Stopwatch.StartNew();
            DisplaySignalProbe.Capture readyCapture;
            while (true)
            {
                await Task.Delay(200, cancellation);
                await _pollTask; // Keep the normal poll/color policy and panel attachment in control.
                readyCapture = await DisplaySignalProbe.CaptureAsync(session);
                var state = readyCapture.State;
                bool ready = UnattendedPlaybackReadiness.IsReady(state, mediaPath,
                    _bridge?.HasAttachedSwapChain == true,
                    await session.GetPlaybackRestartCountAsync() > restartsBefore, expectedPaused: false);
                if (ready && state.FileLoadedCount > loadedBefore && state.ColorPrimaries == "bt.2020" &&
                    state.Transfer == "pq" && DisplaySignalProbe.OutputMatchesPolicy(readyCapture) &&
                    !state.PausedForCache && state.AudioOutput.Length > 0 && state.AudioOutput != "null") break;
                if (clock.Elapsed.TotalSeconds > 20)
                    throw new TimeoutException("The test video did not become ready within 20 seconds.");
            }
            if (_web is null)
            {
                openedWeb = true;
                await OpenWebProbeAsync(cancellation);
            }
            if (!_webReady) throw new InvalidOperationException("Close the WebView2 overlay and try Prepare interaction checks again.");
            await SetBlurEnabledAsync(true, cancellation);
            cancellation.ThrowIfCancellationRequested();
            Trace("interaction-prepared", new { mediaPath, mediaSha256, settings, capture = readyCapture,
                webGeneration = _webGeneration, webReady = _webReady, blurEnabled = _blurEnabled,
                manualInputAccepted = false });
            if (!_closing) InteractionStatus.Text = "Ready. Type with your IME in the web input, commit the text, then click Save interaction snapshot. Check the blur behind the controls. Keep both overlays open.";
        }
        catch (Exception error)
        {
            // Only undo this preparation's additions; never leave the test audio playing after failure.
            if (openedMedia)
                try { await session.SetAsync("pause", "yes"); }
                catch (Exception cleanup) { Trace("interaction-cleanup-error", new { error = cleanup.ToString() }); }
            if (!hadBlur && _blurEnabled)
                try { await SetBlurEnabledAsync(false, CancellationToken.None); }
                catch (Exception cleanup) { Trace("interaction-cleanup-error", new { error = cleanup.ToString() }); }
            if (openedWeb) { CloseWebProbe(); await _webCommandTask; }
            if (!_closing) InteractionStatus.Text = error is OperationCanceledException
                ? "Preparation cancelled. Click Prepare interaction checks to try again."
                : "Preparation failed: " + error.Message;
            throw;
        }
    });

    private async void InteractionSnapshot_Click(object sender, RoutedEventArgs e) => await RunProbeAsync(async cancellation =>
    {
        var directory = NewProbeDirectory("interaction-snapshot");
        string status = "failed";
        string? failure = null;
        bool mediaReady = false;
        var missing = new List<string>();
        DisplaySignalProbe.Capture? capture = null;
        JsonElement? display = null, web = null;
        object? panel = null, controls = null;
        IReadOnlyDictionary<string, string>? properties = null;
        string? mediaSha256 = null;
        long? mpvLogEnd = null, hostLogEnd = null;
        var audioSettings = new[] { "pause", "volume", "mute", "speed", "audio-device" };
        int webGeneration = _webGeneration;
        bool webOpen = _web is not null, webReady = _webReady, blurEnabled = _blurEnabled;
        string? appSha256 = null, webAssetSha256 = null, shaderSha256 = null;
        async Task<string> HashAsync(string path)
        {
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return Convert.ToHexString(await SHA256.HashDataAsync(file, cancellation));
        }
        async Task<long> CopyLogAsync(string name)
        {
            await using var source = new FileStream(Path.Combine(_logDirectory, name), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            long end = source.Length;
            await using var target = File.Create(Path.Combine(directory, name));
            byte[] buffer = new byte[65536];
            for (long remaining = end; remaining > 0;)
            {
                int count = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(remaining, buffer.Length)));
                if (count == 0) throw new EndOfStreamException("Diagnostic log changed while copying.");
                await target.WriteAsync(buffer.AsMemory(0, count)); remaining -= count;
            }
            return end;
        }
        try
        {
            // Browser evidence is independent of media readiness; preserve it even on an idle player.
            if (webOpen && (!webReady || _web?.CoreWebView2 is null))
                missing.Add("WebView2 is not ready. Wait for its input field, then save again.");
            if (webReady && _web?.CoreWebView2 is { } core)
            {
                // Export counters and known player state only; never export input.value or the session token.
                string json = await core.ExecuteScriptAsync("({input:window.maniProbe?.input ?? null, state:window.maniProbe?.state ?? null, response:window.maniProbe?.response ?? null, sent:window.maniProbe?.sent ?? 0, lastSendError:window.maniProbe?.lastSendError ?? null, inputFocused:document.activeElement===document.getElementById('focus-test')})").AsTask(cancellation);
                using var result = JsonDocument.Parse(json);
                web = result.RootElement.Clone();
                if (web.Value.ValueKind != JsonValueKind.Object || !web.Value.TryGetProperty("input", out var counters) || counters.ValueKind != JsonValueKind.Object)
                    throw new InvalidOperationException("Web input counters are unavailable; restart the updated app.");
            }
            await _pollTask;
            capture = await DisplaySignalProbe.CaptureAsync(_session!);
            mediaReady = !capture.State.Idle && !capture.State.Loading && !capture.State.Ended &&
                capture.State.Path.Length > 0 && capture.State.VideoFormat.Length > 0 && capture.State.Duration > 0 &&
                _bridge?.HasAttachedSwapChain == true;
            if (!mediaReady) missing.Add("Video is not ready. Click Prepare interaction checks, then save again.");
            display = ReadSwitchDisplay();
            properties = await _session!.GetPropertiesAsync(audioSettings.Concat(new[] {
                "glsl-shaders", "glsl-shader-opts", "osd-dimensions", "ab-loop-a", "ab-loop-b", "track-list",
                "sid", "secondary-sid", "sub-visibility", "secondary-sub-visibility", "sub-delay", "secondary-sub-delay",
                "sub-pos", "secondary-sub-pos", "sub-ass-override", "secondary-sub-ass-override" }).ToArray());
            panel = new { dipWidth = VideoPanel.ActualWidth, dipHeight = VideoPanel.ActualHeight,
                scaleX = VideoPanel.CompositionScaleX, scaleY = VideoPanel.CompositionScaleY,
                pixelWidth = VideoPanel.ActualWidth * VideoPanel.CompositionScaleX,
                pixelHeight = VideoPanel.ActualHeight * VideoPanel.CompositionScaleY,
                attached = _bridge?.HasAttachedSwapChain == true };
            var bounds = Controls.TransformToVisual(VideoPanel).TransformBounds(
                new Windows.Foundation.Rect(0, 0, Controls.ActualWidth, Controls.ActualHeight));
            controls = new { bounds.X, bounds.Y, bounds.Width, bounds.Height,
                pixelX = bounds.X * VideoPanel.CompositionScaleX, pixelY = bounds.Y * VideoPanel.CompositionScaleY,
                pixelWidth = bounds.Width * VideoPanel.CompositionScaleX, pixelHeight = bounds.Height * VideoPanel.CompositionScaleY };
            if (File.Exists(capture.State.Path)) mediaSha256 = await HashAsync(capture.State.Path);
            appSha256 = await HashAsync(Path.Combine(AppContext.BaseDirectory, "Mani.App.dll"));
            webAssetSha256 = await HashAsync(Path.Combine(AppContext.BaseDirectory, "Assets", "m1-web-probe.html"));
            shaderSha256 = await HashAsync(Path.Combine(AppContext.BaseDirectory, "Assets", "m1-blur.glsl"));
            if (webGeneration != _webGeneration || webOpen != (_web is not null) || blurEnabled != _blurEnabled)
                throw new InvalidOperationException("Overlay state changed while saving the snapshot.");
            status = missing.Count == 0 ? "captured" : "partial";
        }
        catch (OperationCanceledException) { status = "cancelled"; failure = "Snapshot collection cancelled."; throw; }
        catch (Exception error) { failure = error.ToString(); throw; }
        finally
        {
            try { mpvLogEnd = await CopyLogAsync("mpv.log"); hostLogEnd = await CopyLogAsync("host-state.jsonl"); }
            catch (Exception error) { status = "failed"; failure ??= error.ToString(); }
            var report = new { schema_version = 2, suite = "manual-interaction-snapshot", status, failure, mediaReady, missing,
                atUtc = DateTimeOffset.UtcNow, hostProcessId = Environment.ProcessId,
                hostAssemblyId = typeof(MainPage).Module.ModuleVersionId, playbackAssemblyId = typeof(PlaybackSession).Module.ModuleVersionId,
                appSha256, dllSha256 = ProbeOptions("unused").DllSha256, mediaSha256, webAssetSha256, shaderSha256,
                display, panel, controls, capture, properties, webOpen, webReady, webGeneration, webVersion = _webVersion, web,
                blurEnabled, blurStrength = _blurStrength, blurParameters = _blurParameters,
                logSourceDirectory = _logDirectory, logStart = 0, mpvLogEnd, hostLogEnd,
                visualAccepted = false, physicalInputAccepted = false, imeAccepted = false,
                limitations = "Point-in-time evidence and cumulative session log prefixes, not an automated visual/input pass. Input counters contain no text; isTrusted distinguishes browser events from dispatchEvent but does not prove human input. The snapshot button briefly disables controls only during capture, not during manual actions." };
            await File.WriteAllTextAsync(Path.Combine(directory, "result.json"),
                JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Trace("interaction-snapshot", new { directory, status, webOpen, blurEnabled });
            if (!_closing)
            {
                InteractionStatus.Text = status switch
                {
                    "captured" => $"Interaction snapshot saved. WebView2: {(webOpen ? "on" : "off")}; GPU blur: {(blurEnabled ? "on" : "off")}. You can pause the video now.",
                    "partial" => "Partial snapshot saved, including available web input counters. " + string.Join(" ", missing),
                    "cancelled" => "Snapshot cancelled. Partial evidence was saved; try again when ready.",
                    _ => "Snapshot incomplete. Available evidence was saved. " + (failure?.Split('\n')[0] ?? "See the saved report for details.")
                };
                if (web is { ValueKind: JsonValueKind.Object } webState &&
                    webState.TryGetProperty("input", out var inputState) &&
                    inputState.TryGetProperty("committedCompositions", out var commits) && commits.TryGetInt32(out int count))
                    // A missing composition event is a telemetry gap, not proof that IME input failed.
                    InteractionStatus.Text += count > 0 ? $" Composition commits recorded: {count}. Confirm IME behavior manually."
                        : " No composition commits recorded. Confirm IME candidates and committed text manually; a zero count does not mean input failed.";
                LabResults.Text = InteractionStatus.Text;
            }
        }
    });
}

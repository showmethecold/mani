using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Mani.Playback;
using Mani_App.Diagnostics;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace Mani_App;

// Reuse assessment and acceptance boundaries: docs/m1/overlay-matrix.md.
public sealed partial class MainPage
{
    private sealed record OverlayMatrixMedia(string Path, string Sha256, long Bytes, DateTime ModifiedUtc,
        int Width, int Height, double Duration, double StartPosition, int AudioId);

    private async void OverlayMatrix_Click(object sender, RoutedEventArgs e) =>
        await RunProbeAsync(cancellation => RunOverlayMatrixAsync(cancellation, false));

    private async void CurrentVideoOverlay_Click(object sender, RoutedEventArgs e) =>
        await RunProbeAsync(cancellation => RunOverlayMatrixAsync(cancellation, true));

    private async void HdrOverlay_Click(object sender, RoutedEventArgs e) =>
        await RunProbeAsync(cancellation => RunOverlayMatrixAsync(cancellation, false, hdrFixture: true));

    private async Task RunOverlayMatrixAsync(CancellationToken cancellation, bool currentVideo, bool hdrFixture = false, string? outputDirectory = null)
    {
        if (Owner?.AppWindow.Presenter is not OverlappedPresenter presenter || presenter.State == OverlappedPresenterState.Minimized)
            throw new InvalidOperationException("Start in a visible normal or maximized window.");
        if (_web is not null || _blurEnabled) throw new InvalidOperationException("Close WebView2 and GPU blur before starting.");
        var session = _session!;
        await _pollTask;
        _pendingSeek = _pendingVolume = null;
        var config = await session.GetPropertiesAsync("ab-loop-a", "ab-loop-b", "loop-file", "loop-playlist", "vf",
            "glsl-shaders", "glsl-shader-opts", "icc-profile", "icc-profile-auto", "target-lut", "speed", "volume", "mute", "audio-device");
        if (new[] { "ab-loop-a", "ab-loop-b", "loop-file", "loop-playlist", "icc-profile-auto" }.Any(k => config[k] != "no") ||
            new[] { "vf", "glsl-shaders", "icc-profile", "target-lut" }.Any(k => config[k].Length != 0) || config["speed"] != "1.000000" ||
            await session.GetForceSdrOutputAsync())
            throw new InvalidOperationException("Use normal speed and automatic output, without loops, filters or custom color processing.");
        if (hdrFixture && (config["mute"] != "no" || !double.TryParse(config["volume"], CultureInfo.InvariantCulture, out var hdrVolume) || hdrVolume <= 0))
            throw new InvalidOperationException("Unmute the player and use an audible volume before starting.");
        OverlayMatrixMedia? realMedia = null;
        int stableSamples = currentVideo ? 40 : 12;
        if (currentVideo)
        {
            var initial = await session.ReadAsync();
            if (initial.Idle || initial.Loading || !File.Exists(initial.Path) || !double.IsFinite(initial.Duration) || initial.Duration < 30)
                throw new InvalidOperationException("Open a local SDR video with audio and at least 30 seconds of duration first.");
            if (config["mute"] != "no" || !double.TryParse(config["volume"], CultureInfo.InvariantCulture, out var volume) || volume <= 0)
                throw new InvalidOperationException("Unmute the player and use an audible volume before starting.");
            var properties = await session.GetPropertiesAsync("video-params", "track-list");
            using var video = JsonDocument.Parse(properties["video-params"]);
            var v = video.RootElement;
            string? transfer = v.GetProperty("gamma").GetString();
            if (v.GetProperty("primaries").GetString() != "bt.709" ||
                transfer is not ("bt.709" or "srgb" or "bt.1886" or "gamma1.8" or "gamma2.0" or "gamma2.2" or "gamma2.4" or "gamma2.6" or "gamma2.8"))
                throw new InvalidOperationException("This check requires a BT.709 SDR source. Use the separate HDR checks for HDR media.");
            using var tracks = JsonDocument.Parse(properties["track-list"]);
            var audio = tracks.RootElement.EnumerateArray().FirstOrDefault(t => t.GetProperty("type").GetString() == "audio" &&
                t.TryGetProperty("selected", out var selected) && selected.GetBoolean());
            if (audio.ValueKind == JsonValueKind.Undefined)
                throw new InvalidOperationException("Select an audio track before starting.");
            var file = new FileInfo(initial.Path);
            realMedia = new(file.FullName, "", file.Length, file.LastWriteTimeUtc, v.GetProperty("w").GetInt32(),
                v.GetProperty("h").GetInt32(), initial.Duration, Math.Clamp(initial.Position, 0, initial.Duration - 25), audio.GetProperty("id").GetInt32());
        }
        var displayBefore = ReadSwitchDisplay();
        if (DisplayText(displayBefore, "monitor").Length == 0 || DisplayText(displayBefore, "kind").Length == 0)
            throw new InvalidOperationException("Display information is unavailable.");
        if (hdrFixture && DisplayText(displayBefore, "kind") != "HighDynamicRange")
            throw new InvalidOperationException("Enable Windows HDR on this display before running HDR overlay checks.");
        var initialWindow = ReadCheckWindow();
        string directory = outputDirectory ?? NewProbeDirectory("overlay-matrix"), log = Path.Combine(_logDirectory, "mpv.log");
        Directory.CreateDirectory(directory);
        long LogLength() { using var f = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); return f.Length; }
        long logStart = LogLength(), logEnd = logStart;
        string status = "running"; string? failure = null, cleanupFailure = null, logCopyFailure = null;
        bool? finalPaused = null, finalForceSdr = null;
        ColorOutputTarget? finalColorOutput = null;
        IReadOnlyDictionary<string, string>? finalSettings = null;
        WindowCheckState? finalWindow = null; JsonElement? displayAfter = null;
        var stages = new List<object>();
        var oldLab = LabPanel.Visibility; var oldInfo = InfoPanel.Visibility;
        bool oldHitTest = WebProbeContainer.IsHitTestVisible;
        async Task<string> HashAsync(string path)
        {
            await using var file = File.OpenRead(path);
            return Convert.ToHexString(await SHA256.HashDataAsync(file, cancellation));
        }
        string appSha256 = await HashAsync(Path.Combine(AppContext.BaseDirectory, "Mani.App.dll"));
        string webSha256 = await HashAsync(Path.Combine(AppContext.BaseDirectory, "Assets", "m1-web-probe.html"));
        string shaderPath = Path.Combine(AppContext.BaseDirectory, "Assets", "m1-blur.glsl");
        string shaderSha256 = await HashAsync(shaderPath);
        async Task SaveAsync()
        {
            var report = new { schema_version = hdrFixture ? 3 : currentVideo ? 2 : 1, suite = "visible-overlay-matrix", inputKind = hdrFixture ? "hdr-fixture" : currentVideo ? "current-video" : "fixed-fixtures", realMedia, shaderPath, status, failure, cleanupFailure, logCopyFailure,
                atUtc = DateTimeOffset.UtcNow, hostProcessId = Environment.ProcessId,
                hostAssemblyId = typeof(MainPage).Module.ModuleVersionId, playbackAssemblyId = typeof(PlaybackSession).Module.ModuleVersionId,
                dllSha256 = ProbeOptions("unused").DllSha256, appSha256, webSha256, shaderSha256,
                configuration = config, finalSettings, displayBefore, displayAfter, initialWindow, finalWindow,
                finalPaused, finalForceSdr, finalColorOutput, finalWebOpen = _web is not null, finalBlurEnabled = _blurEnabled,
                finalWebChildren = WebProbeContainer.Children.Count, webVersion = _webVersion,
                stages, logStart, logEnd, stableSamplesRequired = stableSamples,
                visualAccepted = false, physicalInputAccepted = false, audioAccepted = false,
                finishPolicy = "leave-last-media-paused-overlays-off",
                limitations = (hdrFixture ? "Synthetic 640x360 PQ source with audio; HDR/forced-SDR/restored-HDR groups. No calibrated luminance or full HDR visual acceptance. " : currentVideo ? "Current local SDR video with audio; actual decoded resolution, no 4K inference. " : "Silent synthetic SDR sources at 1080p/4K. ") + "Current display/window only. Scripted DOM focus/buttons are not physical input. PNGs exclude WinUI/WebView2/DWM and are not calibrated HDR. No performance or audio continuity acceptance." };
            string path = Path.Combine(directory, "result.json");
            await File.WriteAllTextAsync(path + ".tmp", JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(path + ".tmp", path, overwrite: true);
        }
        async Task<DisplaySignalProbe.Capture> ReadyAsync(string path, long loaded, bool paused)
        {
            var wait = Stopwatch.StartNew();
            while (true)
            {
                cancellation.ThrowIfCancellationRequested(); await Task.Delay(100, cancellation); await _pollTask;
                var c = await DisplaySignalProbe.CaptureAsync(session); var s = c.State;
                if (s.LoadFailure.Length > 0) throw new IOException(s.LoadFailure);
                if (s.Path == path && s.FileLoadedCount == loaded && !s.Loading && !s.Idle && !s.Ended && s.Paused == paused &&
                    s.VideoFormat.Length > 0 && _bridge?.HasAttachedSwapChain == true && DisplaySignalProbe.OutputMatchesPolicy(c)) return c;
                if (wait.Elapsed.TotalSeconds > 20) throw new TimeoutException("Video/output did not settle for the overlay matrix.");
            }
        }
        async Task SeekAsync(double seconds)
        {
            await session.CommandAsync("seek", seconds.ToString(CultureInfo.InvariantCulture), "absolute+exact");
            var wait = Stopwatch.StartNew();
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                var seek = await session.GetPropertiesAsync("seeking", "time-pos");
                if (seek["seeking"] == "no" && double.TryParse(seek["time-pos"], CultureInfo.InvariantCulture, out var position) && Math.Abs(position - seconds) < .1) return;
                if (wait.Elapsed.TotalSeconds > 10) throw new TimeoutException("Overlay check seek did not settle.");
                await Task.Delay(50, cancellation);
            }
        }
        void CheckRealFile()
        {
            if (realMedia is null) return;
            var file = new FileInfo(realMedia.Path);
            if (!file.Exists || file.Length != realMedia.Bytes || file.LastWriteTimeUtc != realMedia.ModifiedUtc)
                throw new IOException("The selected media file changed during the overlay check.");
        }
        try
        {
            await SaveAsync();
            LabPanel.Visibility = InfoPanel.Visibility = Visibility.Collapsed;
            WindowCheckBanner.Visibility = Visibility.Visible;
            WebProbeContainer.IsHitTestVisible = false;
            if (realMedia is not null)
            {
                WindowCheckProgress.Text = "Preparing current video checks. Playback paused while verifying the media file.";
                await session.SetAsync("pause", "yes");
                realMedia = realMedia with { Sha256 = await HashAsync(realMedia.Path) };
                CheckRealFile();
                await SaveAsync();
            }
            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Assets", hdrFixture ? "display-media.json" : "baseline-media.json"), cancellation));
            foreach (string resolution in hdrFixture ? new[] { "hdr", "hdr-sdr", "hdr-restored" } : currentVideo ? new[] { "current" } : new[] { "1080p", "4k" })
            {
                var fixture = currentVideo ? default : manifest.RootElement.GetProperty(hdrFixture ? "hdr-av" : resolution);
                bool forceSdr = hdrFixture && resolution == "hdr-sdr";
                if (hdrFixture) await session.SetForceSdrOutputAsync(forceSdr);
                string mediaPath = realMedia?.Path ?? Path.Combine(AppContext.BaseDirectory, "Assets", fixture.GetProperty("file").GetString()!);
                string mediaSha256 = realMedia?.Sha256 ?? await HashAsync(mediaPath);
                double segmentStart = realMedia?.StartPosition ?? 0;
                double snapshotPosition = segmentStart + 2;
                if (!currentVideo && !mediaSha256.Equals(fixture.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Overlay fixture hash mismatch.");
                foreach (string mode in new[] { "plain", "web", "blur", "combined", "cleared" })
                {
                    bool web = mode is "web" or "combined", blur = mode is "blur" or "combined";
                    string id = resolution + "-" + mode;
                    WindowCheckProgress.Text = hdrFixture
                        ? $"HDR overlays: {resolution} / {mode}. Watch the picture and blur; listen during playback. Pauses between stages are expected."
                        : currentVideo ? $"Current video: {mode}. Watch and listen for about 10 seconds. Pauses between stages are expected."
                        : $"Overlay matrix: {resolution} / {mode}. Silent sample. Watch the picture and control background.";
                    string stageStatus = "running"; string? stageFailure = null;
                    long stageStart = LogLength(), prior = (await session.ReadAsync()).FileLoadedCount;
                    long? measurementLogStart = null, measurementLogEnd = null;
                    var observations = new List<object>(); var timeline = new List<object>();
                    object? webCheck = null, frameCheck = null;
                    string? screenshot = null, screenshotSha256 = null;
                    try
                    {
                        CheckRealFile();
                        await session.OpenAsync(mediaPath, paused: true);
                        var ready = await ReadyAsync(mediaPath, prior + 1, true);
                        if (ready.State.Duration < segmentStart + (currentVideo ? 25 : 5)) throw new InvalidDataException("The matrix sample is too short.");
                        if (realMedia is not null)
                        {
                            await session.SetAsync("aid", realMedia.AudioId.ToString(CultureInfo.InvariantCulture));
                            await SeekAsync(segmentStart);
                        }
                        CloseWebProbe(); await _webCommandTask;
                        await SetBlurEnabledAsync(blur, cancellation);
                        if (web)
                        {
                            await OpenWebProbeAsync(cancellation);
                            _webCheckAcceptsCommands = true;
                            try
                            {
                                await ClickWebProbeAsync("play", 1, cancellation);
                                await WaitForWebAsync("window.maniProbe.response?.requestId === 1 && window.maniProbe.response?.success === true && window.maniProbe.state?.paused === false && window.maniProbe.state?.position > 0.05", cancellation);
                                var playing = await session.ReadAsync();
                                await ClickWebProbeAsync("pause", 2, cancellation);
                                await WaitForWebAsync("window.maniProbe.response?.requestId === 2 && window.maniProbe.response?.success === true && window.maniProbe.state?.paused === true", cancellation);
                                var paused = await session.ReadAsync();
                                await _web!.CoreWebView2.ExecuteScriptAsync("document.getElementById('focus-test').focus()").AsTask(cancellation);
                                await WaitForWebAsync("document.activeElement === document.getElementById('focus-test')", cancellation);
                                webCheck = new { playing, paused, domFocusObserved = true, requestIds = new[] { 1, 2 }, generation = _webGeneration };
                                if (playing.Paused || !paused.Paused) throw new InvalidOperationException("Web commands did not change native pause state.");
                            }
                            finally { _webCheckAcceptsCommands = false; }
                        }
                        await session.SetAsync("pause", "yes");
                        await SeekAsync(segmentStart);
                        await ReadyAsync(mediaPath, prior + 1, true);
                        measurementLogStart = LogLength();
                        await session.SetAsync("pause", "no");
                        var clock = Stopwatch.StartNew();
                        while (observations.Count < stableSamples)
                        {
                            await Task.Delay(250, cancellation); await _pollTask;
                            var capture = await DisplaySignalProbe.CaptureAsync(session);
                            var display = ReadSwitchDisplay(); var actualWindow = ReadCheckWindow();
                            if (display.GetRawText() != displayBefore.GetRawText() || !WindowMatches(actualWindow, initialWindow))
                                throw new InvalidOperationException("Keep the window, display mode and scale unchanged.");
                            if (_blurEnabled != blur || (_web is not null) != web || (web && !_webReady))
                                throw new InvalidOperationException("An overlay changed or stopped responding.");
                            var state = capture.State;
                            if (state.FileLoadedCount != prior + 1 || state.Path != mediaPath || state.LoadFailure.Length > 0 || state.Ended)
                                throw new InvalidOperationException("The sample reloaded, failed or ended during observation.");
                            var properties = await session.GetPropertiesAsync("glsl-shaders", "glsl-shader-opts", "osd-dimensions", "track-list");
                            var bounds = Controls.TransformToVisual(VideoPanel).TransformBounds(new Windows.Foundation.Rect(0, 0, Controls.ActualWidth, Controls.ActualHeight));
                            object? transform = null;
                            using (var chain = await session.AcquireSwapChainAsync())
                                if (chain is not null)
                                {
                                    var m = CompositionSwapChain.ReadTransform(chain.Pointer);
                                    transform = new { m.M11, m.M12, m.M21, m.M22, m.M31, m.M32 };
                                }
                            bool stable = !state.Paused && !state.Idle && !state.Loading && !state.PausedForCache &&
                                _bridge?.HasAttachedSwapChain == true && DisplaySignalProbe.OutputMatchesPolicy(capture) && transform is not null;
                            var o = new { sampleId = timeline.Count + 1, elapsedMs = clock.ElapsedMilliseconds, stable, display, window = actualWindow,
                                panelAttached = _bridge?.HasAttachedSwapChain == true, panelDipWidth = VideoPanel.ActualWidth, panelDipHeight = VideoPanel.ActualHeight,
                                panelWidth = VideoPanel.ActualWidth * VideoPanel.CompositionScaleX, panelHeight = VideoPanel.ActualHeight * VideoPanel.CompositionScaleY,
                                transform, capture, properties, webOpen = _web is not null, webReady = _webReady, webGeneration = _webGeneration,
                                webChildren = WebProbeContainer.Children.Count, blurEnabled = _blurEnabled, blurStrength = _blurStrength, blurParameters = _blurParameters,
                                controls = new { bounds.X, bounds.Y, bounds.Width, bounds.Height } };
                            timeline.Add(o);
                            if (stable) observations.Add(o); else observations.Clear();
                            if (clock.Elapsed.TotalSeconds > (currentVideo ? 20 : 5)) throw new TimeoutException("Stable playback did not settle within the observation limit.");
                        }
                        measurementLogEnd = LogLength();
                        await session.SetAsync("pause", "yes");
                        await SeekAsync(snapshotPosition);
                        await ReadyAsync(mediaPath, prior + 1, true);
                        screenshot = id + "-output.png";
                        await session.CommandAsync("screenshot-to-file", Path.Combine(directory, screenshot), "window");
                        screenshotSha256 = await HashAsync(Path.Combine(directory, screenshot));
                        if (mode == "cleared")
                        {
                            var before = await session.ReadAsync();
                            string name = resolution + "-independent.png";
                            await FrameCapture.CaptureAsync(ProbeOptions(id + "-capture"), mediaPath, snapshotPosition, Path.Combine(directory, name), cancellation);
                            var after = await session.ReadAsync();
                            frameCheck = new { before, after, file = name, sha256 = await HashAsync(Path.Combine(directory, name)) };
                            if (!after.Paused || after.Path != before.Path || after.FileLoadedCount != before.FileLoadedCount || Math.Abs(after.Position - before.Position) > .01)
                                throw new InvalidOperationException("Independent capture changed main playback.");
                        }
                        stageStatus = "completed";
                    }
                    catch (OperationCanceledException) { stageStatus = "cancelled"; throw; }
                    catch (Exception error) { stageStatus = "failed"; stageFailure = error.ToString(); throw; }
                    finally
                    {
                        stages.Add(new { id, resolution, mode, status = stageStatus, failure = stageFailure, mediaPath, mediaSha256,
                            fileLoadedBefore = prior, forceSdr, segmentStart, snapshotPosition, measurementLogStart, measurementLogEnd,
                            observations, timeline, webCheck, frameCheck, screenshot, screenshotSha256,
                            logStart = stageStart, logEnd = LogLength() });
                        await SaveAsync();
                    }
                }
            }
            CheckRealFile();
            status = "completed";
        }
        catch (OperationCanceledException) { status = "cancelled"; throw; }
        catch (Exception error) { status = "failed"; failure = error.ToString(); throw; }
        finally
        {
            _webCheckAcceptsCommands = false;
            try { CloseWebProbe(); await _webCommandTask; }
            catch (Exception error) { cleanupFailure = error.ToString(); status = "failed"; }
            // Restore the diagnostic override even if subsequent overlay cleanup fails.
            if (hdrFixture)
            {
                try { await session.SetForceSdrOutputAsync(false); }
                catch (Exception error) { cleanupFailure ??= error.ToString(); status = "failed"; }
            }
            try
            {
                await session.SetAsync("pause", "yes");
                await SetBlurEnabledAsync(false, CancellationToken.None);
                await session.SetAsync("glsl-shader-opts", config["glsl-shader-opts"]);
                _blurParameters = "";
                var finalState = await session.ReadAsync();
                finalPaused = finalState.Paused;
                finalColorOutput = finalState.ColorOutput;
                finalForceSdr = await session.GetForceSdrOutputAsync();
                finalSettings = await session.GetPropertiesAsync(config.Keys.ToArray());
                if (finalPaused != true || finalForceSdr != false || finalSettings.Any(kv => config[kv.Key] != kv.Value))
                    throw new InvalidOperationException("Overlay matrix settings did not restore.");
            }
            catch (Exception error) { cleanupFailure ??= error.ToString(); status = "failed"; }
            if (!_closing)
            {
                LabPanel.Visibility = oldLab; InfoPanel.Visibility = oldInfo;
                WebProbeContainer.IsHitTestVisible = oldHitTest;
                WindowCheckBanner.Visibility = Visibility.Collapsed;
                finalWindow = ReadCheckWindow(); displayAfter = ReadSwitchDisplay();
                if (!WindowMatches(finalWindow, initialWindow) || displayAfter.Value.GetRawText() != displayBefore.GetRawText())
                { cleanupFailure ??= "Window or display changed."; status = "failed"; }
            }
            try
            {
                await using var source = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                logEnd = source.Length; source.Position = logStart;
                await using var target = File.Create(Path.Combine(directory, "mpv-overlay-matrix.log"));
                byte[] buffer = new byte[65536];
                for (long left = logEnd - logStart; left > 0;)
                {
                    int n = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(left, buffer.Length)));
                    if (n == 0) throw new EndOfStreamException("Native log changed while copying.");
                    await target.WriteAsync(buffer.AsMemory(0, n)); left -= n;
                }
            }
            catch (Exception error) { logCopyFailure = error.ToString(); status = "failed"; }
            await SaveAsync();
            if (!_closing) LabResults.Text = WindowCheckStatus.Text = status == "completed"
                ? (hdrFixture
                    ? "Overlay matrix evidence saved. HDR restored; sample paused and overlays closed. Report any picture, blur or sound issues."
                    : currentVideo ? "Overlay matrix evidence saved. Current video paused; overlays closed. Report any picture or sound issues during playback."
                    : "Overlay matrix evidence saved. Silent sample paused; overlays closed. Report any cropping, flicker or misaligned blur.")
                : "Overlay matrix incomplete. Partial evidence saved; see the report.";
        }
    }
}


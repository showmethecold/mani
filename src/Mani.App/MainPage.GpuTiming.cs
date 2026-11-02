using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Mani.Playback;
using Mani_App.Diagnostics;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace Mani_App;

// IINA/mpv reuse and measurement limits: docs/m1/gpu-timing-validation.md.
public sealed partial class MainPage
{
    private async void GpuTiming_Click(object sender, RoutedEventArgs e) =>
        await RunProbeAsync(async cancellation =>
        {
            try { await RunGpuTimingAsync(cancellation); }
            catch (Exception error) { if (!_closing) WindowCheckStatus.Text = error.Message; throw; }
        });

    private async void HdrGpuTiming_Click(object sender, RoutedEventArgs e) =>
        await RunProbeAsync(async cancellation =>
        {
            try { await RunGpuTimingAsync(cancellation, hdrFixture: true); }
            catch (Exception error) { if (!_closing) WindowCheckStatus.Text = error.Message; throw; }
        });

    private async Task RunGpuTimingAsync(CancellationToken cancellation, bool hdrFixture = false, string? outputDirectory = null)
    {
        if (Owner?.AppWindow.Presenter is not OverlappedPresenter presenter || presenter.State == OverlappedPresenterState.Minimized)
            throw new InvalidOperationException("Start in a visible normal or maximized window.");
        if (_web is not null || _blurEnabled) throw new InvalidOperationException("Close WebView2 and GPU blur before starting.");
        var session = _session!;
        await _pollTask; _pendingSeek = _pendingVolume = null;
        var config = await session.GetPropertiesAsync("ab-loop-a", "ab-loop-b", "loop-file", "loop-playlist", "vf",
            "glsl-shaders", "glsl-shader-opts", "icc-profile", "icc-profile-auto", "target-lut", "speed", "volume", "mute", "audio-device", "aid", "sid", "secondary-sid");
        if (new[] { "ab-loop-a", "ab-loop-b", "loop-file", "loop-playlist", "icc-profile-auto" }.Any(k => config[k] != "no") ||
            new[] { "vf", "glsl-shaders", "icc-profile", "target-lut" }.Any(k => config[k].Length != 0) || config["speed"] != "1.000000" ||
            await session.GetForceSdrOutputAsync())
            throw new InvalidOperationException("Use normal speed and automatic output, without loops, filters or custom color processing.");
        if (config["mute"] != "no" || !double.TryParse(config["volume"], CultureInfo.InvariantCulture, out var volume) || volume <= 0)
            throw new InvalidOperationException("Unmute the player and use an audible volume before starting.");
        var displayBefore = ReadSwitchDisplay(); var initialWindow = ReadCheckWindow();
        if (DisplayText(displayBefore, "monitor").Length == 0 || DisplayText(displayBefore, "kind").Length == 0)
            throw new InvalidOperationException("Display information is unavailable.");
        if (hdrFixture && DisplayText(displayBefore, "kind") != "HighDynamicRange")
            throw new InvalidOperationException("Enable Windows HDR before measuring HDR GPU blur.");
        var configurationBeforeMediaPreparation = hdrFixture ? config : null;
        if (hdrFixture)
        {
            cancellation.ThrowIfCancellationRequested();
            string mediaPath = LifecycleProbe.Fixture(Path.Combine(AppContext.BaseDirectory, "Assets"), "display-media.json", "hdr-av");
            try
            {
                await LifecycleProbe.OpenReadyAsync(session, mediaPath, LifecycleHostReady, cancellation);
                var prepared = await session.GetPropertiesAsync(config.Keys.ToArray());
                if (prepared.Any(kv => kv.Key is not ("aid" or "sid" or "secondary-sid") && config[kv.Key] != kv.Value))
                    throw new InvalidOperationException("Playback settings changed while preparing the HDR sample.");
                config = prepared; // Track IDs belong to the newly loaded sample; other options must be unchanged.
            }
            catch
            {
                try { await session.SetAsync("pause", "yes"); } catch { }
                throw;
            }
        }
        var initial = await session.ReadAsync();
        if (initial.Idle || initial.Loading || !File.Exists(initial.Path) || !double.IsFinite(initial.Duration) || initial.Duration < 40)
            throw new InvalidOperationException("Open a local video with audio and at least 40 seconds of duration first.");
        var source = await session.GetPropertiesAsync("video-params", "track-list");
        using var video = JsonDocument.Parse(source["video-params"]);
        var v = video.RootElement;
        if (hdrFixture)
        {
            if (v.GetProperty("primaries").GetString() != "bt.2020" || v.GetProperty("gamma").GetString() != "pq" ||
                v.GetProperty("hw-pixelformat").GetString() != "p010" || v.GetProperty("w").GetInt32() != 640 || v.GetProperty("h").GetInt32() != 360)
                throw new InvalidOperationException("The HDR sample must decode as 640x360 BT.2020 PQ / p010.");
        }
        else if (v.GetProperty("primaries").GetString() != "bt.709" || v.GetProperty("gamma").GetString() is not
            ("bt.709" or "srgb" or "bt.1886" or "gamma1.8" or "gamma2.0" or "gamma2.2" or "gamma2.4" or "gamma2.6" or "gamma2.8"))
            throw new InvalidOperationException("This timing check requires a BT.709 SDR source. Use Measure HDR GPU blur for the HDR sample.");
        using var tracks = JsonDocument.Parse(source["track-list"]);
        var audio = tracks.RootElement.EnumerateArray().FirstOrDefault(t => t.GetProperty("type").GetString() == "audio" &&
            t.TryGetProperty("selected", out var selected) && selected.GetBoolean());
        if (audio.ValueKind == JsonValueKind.Undefined) throw new InvalidOperationException("Select an audio track before starting.");
        var file = new FileInfo(initial.Path);
        var media = new OverlayMatrixMedia(file.FullName, "", file.Length, file.LastWriteTimeUtc, v.GetProperty("w").GetInt32(),
            v.GetProperty("h").GetInt32(), initial.Duration, hdrFixture ? 0 : Math.Clamp(initial.Position, 0, initial.Duration - 40), audio.GetProperty("id").GetInt32());
        string directory = outputDirectory ?? NewProbeDirectory("gpu-timing"), log = Path.Combine(_logDirectory, "mpv.log");
        Directory.CreateDirectory(directory);
        string shaderPath = Path.Combine(AppContext.BaseDirectory, "Assets", "m1-blur.glsl");
        async Task<string> HashAsync(string path)
        {
            await using var stream = File.OpenRead(path);
            return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellation));
        }
        string appSha256 = await HashAsync(Path.Combine(AppContext.BaseDirectory, "Mani.App.dll"));
        string playbackSha256 = await HashAsync(typeof(PlaybackSession).Assembly.Location);
        string shaderSha256 = await HashAsync(shaderPath);
        long LogLength() { using var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); return stream.Length; }
        long logStart = LogLength(), logEnd = logStart;
        string status = "running"; string? failure = null, cleanupFailure = null, logCopyFailure = null;
        bool? finalPaused = null, finalForceSdr = null;
        ColorOutputTarget? finalColorOutput = null;
        IReadOnlyDictionary<string, string>? finalSettings = null;
        WindowCheckState? finalWindow = null; JsonElement? displayAfter = null;
        var stages = new List<object>();
        var oldLab = LabPanel.Visibility; var oldInfo = InfoPanel.Visibility;
        async Task SaveAsync()
        {
            var report = new { schema_version = hdrFixture ? 2 : 1, inputKind = hdrFixture ? "hdr-fixture" : "current-video",
                suite = "visible-gpu-timing", status, failure, cleanupFailure, logCopyFailure,
                atUtc = DateTimeOffset.UtcNow, hostProcessId = Environment.ProcessId,
                hostAssemblyId = typeof(MainPage).Module.ModuleVersionId, playbackAssemblyId = typeof(PlaybackSession).Module.ModuleVersionId,
                appSha256, playbackSha256, dllSha256 = ProbeOptions("unused").DllSha256, shaderSha256, shaderPath, realMedia = media,
                configuration = config, configurationBeforeMediaPreparation, finalSettings, initialWindow, finalWindow, displayBefore, displayAfter,
                finalPaused, finalForceSdr, finalColorOutput, finalWebOpen = _web is not null, finalBlurEnabled = _blurEnabled,
                finalWebChildren = WebProbeContainer.Children.Count, stages, logStart, logEnd,
                warmupSeconds = 15, measurementSeconds = 15, samplesRequired = 16,
                metric = "sum-of-latest-fresh-pass-nanoseconds-sampled-at-1Hz",
                performanceAccepted = false, visualAccepted = false, audioAccepted = false,
                finishPolicy = "leave-current-media-paused-overlays-off",
                limitations = (hdrFixture ? "Pinned synthetic 640x360 BT.2020 PQ source on an HDR display; no high-bitrate/4K inference. " : "Current BT.709 SDR video. ") + "Sparse per-pass latest GPU timings, not synchronized whole-frame times. Native rolling averages may include previous stages. No GPU utilization, decode/DWM timing, calibrated HDR or full performance acceptance." };
            string path = Path.Combine(directory, "result.json");
            await File.WriteAllTextAsync(path + ".tmp", JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(path + ".tmp", path, overwrite: true);
        }
        void CheckHost()
        {
            cancellation.ThrowIfCancellationRequested();
            if (_closing || !WindowMatches(ReadCheckWindow(), initialWindow) || ReadSwitchDisplay().GetRawText() != displayBefore.GetRawText())
                throw new InvalidOperationException("Keep the window, display mode and scale unchanged.");
            var current = new FileInfo(media.Path);
            if (!current.Exists || current.Length != media.Bytes || current.LastWriteTimeUtc != media.ModifiedUtc)
                throw new IOException("The selected media file changed during measurement.");
        }
        try
        {
            await SaveAsync();
            LabPanel.Visibility = InfoPanel.Visibility = Visibility.Collapsed;
            WindowCheckBanner.Visibility = Visibility.Visible;
            WindowCheckProgress.Text = hdrFixture ? "Preparing HDR GPU timing. Verifying the test video."
                : "Preparing GPU timing. Playback paused while verifying the media file.";
            await session.SetAsync("pause", "yes");
            media = media with { Sha256 = await HashAsync(media.Path) };
            if (hdrFixture)
            {
                using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Assets", "display-media.json"), cancellation));
                if (!media.Sha256.Equals(manifest.RootElement.GetProperty("hdr-av").GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("HDR timing sample hash mismatch.");
            }
            CheckHost(); await SaveAsync();
            int index = 0;
            foreach (bool blur in new[] { false, true, true, false })
            {
                index++;
                string id = $"{index}-{(blur ? "blur" : "plain")}";
                string stageStatus = "running"; string? stageFailure = null;
                long stageStart = LogLength(); long? measurementLogStart = null, measurementLogEnd = null;
                var observations = new List<object>();
                try
                {
                    CheckHost();
                    await session.SetAsync("pause", "yes");
                    await session.CommandAsync("seek", media.StartPosition.ToString(CultureInfo.InvariantCulture), "absolute+exact");
                    var seekWait = Stopwatch.StartNew();
                    while (true)
                    {
                        CheckHost();
                        var seek = await session.GetPropertiesAsync("seeking", "time-pos");
                        if (seek["seeking"] == "no" && double.TryParse(seek["time-pos"], CultureInfo.InvariantCulture, out var position) &&
                            Math.Abs(position - media.StartPosition) < .15) break;
                        if (seekWait.Elapsed.TotalSeconds > 10) throw new TimeoutException("The timing segment seek did not settle.");
                        await Task.Delay(50, cancellation);
                    }
                    await SetBlurEnabledAsync(blur, cancellation);
                    WindowCheckProgress.Text = $"{(hdrFixture ? "HDR GPU timing" : "GPU timing")} {index}/4: blur {(blur ? "on" : "off")}. Warming up for 15 seconds, then measuring for 15 seconds.";
                    await session.SetAsync("pause", "no");
                    var clock = Stopwatch.StartNew();
                    for (int second = 1; second <= 30; second++)
                    {
                        double remaining = second * 1000 - clock.Elapsed.TotalMilliseconds;
                        if (remaining > 0) await Task.Delay(TimeSpan.FromMilliseconds(remaining), cancellation);
                        CheckHost(); await _pollTask;
                        if (clock.Elapsed.TotalSeconds > second + 2) throw new TimeoutException("GPU timing sampling stalled.");
                        if (second == 15) measurementLogStart = LogLength();
                        var capture = await DisplaySignalProbe.CaptureAsync(session);
                        var properties = await session.GetPropertiesAsync(config.Keys.Concat(new[] { "vo-passes", "osd-dimensions", "track-list" }).ToArray());
                        double elapsedMs = clock.Elapsed.TotalMilliseconds;
                        var bounds = Controls.TransformToVisual(VideoPanel).TransformBounds(new Windows.Foundation.Rect(0, 0, Controls.ActualWidth, Controls.ActualHeight));
                        object? transform = null;
                        using (var chain = await session.AcquireSwapChainAsync())
                            if (chain is not null)
                            {
                                var m = CompositionSwapChain.ReadTransform(chain.Pointer);
                                transform = new { m.M11, m.M12, m.M21, m.M22, m.M31, m.M32 };
                            }
                        observations.Add(new { second, measured = second >= 15, elapsedMs, capture, properties,
                            display = ReadSwitchDisplay(), window = ReadCheckWindow(), transform,
                            panelAttached = _bridge?.HasAttachedSwapChain == true,
                            panelDipWidth = VideoPanel.ActualWidth, panelDipHeight = VideoPanel.ActualHeight,
                            panelWidth = VideoPanel.ActualWidth * VideoPanel.CompositionScaleX,
                            panelHeight = VideoPanel.ActualHeight * VideoPanel.CompositionScaleY,
                            controls = new { bounds.X, bounds.Y, bounds.Width, bounds.Height },
                            webOpen = _web is not null, webChildren = WebProbeContainer.Children.Count,
                            blurEnabled = _blurEnabled, blurStrength = _blurStrength, blurParameters = _blurParameters });
                        var state = capture.State;
                        if (hdrFixture && (state.ColorOutput?.Hdr != true || state.ColorPrimaries != "bt.2020" || state.Transfer != "pq"))
                            throw new InvalidOperationException("HDR input or actual HDR output changed during GPU timing.");
                        if (state.Path != media.Path || state.FileLoadedCount != initial.FileLoadedCount || state.Paused || state.Loading || state.Idle ||
                            state.Ended || state.PausedForCache || state.LoadFailure.Length > 0 || state.HardwareDecoder != "d3d11va" || state.AudioOutput != "wasapi" ||
                            !DisplaySignalProbe.OutputMatchesPolicy(capture) || transform is null || _bridge?.HasAttachedSwapChain != true || _web is not null || _blurEnabled != blur)
                            throw new InvalidOperationException("Playback, output or overlay state changed during GPU timing.");
                        if (properties.Any(kv => config.ContainsKey(kv.Key) && kv.Key is not ("glsl-shaders" or "glsl-shader-opts") && config[kv.Key] != kv.Value))
                            throw new InvalidOperationException("Playback settings changed during GPU timing.");
                        using var passes = JsonDocument.Parse(properties["vo-passes"]);
                        var fresh = passes.RootElement.GetProperty("fresh");
                        if (fresh.GetArrayLength() == 0 || !fresh.EnumerateArray().Any(p => p.GetProperty("last").GetInt64() > 0))
                            throw new InvalidOperationException("Native GPU timing is unavailable; zero timing is not zero cost.");
                        foreach (string name in new[] { "Mani M1 horizontal GPU blur", "Mani M1 rounded control blur" })
                            if (fresh.EnumerateArray().Any(p => p.GetProperty("desc").GetString()!.Contains(name) && p.GetProperty("last").GetInt64() > 0) != blur)
                                throw new InvalidOperationException("Native GPU passes do not match the requested blur state.");
                    }
                    measurementLogEnd = LogLength();
                    await session.SetAsync("pause", "yes");
                    stageStatus = "completed";
                }
                catch (OperationCanceledException) { stageStatus = "cancelled"; throw; }
                catch (Exception error) { stageStatus = "failed"; stageFailure = error.ToString(); throw; }
                finally
                {
                    stages.Add(new { id, blur, status = stageStatus, failure = stageFailure, logStart = stageStart, logEnd = LogLength(),
                        measurementLogStart, measurementLogEnd, observations });
                    await SaveAsync();
                }
            }
            CheckHost(); status = "completed";
        }
        catch (OperationCanceledException) { status = "cancelled"; throw; }
        catch (Exception error) { status = "failed"; failure = error.ToString(); throw; }
        finally
        {
            async Task CleanupAsync(Func<Task> action)
            {
                try { await action(); }
                catch (Exception error) { cleanupFailure ??= error.ToString(); status = "failed"; }
            }
            await CleanupAsync(() => session.SetAsync("pause", "yes"));
            await CleanupAsync(() => SetBlurEnabledAsync(false, CancellationToken.None));
            await CleanupAsync(() => session.SetAsync("glsl-shaders", config["glsl-shaders"]));
            await CleanupAsync(() => session.SetAsync("glsl-shader-opts", config["glsl-shader-opts"]));
            _blurParameters = "";
            await CleanupAsync(async () =>
            {
                var finalState = await session.ReadAsync();
                finalPaused = finalState.Paused; finalColorOutput = finalState.ColorOutput;
                finalForceSdr = await session.GetForceSdrOutputAsync();
                finalSettings = await session.GetPropertiesAsync(config.Keys.ToArray());
                if (finalPaused != true || finalForceSdr != false || (hdrFixture && finalColorOutput?.Hdr != true) || finalSettings.Any(kv => config[kv.Key] != kv.Value))
                    throw new InvalidOperationException("GPU timing settings did not restore.");
            });
            if (!_closing)
            {
                LabPanel.Visibility = oldLab; InfoPanel.Visibility = oldInfo; WindowCheckBanner.Visibility = Visibility.Collapsed;
                finalWindow = ReadCheckWindow(); displayAfter = ReadSwitchDisplay();
                if (!WindowMatches(finalWindow, initialWindow) || displayAfter.Value.GetRawText() != displayBefore.GetRawText())
                { cleanupFailure ??= "Window or display changed."; status = "failed"; }
            }
            try
            {
                await using var input = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                logEnd = input.Length; input.Position = logStart;
                await using var output = File.Create(Path.Combine(directory, "mpv-gpu-timing.log"));
                byte[] buffer = new byte[65536];
                for (long left = logEnd - logStart; left > 0;)
                {
                    int n = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(left, buffer.Length)));
                    if (n == 0) throw new EndOfStreamException("Native log changed while copying.");
                    await output.WriteAsync(buffer.AsMemory(0, n)); left -= n;
                }
            }
            catch (Exception error) { logCopyFailure = error.ToString(); status = "failed"; }
            await SaveAsync();
            if (!_closing) LabResults.Text = WindowCheckStatus.Text = status == "completed"
                ? "GPU timing saved. Video paused; blur off. Report any picture or sound issues during playback."
                : "GPU timing incomplete. Partial evidence saved; see the report.";
        }
    }
}

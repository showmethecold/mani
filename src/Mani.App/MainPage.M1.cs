using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Mani.Playback;
using Microsoft.Graphics.Display;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Microsoft.Windows.Storage.Pickers;

namespace Mani_App;

public sealed partial class MainPage
{
    private double _blurStrength;
    private string _blurParameters = "";
    private bool _m1Busy, _hostStress, _labUpdating, _blurEnabled, _webReady;
    private Task _m1Task = Task.CompletedTask;
    private CancellationTokenSource? _m1Cancellation;
    private WebView2? _web;
    private readonly string _webToken = Guid.NewGuid().ToString("N");
    private DisplayInformation? _display;

    private PlaybackOptions ProbeOptions(string name)
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "mpv-runtime.json")));
        return new PlaybackOptions(Path.Combine(AppContext.BaseDirectory, "libmpv-2.dll"),
            manifest.RootElement.GetProperty("sha256").GetString()!, Path.Combine(_logDirectory, name + ".log"));
    }
    private async Task RunProbeAsync(Func<CancellationToken, Task> action)
    {
        if (!DiagnosticsBuild.Enabled) return;
        if (_closing || _m1Busy || _session is null) return;
        CloseLayoutSidebar(animate: false);
        _logOwner?.PreserveEvidence();
        _m1Busy = true;
        _m1Cancellation = new CancellationTokenSource();
        ControlsHost.IsEnabled = false;
        _m1Task = RunProbeCoreAsync(action, _m1Cancellation.Token);
        await _m1Task;
    }
    private async Task RunProbeCoreAsync(Func<CancellationToken, Task> action, CancellationToken cancellation)
    {
        try { await action(cancellation); }
        catch (OperationCanceledException) { if (!_closing) LabResults.Text = "Probe cancelled."; }
        catch (Exception error)
        {
            Trace("probe-error", new { error = error.ToString() });
            if (!_closing) LabResults.Text = $"Probe failed (0x{error.HResult:X8}). See host-state.jsonl for details.";
        }
        finally
        {
            _m1Busy = false;
            _m1Cancellation?.Dispose(); _m1Cancellation = null;
            if (!_closing) ControlsHost.IsEnabled = true;
        }
    }
    private void CancelProbe_Click(object sender, RoutedEventArgs e) => _m1Cancellation?.Cancel();
    private async void Lab_Click(object sender, RoutedEventArgs e)
    {
        if (!DiagnosticsBuild.Enabled) return;
        LabPanel.Visibility = LabPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        if (LabPanel.Visibility != Visibility.Visible || _session is null || _m1Busy) return;
        await RunProbeAsync(async _ =>
        {
            await RefreshSubtitleControlsAsync();
            LabResults.Text = "Ready. Open a video before running media probes.";
        });
    }
    private async void SubtitleProbe_Click(object sender, RoutedEventArgs e) => await RunProbeAsync(async cancellation =>
    {
        if (Owner is null || _session is null || _snapshot is not { Idle: false }) { LabResults.Text = "Open a video first."; return; }
        var picker = new FileOpenPicker(Owner.AppWindow.Id) { CommitButtonText = "Load subtitle" };
        picker.FileTypeFilter.Add(".srt"); picker.FileTypeFilter.Add(".ass"); picker.FileTypeFilter.Add(".ssa");
        var file = await picker.PickSingleFileAsync().AsTask(cancellation); cancellation.ThrowIfCancellationRequested();
        if (file is null || _closing) return;
        var previous = (await _session.GetPropertiesAsync("sid"))["sid"];
        await _session.CommandAsync("sub-add", file.Path, "select");
        var selected = (await _session.GetPropertiesAsync("sid"))["sid"];
        if ((string)((Button)sender).Tag == "secondary")
        {
            if (selected == previous) { LabResults.Text = "Choose a different subtitle for the secondary track."; return; }
            await _session.SetAsync("sid", previous);
            await _session.SetAsync("secondary-sid", selected);
        }
        var properties = await _session.GetPropertiesAsync("sid", "secondary-sid", "track-list");
        Trace("subtitle-load", properties);
        if (!_closing) LabResults.Text = "Subtitle loaded. Adjust each channel independently; test preserved ASS with an ASS file.";
    });
    private async void SubtitleNumber_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_labUpdating || _session is null || !double.IsFinite(args.NewValue)) return;
        await Execute(s => s.SetAsync((string)sender.Tag, args.NewValue.ToString(CultureInfo.InvariantCulture)));
    }
    private async void SubtitleStyle_Changed(object sender, SelectionChangedEventArgs args)
    {
        if (_labUpdating || _session is null || sender is not ComboBox { SelectedItem: ComboBoxItem item } input) return;
        await Execute(s => s.SetAsync((string)input.Tag, (string)item.Tag));
    }
    private async void ClearLoop_Click(object sender, RoutedEventArgs e) => await Execute(async session =>
    {
        await session.ClearAbLoopAsync();
        LabResults.Text = "A-B loop cleared.";
    });
    private async void BlurProbe_Click(object sender, RoutedEventArgs e) => await RunProbeAsync(async cancellation =>
    {
        await SetBlurEnabledAsync(!_blurEnabled, cancellation);
        if (!_closing) LabResults.Text = _blurEnabled ? "GPU blur follows the rounded control bounds. HDR and resize animation require visual acceptance." : "GPU blur disabled.";
    });

    private async Task SetBlurEnabledAsync(bool enable, CancellationToken cancellation)
    {
        if (_blurEnabled == enable) return;
        var shader = Path.Combine(AppContext.BaseDirectory, "Assets", "m1-blur.glsl");
        try
        {
            if (enable)
            {
                _blurStrength = 0; _blurEnabled = true;
                await SyncBlurMaskAsync();
                await _session!.CommandAsync("change-list", "glsl-shaders", "append", shader);
            }
            for (int i = 1; i <= 5; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                _blurStrength = enable ? i / 5.0 : 1 - i / 5.0;
                await SyncBlurMaskAsync(); await Task.Delay(40, cancellation);
            }
            if (!enable)
            {
                await _session!.CommandAsync("change-list", "glsl-shaders", "remove", shader);
                _blurEnabled = false;
            }
            Trace("blur-probe", new { enabled = _blurEnabled });
        }
        catch
        {
            _blurEnabled = false; _blurStrength = 0; _blurParameters = "";
            try { await _session!.CommandAsync("change-list", "glsl-shaders", "remove", shader); } catch { }
            throw;
        }
    }
    private readonly SemaphoreSlim _blurGate = new(1, 1);
    private async Task SyncBlurMaskAsync()
    {
        await _blurGate.WaitAsync();
        try { await SyncBlurMaskCoreAsync(); }
        finally { _blurGate.Release(); }
    }
    private async Task SyncBlurMaskCoreAsync()
    {
        if (!_blurEnabled || _session is null || _closing) return;
        var properties = await _session.GetPropertiesAsync("osd-dimensions");
        if (properties["osd-dimensions"].Length == 0 || _closing) return;
        string parameters = CalculateBlurParameters(properties["osd-dimensions"]);
        if (parameters.Length == 0 || parameters == _blurParameters) return;
        await _session.SetAsync("glsl-shader-opts", parameters);
        _blurParameters = parameters;
        Trace("osc-material", new { parameters });
    }
    private string CalculateBlurParameters(string dimensionsJson)
    {
        using var data = JsonDocument.Parse(dimensionsJson);
        var dimensions = data.RootElement;
        double w = dimensions.GetProperty("w").GetDouble(), h = dimensions.GetProperty("h").GetDouble();
        double left = dimensions.GetProperty("ml").GetDouble(), top = dimensions.GetProperty("mt").GetDouble();
        double width = w - left - dimensions.GetProperty("mr").GetDouble();
        double height = h - top - dimensions.GetProperty("mb").GetDouble();
        if (width <= 0 || height <= 0) return "";
        double sx = VideoPanel.CompositionScaleX, sy = VideoPanel.CompositionScaleY;
        bool effects = !_accessibility.HighContrast && _systemUi.AdvancedEffectsEnabled;
        GlassMask Mask(Microsoft.UI.Xaml.Controls.Border element, double opacity)
        {
            var bounds = element.TransformToVisual(VideoPanel).TransformBounds(new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight));
            return GlassMask.Map(new(bounds.X, bounds.Y, bounds.Width, bounds.Height), sx, sy, width, height,
                left, top, element.CornerRadius.TopLeft, effects && element.Visibility == Visibility.Visible ? _blurStrength * opacity : 0);
        }
        // Reuse the same separable blur texture for all glass surfaces; no extra video copies/passes.
        return string.Join(",", Mask(Controls, _appliedDocking.Docked || IsMusicMode ? 0 : _osc.Opacity).Parameters("rect_"),
            Mask(LayoutSidebar, _settings.EdgeToEdgeVideo && !IsMusicMode && !IsPictureInPicture ? 1 : 0).Parameters("side_"),
            Mask(OsdOverlay, _osd.Opacity).Parameters("osd_"));
    }
    private void WatchDisplay()
    {
        if (Owner is null) return;
        try
        {
            _display = DisplayInformation.CreateForWindowId(Owner.AppWindow.Id);
            _display.AdvancedColorInfoChanged += Display_Changed;
        }
        catch (Exception error) { Trace("display-watch-unavailable", new { error = error.ToString() }); }
    }
    private void Display_Changed(DisplayInformation sender, object args) => DispatcherQueue.TryEnqueue(() =>
    {
        if (_closing) return;
        try { Trace("advanced-color-changed", DisplayDetails()); }
        catch (Exception error) { Trace("display-query-error", new { error = error.ToString() }); }
    });
    private object DisplayDetails()
    {
        var monitor = Owner is null ? null : Mani_App.Diagnostics.WindowMonitor.GetDeviceName(WinRT.Interop.WindowNative.GetWindowHandle(Owner));
        if (_display is null) return new { status = "unavailable", monitor };
        var color = _display.GetAdvancedColorInfo();
        return new { monitor, kind = color.CurrentAdvancedColorKind.ToString(), color.SdrWhiteLevelInNits, color.MaxLuminanceInNits,
            color.MinLuminanceInNits, hdrAvailable = color.IsAdvancedColorKindAvailable(DisplayAdvancedColorKind.HighDynamicRange),
            scaleX = VideoPanel.CompositionScaleX, scaleY = VideoPanel.CompositionScaleY };
    }
    private async void DisplayProbe_Click(object sender, RoutedEventArgs e) => await RunProbeAsync(async _ =>
    {
        using var chain = await _session!.AcquireSwapChainAsync();
        var report = new { display = DisplayDetails(), swapchain = chain is null ? null : CompositionSwapChain.ReadDescription(chain.Pointer),
            engine = await _session.GetPropertiesAsync("video-params", "video-out-params", "target-prim", "target-trc", "target-peak", "hwdec-current", "current-vo"),
            limitation = "Output format and display capability do not prove correct HDR luminance or tone mapping." };
        Trace("display-probe", report);
        await File.WriteAllTextAsync(Path.Combine(_logDirectory, "display-probe.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        if (!_closing) LabResults.Text = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
    });
    private async void OcrProbe_Click(object sender, RoutedEventArgs e) => await RunProbeAsync(async cancellation =>
    {
        var state = await _session!.ReadAsync();
        if (state.Idle || !File.Exists(state.Path)) { LabResults.Text = "Open a local video first."; return; }
        string languageTag = (string)((ComboBoxItem)OcrLanguage.SelectedItem).Tag;
        var directory = Path.Combine(_logDirectory, "capture-" + DateTime.UtcNow.ToString("HHmmss-fff"));
        Directory.CreateDirectory(directory);
        LabResults.Text = "Capturing an independent frame...";
        var png = await FrameCapture.CaptureAsync(ProbeOptions("capture-" + Guid.NewGuid().ToString("N")), state.Path, state.Position, Path.Combine(directory, "frame.png"), cancellation);
        var result = await Mani_App.Diagnostics.OcrProbe.RecognizeAsync(png, languageTag, cancellation);
        var report = new { result, requestedTime = state.Position, main = await _session.ReadAsync() };
        await File.WriteAllTextAsync(Path.Combine(directory, "ocr.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), cancellation);
        if (!_closing) LabResults.Text = result.Status == "blocked-missing-language"
            ? $"OCR language pack is unavailable: {languageTag}. Installed: {string.Join(", ", result.InstalledLanguages)}. The frame was saved."
            : result.Text.Length == 0 ? "Frame saved. OCR found no text." : result.Text;
    });
    private async void BenchmarkProbe_Click(object sender, RoutedEventArgs e) =>
        await RunProbeAsync(async cancellation => { await RunPerformanceProbeAsync(cancellation); });

    private async Task<string> RunPerformanceProbeAsync(CancellationToken cancellation, bool playbackFirst = false, string? baselineCase = null)
    {
        var session = _session!;
        await _pollTask;
        _pendingSeek = null;
        _pendingVolume = null;
        var before = await session.ReadAsync();
        if (before.Idle || before.Loading || before.VideoFormat.Length == 0 || before.Duration < 2 || !File.Exists(before.Path))
            throw new InvalidOperationException("Open a local video at least two seconds long.");
        if (_web is not null || _blurEnabled)
            throw new InvalidOperationException("Turn off WebView2 and GPU blur before measuring performance.");
        var previousPerformanceProbes = _performanceProbeCount++;
        var priorHostCycles = _hostCycleAttempts;
        var run = NewProbeDirectory(playbackFirst ? "playback-baseline" : "performance");
        Directory.CreateDirectory(run);
        var measurements = new List<object>(); var samples = new List<object>(); string status = "interrupted";
        string mediaHash = "";
        Mani_App.Diagnostics.ExternalResourceProbe? external = null;
        JsonElement? externalResources = null;
        string? failure = null, restoreFailure = null, reportReadFailure = null;
        var loop = await session.GetPropertiesAsync("ab-loop-a", "ab-loop-b", "speed", "volume", "mute", "sub-visibility", "secondary-sub-visibility");
        var displayAtStart = DisplayDetails();
        using var process = Process.GetCurrentProcess();
        try
        {
            if (playbackFirst)
            {
                LabResults.Text = "Waiting for the external collector. Run python scripts/run_m1_baselines.py --collect-host in the project folder.";
                external = await Mani_App.Diagnostics.ExternalResourceProbe.PrepareAsync(run, baselineCase!, cancellation);
            }
            LabResults.Text = playbackFirst
                ? "Measuring normal playback with external resource collection. Keep the window and display unchanged..."
                : "Hashing media, then measuring 10 seeks and up to 10 seconds of playback...";
            using (var stream = File.OpenRead(before.Path))
                mediaHash = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(stream, cancellation));
            await session.SetAsync("ab-loop-a", "no"); await session.SetAsync("ab-loop-b", "no");
            await session.SetAsync("speed", "1"); await session.SetAsync("volume", "50"); await session.SetAsync("mute", "no");
            await session.SetAsync("sub-visibility", "no"); await session.SetAsync("secondary-sub-visibility", "no");
            await session.SetAsync("pause", "yes");
            async Task MeasureSeek(double target, bool record)
            {
                var counter = await session.GetPlaybackRestartCountAsync(); var timer = Stopwatch.StartNew();
                await session.CommandAsync("seek", target.ToString(CultureInfo.InvariantCulture), "absolute+exact");
                while (await session.GetPlaybackRestartCountAsync() <= counter)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (timer.Elapsed.TotalSeconds > 15) throw new TimeoutException("Seek readiness timed out.");
                    await Task.Delay(10, cancellation);
                }
                if (record) measurements.Add(new { target, engine_ready_ms = timer.Elapsed.TotalMilliseconds, observedPosition = (await session.ReadAsync()).Position });
            }
            async Task MeasureSeeks()
            {
                foreach (var fraction in new[] { .1, .7, .3, .9, .2, .6, .4, .8, .15, .5 })
                    await MeasureSeek(before.Duration * fraction, true);
            }
            async Task MeasurePlayback()
            {
                await MeasureSeek(0, false);
                await session.SetAsync("pause", "no");
                if (external is not null) await external.StartAsync();
                else process.Refresh();
                var timer = Stopwatch.StartNew(); var cpu = external is null ? process.TotalProcessorTime : TimeSpan.Zero;
                var duration = Math.Min(playbackFirst ? 30 : 10, before.Duration - 0.3);
                while (timer.Elapsed.TotalSeconds < duration)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (external is null) process.Refresh();
                    var state = await session.ReadAsync();
                    if (state.Idle || state.Ended || state.Paused || state.Path != before.Path)
                        throw new InvalidOperationException("Playback stopped or changed during sampling.");
                    if (external is not null)
                        samples.Add(new { seconds = timer.Elapsed.TotalSeconds,
                            panelWidth = VideoPanel.ActualWidth * VideoPanel.CompositionScaleX,
                            panelHeight = VideoPanel.ActualHeight * VideoPanel.CompositionScaleY,
                            state.Position, state.DroppedFrames, state.HardwareDecoder, state.AvSync });
                    else samples.Add(new { seconds = timer.Elapsed.TotalSeconds, cpu_seconds = (process.TotalProcessorTime-cpu).TotalSeconds,
                        process.HandleCount, process.PrivateMemorySize64, process.WorkingSet64,
                        managedBytes = GC.GetTotalMemory(false), threadCount = process.Threads.Count,
                        gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2),
                        panelWidth = VideoPanel.ActualWidth * VideoPanel.CompositionScaleX,
                        panelHeight = VideoPanel.ActualHeight * VideoPanel.CompositionScaleY,
                        state.Position, state.DroppedFrames, state.HardwareDecoder, state.AvSync });
                    await Task.Delay(200, cancellation);
                }
                // Stop the external interval before pausing/seeking. Resource collection
                // never includes the collector handshake or the subsequent seek tests.
                if (external is not null) await external.StopAsync();
                await session.SetAsync("pause", "yes");
                if (external is not null) externalResources = await external.CompleteAsync(cancellation);
            }
            if (playbackFirst) { await MeasurePlayback(); await MeasureSeeks(); }
            else { await MeasureSeeks(); await MeasurePlayback(); }
            status = "completed";
        }
        catch (OperationCanceledException) { status = "cancelled"; throw; }
        catch (Exception error) { status = "failed"; failure = error.ToString(); throw; }
        finally
        {
            if (external is not null && externalResources is null)
            {
                try { await external.CancelAsync(); }
                catch (Exception error) { reportReadFailure = error.ToString(); status = "failed"; }
            }
            IReadOnlyDictionary<string, string>? engine = null;
            object? display = null;
            try
            {
                engine = await session.GetPropertiesAsync("current-vo", "hwdec-current", "gpu-api", "gpu-context", "video-out-params", "glsl-shaders", "vf", "video-sync", "osd-dimensions", "speed", "volume", "mute", "sub-visibility", "secondary-sub-visibility", "aid", "vid");
                display = DisplayDetails();
            }
            catch (Exception error) { status = "failed"; reportReadFailure = error.ToString(); }
            try
            {
                if (!_closing)
                {
                    await session.SetAsync("pause", "yes");
                    await session.CommandAsync("seek", before.Position.ToString(CultureInfo.InvariantCulture), "absolute+exact");
                    foreach (var value in loop) await session.SetAsync(value.Key, value.Value);
                    await session.SetAsync("pause", before.Paused ? "yes" : "no");
                }
            }
            catch (Exception error) { status = "failed"; restoreFailure = error.ToString(); }
            var report = new { schema_version = playbackFirst ? 3 : 2,
                resourceSampler = playbackFirst ? Mani_App.Diagnostics.ExternalResourceProbe.Profile : "in-process-dotnet-v1",
                externalRequestId = external?.RequestId, hostCreationFileTime = external?.CreationFileTime, externalResources, atUtc = DateTimeOffset.UtcNow, mediaPath = before.Path,
                hostProcessId = Environment.ProcessId, hostStartedAtUtc = process.StartTime.ToUniversalTime(),
                hostAssemblyId = typeof(MainPage).Module.ModuleVersionId,
                playbackAssemblyId = typeof(PlaybackSession).Module.ModuleVersionId,
                uiPollingProfile = "batched-v1",
                measurementOrder = playbackFirst ? "playback-first" : "seek-first", playbackLimitSeconds = playbackFirst ? 30 : 10,
                baselineCase, priorHostCycles, previousPerformanceProbes, processAgeSeconds = (DateTime.UtcNow - process.StartTime.ToUniversalTime()).TotalSeconds,
                status, failure, restoreFailure, reportReadFailure, mode = "WinUI host", media_sha256 = mediaHash, dll_sha256 = ProbeOptions("unused").DllSha256,
                measurements, samples, panelWidth = VideoPanel.ActualWidth * VideoPanel.CompositionScaleX, panelHeight = VideoPanel.ActualHeight * VideoPanel.CompositionScaleY,
                engine, display, displayAtStart, webOverlayActive = _web is not null, blurActive = _blurEnabled,
                limitations = "Readiness includes command queue/polling. Not first DWM presentation; GPU utilization is unmeasured. Compare only matching media, GPU, config, output size and actual decoder." };
            await File.WriteAllTextAsync(Path.Combine(run, "host-performance.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            if (!_closing) LabResults.Text = status == "completed"
                ? (playbackFirst ? "Playback baseline saved. Next: measure the other baseline videos, then close Mani. The collector terminal will run the independent comparisons." : "Performance samples saved. Playback position and pause state restored.")
                : "Performance probe did not complete. See host-performance.json for details.";
        }
        if (status != "completed") throw new InvalidOperationException("Performance report is incomplete. See host-performance.json.");
        return Path.Combine(run, "host-performance.json");
    }
    private async Task ShutdownProbesAsync()
    {
        _m1Cancellation?.Cancel();
        try { await _m1Task; }
        finally
        {
            try { CloseWebProbe(); await _webCommandTask; }
            finally
            {
                if (_display is not null) { _display.AdvancedColorInfoChanged -= Display_Changed; _display = null; }
            }
        }
    }
}

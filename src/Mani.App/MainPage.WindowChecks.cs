using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Mani.Playback;
using Mani_App.Diagnostics;
using Mani_App.Interop;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace Mani_App;

// IINA source/platform assessment: docs/m1/window-validation.md.
// Drives existing production window/layout paths; never repairs their output in the probe.
public sealed partial class MainPage
{
    private sealed record WindowCheckState(string Presenter, string State, int X, int Y, int Width, int Height);
    private WindowCheckState ReadCheckWindow()
    {
        var w = Owner!.AppWindow;
        return new(w.Presenter.Kind.ToString(), (w.Presenter as OverlappedPresenter)?.State.ToString() ?? "",
            w.Position.X, w.Position.Y, w.Size.Width, w.Size.Height);
    }
    private static bool WindowMatches(WindowCheckState actual, WindowCheckState expected) =>
        actual.Presenter == expected.Presenter && actual.State == expected.State &&
        Math.Abs(actual.X - expected.X) <= 2 && Math.Abs(actual.Y - expected.Y) <= 2 &&
        Math.Abs(actual.Width - expected.Width) <= 2 && Math.Abs(actual.Height - expected.Height) <= 2;

    private async void WindowChecks_Click(object sender, RoutedEventArgs e)
    {
        await RunProbeAsync(async cancellation =>
        {
            try { await RunWindowChecksAsync(cancellation); }
            catch (OperationCanceledException) { if (!_closing) WindowCheckStatus.Text = "Window check cancelled. Partial evidence saved."; throw; }
            catch (Exception error) { if (!_closing) WindowCheckStatus.Text = error.Message; throw; }
        });
    }

    private async void MinimizeChecks_Click(object sender, RoutedEventArgs e)
    {
        await RunProbeAsync(async cancellation =>
        {
            try { await RunWindowChecksAsync(cancellation, minimize: true); }
            catch (OperationCanceledException) { if (!_closing) WindowCheckStatus.Text = "Minimize check cancelled. Partial evidence saved."; throw; }
            catch (Exception error) { if (!_closing) WindowCheckStatus.Text = error.Message; throw; }
        });
    }

    private async void SessionLockChecks_Click(object sender, RoutedEventArgs e)
    {
        await RunProbeAsync(async cancellation =>
        {
            try { await RunWindowChecksAsync(cancellation, sessionLock: true); }
            catch (OperationCanceledException) { if (!_closing) WindowCheckStatus.Text = "Lock/unlock check cancelled. Partial evidence saved."; throw; }
            catch (Exception error) { if (!_closing) WindowCheckStatus.Text = error.Message; throw; }
        });
    }

    private async void DpiChecks_Click(object sender, RoutedEventArgs e) =>
        await RunProbeAsync(cancellation => RunWindowChecksAsync(cancellation, dpi: true));

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    private async Task RunWindowChecksAsync(CancellationToken cancellation, bool minimize = false, bool sessionLock = false, bool dpi = false, bool sleep = false, bool overlayWindow = false, string? outputDirectory = null)
    {
        if (Owner?.AppWindow.Presenter is not OverlappedPresenter presenter || presenter.State == OverlappedPresenterState.Minimized)
            throw new InvalidOperationException("Start in a visible window or maximized window, outside fullscreen.");
        if (_web is not null || _blurEnabled) throw new InvalidOperationException("Turn off WebView2 and GPU blur first.");
        if (overlayWindow && (minimize || sessionLock || dpi || sleep))
            throw new InvalidOperationException("Overlay window checks require the single-display resize sequence.");
        bool stateCycle = minimize || sessionLock || sleep;
        if (sleep && (_powerObserver?.Active != true || _powerObserver.Failure is not null))
            throw new InvalidOperationException("Sleep/wake notifications are unavailable. Restart Mani and inspect the logs.");
        await _sleepPauseTask;
        var session = _session!;
        var window = Owner.AppWindow;
        await _pollTask;
        _pendingSeek = _pendingVolume = null;
        var displayBefore = ReadSwitchDisplay();
        string monitor = DisplayText(displayBefore, "monitor"), kind = DisplayText(displayBefore, "kind");
        if (monitor.Length == 0 || kind is not ("HighDynamicRange" or "StandardDynamicRange" or "WideColorGamut"))
            throw new InvalidOperationException("Display information is unavailable.");
        bool previousForceSdr = await session.GetForceSdrOutputAsync();
        if (previousForceSdr) throw new InvalidOperationException("Disable the forced SDR test override first.");
        var configuration = await session.GetPropertiesAsync("ab-loop-a", "ab-loop-b", "loop-file", "loop-playlist",
            "vf", "glsl-shaders", "icc-profile", "icc-profile-auto", "target-lut", "speed", "mute", "volume", "audio-device", "glsl-shader-opts");
        if (new[] { "ab-loop-a", "ab-loop-b", "loop-file", "loop-playlist", "icc-profile-auto" }.Any(k => configuration[k] != "no") ||
            new[] { "vf", "glsl-shaders", "icc-profile", "target-lut" }.Any(k => configuration[k].Length != 0))
            throw new InvalidOperationException("Turn off loops, filters, shaders and custom ICC/LUT processing first.");
        var before = await session.ReadAsync();
        if (configuration["mute"] != "no" || before.Volume <= 0 ||
            !double.TryParse(configuration["speed"], System.Globalization.CultureInfo.InvariantCulture, out var speed) || speed != 1)
            throw new InvalidOperationException("Unmute playback, set a comfortable volume and use normal playback speed.");

        nint dpiWindow = dpi ? WinRT.Interop.WindowNative.GetWindowHandle(Owner) : 0;
        uint? originalWindowDpi = dpi ? GetDpiForWindow(dpiWindow) : null, finalWindowDpi = null;
        double originalScale = VideoPanel.CompositionScaleX;
        if (dpi && (originalWindowDpi is null or 0 || !double.IsFinite(originalScale) || originalScale <= 0 ||
            Math.Abs(originalWindowDpi.Value / 96d - originalScale) > 0.0001 ||
            Math.Abs(VideoPanel.CompositionScaleY - originalScale) > 0.0001))
            throw new InvalidOperationException("Wait for Windows DPI and panel scale to settle before starting.");
        var initialWindow = ReadCheckWindow();
        WindowCheckState? normalWindow = null, finalWindow = null;
        var area = DisplayArea.GetFromWindowId(window.Id, DisplayAreaFallback.Nearest);
        var work = area.WorkArea;
        var screen = area.OuterBounds;
        object? ReadMonitorBounds()
        {
            if (!dpi) return null;
            var bounds = DisplayArea.GetFromWindowId(window.Id, DisplayAreaFallback.Nearest).OuterBounds;
            return new { bounds.X, bounds.Y, bounds.Width, bounds.Height };
        }
        var originalDisplayBounds = ReadMonitorBounds();
        object? finalDisplayBounds = null;
        WindowCheckState Sized(double fraction) => new("Overlapped", "Restored",
            work.X + (work.Width - (int)(work.Width * fraction)) / 2,
            work.Y + (work.Height - (int)(work.Height * fraction)) / 2,
            (int)(work.Width * fraction), (int)(work.Height * fraction));
        var small = Sized(0.65); var large = Sized(0.85);
        if (!stateCycle && !dpi && (small.Width / VideoPanel.CompositionScaleX < 640 || small.Height / VideoPanel.CompositionScaleY < 360))
            throw new InvalidOperationException("The display work area is too small for this window check.");
        void MoveTo(WindowCheckState rect) => window.MoveAndResize(new RectInt32(rect.X, rect.Y, rect.Width, rect.Height));
        void RestoreWindow()
        {
            if (window.Presenter.Kind == AppWindowPresenterKind.FullScreen) Owner.ToggleFullscreen();
            var current = (OverlappedPresenter)window.Presenter;
            current.Restore();
            if (normalWindow is not null) MoveTo(normalWindow);
            if (initialWindow.State == "Maximized") current.Maximize();
        }

        string prefix = overlayWindow ? "overlay-window" : sleep ? "sleep" : dpi ? "dpi" : sessionLock ? "session-lock" : minimize ? "minimize" : "window";
        string suite = "visible-" + prefix + "-checks";
        string directory = outputDirectory ?? NewProbeDirectory(prefix + "-checks"), logPath = Path.Combine(_logDirectory, "mpv.log");
        Directory.CreateDirectory(directory);
        string shaderPath = Path.Combine(AppContext.BaseDirectory, "Assets", "m1-blur.glsl");
        string? shaderSha256 = overlayWindow ? Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(shaderPath, cancellation))) : null;
        string? webSha256 = overlayWindow ? Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Assets", "m1-web-probe.html"), cancellation))) : null;
        var oldLab = LabPanel.Visibility; var oldInfo = InfoPanel.Visibility; bool oldWebHitTest = WebProbeContainer.IsHitTestVisible;
        IReadOnlyDictionary<string, string>? finalOverlaySettings = null;
        long LogLength() { using var f = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); return f.Length; }
        long logStart = LogLength(), logEnd = logStart;
        string status = "running", mediaPath = "", mediaSha256 = "";
        string? failure = null, cleanupFailure = null, logCopyFailure = null;
        bool? finalPaused = null, finalForceSdr = null;
        bool windowRestoreSkippedForClose = false;
        JsonElement? displayAfter = null;
        IReadOnlyDictionary<string, string>? audioSettingsAfter = null;
        var stages = new List<object>(); var timeline = new List<object>();
        long fileLoadedBefore = before.FileLoadedCount;
        long? readyFileLoadedCount = null;
        int sampleId = 0;
        var whole = Stopwatch.StartNew();
        SessionLockObserver? lockObserver = null;
        SleepWakeSequence? sleepSequence = null;
        async Task SaveAsync()
        {
            var report = new { schema_version = 1, suite, status, failure, cleanupFailure, logCopyFailure,
                atUtc = DateTimeOffset.UtcNow, hostProcessId = Environment.ProcessId,
                hostAssemblyId = typeof(MainPage).Module.ModuleVersionId, playbackAssemblyId = typeof(PlaybackSession).Module.ModuleVersionId,
                dllSha256 = ProbeOptions("unused").DllSha256, mediaPath, mediaSha256, configuration, audioSettingsAfter,
                displayBefore, displayAfter, initialWindow, normalWindow, finalWindow, windowRestoreSkippedForClose,
                previousForceSdr, finalForceSdr, finalPaused, fileLoadedBefore, readyFileLoadedCount,
                stableSamplesRequired = 12, stages, timeline, logStart, logEnd,
                overlayWindow, shaderPath, shaderSha256, webSha256, finalOverlaySettings,
                finalWebOpen = _web is not null, finalWebChildren = WebProbeContainer.Children.Count, finalBlurEnabled = _blurEnabled,
                originalWindowDpi, finalWindowDpi, originalScale, originalDisplayBounds, finalDisplayBounds,
                powerNotifications = sleepSequence is null ? null : new {
                    events = sleepSequence.Events.ToArray(), failure = sleepSequence.Failure,
                    pauseResult = sleepSequence.PauseResult, pauseCompletedMs = sleepSequence.PauseCompletedMs,
                    confirmedMs = sleepSequence.ConfirmedMs, observerActive = _powerObserver?.Active,
                    observerFailure = _powerObserver?.Failure, diagnosticDetached = _sleepCheck is null },
                sessionNotifications = lockObserver is null ? null : new {
                    sessionId = lockObserver.Sequence.SessionId, events = lockObserver.Sequence.Events.ToArray(),
                    locked = lockObserver.Sequence.Locked, failure = lockObserver.Sequence.Failure,
                    released = lockObserver.Released, cleanupFailure = lockObserver.CleanupFailure },
                finishPolicy = "restore-window-leave-fixture-paused",
                limitations = dpi ? "Single display and recorded DPI sequence only. Readbacks are not visual, audible, mixed-monitor or DWM timing acceptance."
                    : "Current display/DPI only. Geometry and output readback are not visual, audible, cross-monitor, mixed-DPI or DWM timing acceptance." };
            string path = Path.Combine(directory, "result.json");
            await File.WriteAllTextAsync(path + ".tmp", JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(path + ".tmp", path, overwrite: true);
        }
        try
        {
            if (sleep) _sleepCheck = sleepSequence = new(() => whole.ElapsedMilliseconds);
            if (sessionLock) lockObserver = new(WinRT.Interop.WindowNative.GetWindowHandle(Owner), () => whole.ElapsedMilliseconds);
            await SaveAsync();
            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Assets", "display-media.json"), cancellation));
            var fixture = manifest.RootElement.GetProperty("hdr-av");
            mediaPath = Path.Combine(AppContext.BaseDirectory, "Assets", fixture.GetProperty("file").GetString()!);
            await using (var input = File.OpenRead(mediaPath)) mediaSha256 = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellation));
            if (!mediaSha256.Equals(fixture.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Window test video hash mismatch.");
            _notice = "";
            // Capture the normal restore bounds before changing size; retain maximized state separately.
            presenter.Restore();
            await Task.Delay(250, cancellation);
            normalWindow = ReadCheckWindow();
            if (normalWindow.State != "Restored") throw new InvalidOperationException("The window did not leave maximized state.");
            if (stateCycle || dpi) RestoreWindow();
            LabPanel.Visibility = Visibility.Collapsed;
            if (overlayWindow) { InfoPanel.Visibility = Visibility.Collapsed; WebProbeContainer.IsHitTestVisible = false; }
            WindowCheckBanner.Visibility = Visibility.Visible;
            long previousRestarts = await session.GetPlaybackRestartCountAsync();
            await session.OpenAsync(mediaPath, paused: false);
            // IINA 1.5.1 defaults pauseWhenMinimized/togglePipByMinimizingWindow to false.
            // Drive only window actions during minimize/restore. Pause/resume are explicit separate stages.
            // Assessment and Windows adaptation: docs/m1/minimize-validation.md.
            // IINA inactive/sleep policy assessment: docs/m1/session-lock-validation.md.
            // Locking never changes playback here; actual WTS events gate the diagnostic.
            // Reuse IINA backing-scale behavior and Mani's production pixel/transform path.
            // Only read Windows DPI; the user changes system scale. See docs/m1/dpi-validation.md.
            // IINA pause-on-sleep is the production PowerNotification path, not a probe pause.
            var plan = sleep
                ? new[] { ("initial", "Playing before sleep"),
                    ("awake-paused", "Choose Windows Start > Power > Sleep. Wait 10 seconds, wake and unlock, then click Continue after wake."),
                    ("resumed", "Testing picture and sound after wake") }
                : dpi
                ? new[] { ("initial", "Checking the original scale"),
                    ("dpi-100", "Set Windows display Scale to 100%. Keep resolution and HDR unchanged."),
                    ("dpi-150", "Set Windows display Scale to 150%. Keep resolution and HDR unchanged."),
                    ("dpi-200", "Set Windows display Scale to 200%. Keep resolution and HDR unchanged."),
                    ("dpi-return", $"Restore Windows display Scale to {originalScale * 100:F0}%. Keep the player visible.") }
                : sessionLock
                ? new[] { ("initial", "Playing before lock"),
                    ("locked-playing", "1/2: Press Win+L, wait 10 seconds, then unlock. Audio should continue."),
                    ("restored-playing", "Checking playback after unlock"), ("paused", "Pausing before second lock"),
                    ("locked-paused", "2/2: Press Win+L, wait 10 seconds, then unlock. Playback is intentionally paused."),
                    ("restored-paused", "Checking paused playback after unlock"), ("resumed", "Playing after unlock") }
                : minimize
                ? new[] { ("initial", "Playing before minimize"), ("minimized-playing", "Minimized while playing"),
                    ("restored-playing", "Restored while playing"), ("paused", "Paused before minimize"),
                    ("minimized-paused", "Minimized while paused"), ("restored-paused", "Restored while paused"),
                    ("resumed", "Playing after restore") }
                : overlayWindow
                ? new[] { ("initial", "Overlays in the initial window"), ("smaller", "Overlays in a smaller window"),
                    ("larger", "Overlays in a larger window"), ("fullscreen", "Overlays in fullscreen"),
                    ("restored", "Overlays after window restoration"), ("cleared", "Original window with overlays removed") }
                : new[] { ("initial", "Initial window"), ("smaller", "Smaller window"),
                    ("larger", "Larger window"), ("fullscreen", "Fullscreen"), ("restored", "Original window") };
            foreach (var (id, label) in plan)
            {
                cancellation.ThrowIfCancellationRequested();
                bool hidden = (minimize && id.StartsWith("minimized-", StringComparison.Ordinal)) ||
                    (sessionLock && id.StartsWith("locked-", StringComparison.Ordinal));
                bool paused = stateCycle && (id is "paused" or "minimized-paused" or "locked-paused" or "restored-paused" or "awake-paused");
                int priorEvents = id is "initial" or "locked-playing" ? 0 : id is "restored-playing" or "paused" or "locked-paused" ? 2 : 4;
                if (sleep && id == "awake-paused")
                {
                    sleepSequence!.Armed = true;
                    SleepContinueButton.Visibility = Visibility.Visible;
                    SleepContinueButton.IsEnabled = false;
                }
                WindowCheckProgress.Text = (overlayWindow ? "Combined checks 2: " : sleep ? "Sleep/wake checks: " : dpi ? "DPI checks: " : sessionLock ? "Lock/unlock checks: " : minimize ? "Minimize checks: " : "Window checks: ") + label;
                long stageLogStart = LogLength();
                var observations = new List<object>();
                string stageStatus = "running"; string? stageFailure = null;
                JsonElement? webSnapshot = null; double? webNativePosition = null;
                double? expectedScale = dpi ? id switch { "dpi-100" => 1, "dpi-150" => 1.5, "dpi-200" => 2, _ => originalScale } : null;
                var expected = dpi ? initialWindow : stateCycle ? (hidden && minimize ? initialWindow with { State = "Minimized" } : initialWindow)
                    : id switch {
                        "smaller" => small, "larger" => large,
                        "fullscreen" => new WindowCheckState("FullScreen", "", screen.X, screen.Y, screen.Width, screen.Height),
                        "restored" or "cleared" => initialWindow, _ => normalWindow };
                var clock = Stopwatch.StartNew(); long lastSave = 0;
                try
                {
                    if (overlayWindow && id == "initial")
                    {
                        await OpenWebProbeAsync(cancellation);
                        await SetBlurEnabledAsync(true, cancellation);
                    }
                    if (overlayWindow && id == "cleared")
                    {
                        CloseWebProbe(); await _webCommandTask;
                        await SetBlurEnabledAsync(false, cancellation);
                    }
                    if (stateCycle)
                    {
                        if (id == "paused") await session.SetAsync("pause", "yes");
                        else if (id == "resumed") await session.SetAsync("pause", "no");
                        else if (minimize && hidden) ((OverlappedPresenter)window.Presenter).Minimize();
                        else if (minimize && id.StartsWith("restored-", StringComparison.Ordinal)) ((OverlappedPresenter)window.Presenter).Restore();
                    }
                    else if (id is "smaller" or "larger") MoveTo(expected);
                    else if (id == "fullscreen") Owner.ToggleFullscreen();
                    else if (id == "restored") RestoreWindow();
                    while (true)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        await Task.Delay(250, cancellation);
                        await _pollTask;
                        if (!overlayWindow && (_web is not null || _blurEnabled)) throw new InvalidOperationException("WebView2 or GPU blur was enabled during the check.");
                        bool expectOverlay = overlayWindow && id != "cleared";
                        if (overlayWindow && ((_web is not null) != expectOverlay || _blurEnabled != expectOverlay || (expectOverlay && !_webReady)))
                            throw new InvalidOperationException("An overlay changed or stopped responding during window checks.");
                        if (sleepSequence is not null)
                        {
                            sleepSequence.RequireHealthy();
                            if (_powerObserver?.Active != true || _powerObserver.Failure is not null)
                                throw new InvalidOperationException("Power observer became unavailable.");
                            if (id == "initial" && sleepSequence.Events.Count != 0)
                                throw new InvalidOperationException("Wait for the sleep prompt before sleeping.");
                            if (id == "awake-paused")
                            {
                                SleepContinueButton.IsEnabled = sleepSequence.Complete && sleepSequence.PauseCompletedMs is not null && sleepSequence.ConfirmedMs is null;
                                if (clock.Elapsed.TotalSeconds > 180) throw new TimeoutException("No completed sleep/wake and confirmation within three minutes.");
                                // Do not query a suspended/recovering display or pretend it is presenting.
                                // Raw native logs and power events retain the unobserved interval.
                                if (sleepSequence.Events.Count > 0 && sleepSequence.ConfirmedMs is null)
                                {
                                    if (clock.ElapsedMilliseconds - lastSave >= 2000) { lastSave = clock.ElapsedMilliseconds; await SaveAsync(); }
                                    continue;
                                }
                            }
                        }
                        int? powerEventsBefore = sleepSequence?.Events.Count;
                        if (lockObserver is not null)
                        {
                            lockObserver.Sequence.RequireHealthy();
                            if (hidden && lockObserver.Sequence.CycleCompleted(priorEvents))
                            {
                                if (observations.Count != 12) throw new InvalidOperationException("Not enough stable samples while locked. Wait ten seconds before unlocking.");
                                stageStatus = "completed"; break;
                            }
                            if (!hidden && (lockObserver.Sequence.Events.Count != priorEvents || lockObserver.Sequence.Locked))
                                throw new InvalidOperationException("Lock Windows only when the banner asks you to.");
                        }
                        int? sessionEventsBefore = lockObserver?.Sequence.Events.Count;
                        var display = ReadSwitchDisplay();
                        if (!hidden && (DisplayText(display, "monitor") != monitor || DisplayText(display, "kind") != kind ||
                            (!dpi && (display.GetProperty("scaleX").GetDouble() != displayBefore.GetProperty("scaleX").GetDouble() ||
                            display.GetProperty("scaleY").GetDouble() != displayBefore.GetProperty("scaleY").GetDouble()))))
                            throw new InvalidOperationException(dpi ? "Keep the same display and Windows HDR setting. Change only display Scale."
                                : "Keep this check on the same display with unchanged Windows HDR and scale.");
                        var displayBounds = ReadMonitorBounds();
                        if (dpi && JsonSerializer.Serialize(displayBounds) != JsonSerializer.Serialize(originalDisplayBounds))
                            throw new InvalidOperationException("Keep the same display resolution and monitor layout during this scale check.");
                        uint? windowDpiBefore = dpi ? GetDpiForWindow(dpiWindow) : null;
                        var capture = await DisplaySignalProbe.CaptureAsync(session);
                        var state = capture.State;
                        if (state.LoadFailure.Length > 0) throw new IOException(state.LoadFailure);
                        if (readyFileLoadedCount is long loaded && state.FileLoadedCount != loaded)
                            throw new InvalidOperationException("The fixture was reloaded during the window check.");
                        var observedWindow = ReadCheckWindow();
                        double width = VideoPanel.ActualWidth * VideoPanel.CompositionScaleX;
                        double height = VideoPanel.ActualHeight * VideoPanel.CompositionScaleY;
                        object? transform = null;
                        bool geometryMatches = false;
                        using (var chain = await session.AcquireSwapChainAsync())
                        {
                            if (chain is not null && capture.Swapchain is not null)
                            {
                                var matrix = CompositionSwapChain.ReadTransform(chain.Pointer);
                                transform = new { matrix.M11, matrix.M12, matrix.M21, matrix.M22, matrix.M31, matrix.M32 };
                                var desc = JsonSerializer.SerializeToElement(capture.Swapchain);
                                geometryMatches = Math.Abs(desc.GetProperty("Width").GetDouble() - width) <= 1 &&
                                    Math.Abs(desc.GetProperty("Height").GetDouble() - height) <= 1 &&
                                    Math.Abs(matrix.M11 * VideoPanel.CompositionScaleX - 1) < 0.0001 &&
                                    Math.Abs(matrix.M22 * VideoPanel.CompositionScaleY - 1) < 0.0001 &&
                                    matrix.M12 == 0 && matrix.M21 == 0 && matrix.M31 == 0 && matrix.M32 == 0;
                            }
                        }
                        uint? windowDpi = dpi ? GetDpiForWindow(dpiWindow) : null;
                        bool dpiReady = !dpi || (windowDpi == windowDpiBefore && windowDpi == (uint)Math.Round(expectedScale!.Value * 96) &&
                            Math.Abs(display.GetProperty("scaleX").GetDouble() - expectedScale.Value) < 0.0001 &&
                            Math.Abs(display.GetProperty("scaleY").GetDouble() - expectedScale.Value) < 0.0001 &&
                            Math.Abs(VideoPanel.CompositionScaleX - expectedScale.Value) < 0.0001 &&
                            Math.Abs(VideoPanel.CompositionScaleY - expectedScale.Value) < 0.0001);
                        if (dpi && id != "initial" && dpiReady && !WindowMatches(observedWindow, expected))
                        {
                            // Windows may resize/reposition the window on a DPI change. Observe its result;
                            // require twelve consistent samples without forcing a window size in the probe.
                            observations.Clear(); expected = observedWindow;
                        }
                        bool attached = _bridge?.HasAttachedSwapChain == true && width > 0 && height > 0;
                        bool ready = state.FileLoadedCount > fileLoadedBefore && state.ColorPrimaries == "bt.2020" && state.Transfer == "pq" &&
                            UnattendedPlaybackReadiness.IsReady(state, mediaPath, hidden || attached,
                                await session.GetPlaybackRestartCountAsync() > previousRestarts, expectedPaused: paused);
                        // Minimized layout may have no usable surface/monitor rectangle. Observe it without
                        // claiming presentation; restored stages must recover the actual geometry/color output.
                        bool windowReady = hidden
                            ? sessionLock ? lockObserver!.Sequence.Locked && lockObserver.Sequence.Events.Count == priorEvents + 1
                                : observedWindow.Presenter == "Overlapped" && observedWindow.State == "Minimized"
                            : geometryMatches && WindowMatches(observedWindow, expected) &&
                                state.ColorOutput?.Hdr == (kind == "HighDynamicRange") && DisplaySignalProbe.OutputMatchesPolicy(capture);
                        bool sessionEventsStable = sessionEventsBefore == lockObserver?.Sequence.Events.Count;
                        bool powerEventsStable = powerEventsBefore == sleepSequence?.Events.Count;
                        bool sleepReady = sleepSequence is null || (id == "initial" ? sleepSequence.Events.Count == 0
                            : sleepSequence.Complete && sleepSequence.ConfirmedMs is not null);
                        bool stable = ready && windowReady && dpiReady && sessionEventsStable && sleepReady && powerEventsStable &&
                            (!dpi || (observedWindow.Presenter == "Overlapped" && observedWindow.State == initialWindow.State)) &&
                            !state.PausedForCache && state.AudioOutput.Length > 0 && state.AudioOutput != "null";
                        IReadOnlyDictionary<string, string>? properties = null;
                        var bounds = Controls.TransformToVisual(VideoPanel).TransformBounds(new Windows.Foundation.Rect(0, 0, Controls.ActualWidth, Controls.ActualHeight));
                        if (overlayWindow)
                        {
                            properties = await session.GetPropertiesAsync("glsl-shaders", "glsl-shader-opts", "osd-dimensions");
                            if (expectOverlay)
                            {
                                string expectedMask = properties["osd-dimensions"].Length == 0 ? "" : CalculateBlurParameters(properties["osd-dimensions"]);
                                stable &= expectedMask.Length > 0 && _blurStrength == 1 && _blurParameters == expectedMask &&
                                    properties["glsl-shader-opts"] == expectedMask && properties["glsl-shaders"] == shaderPath;
                            }
                            else stable &= properties["glsl-shaders"].Length == 0 && _blurStrength == 0;
                        }
                        int index = ++sampleId; long elapsedMs = whole.ElapsedMilliseconds; var atUtc = DateTimeOffset.UtcNow;
                        // Full captures in the timeline allow independent review of failed/transitional geometry.
                        var observation = new { sampleId = index, stage = id, atUtc, elapsedMs, display,
                            window = observedWindow, panelAttached = attached, panelWidth = width, panelHeight = height,
                            panelDipWidth = VideoPanel.ActualWidth, panelDipHeight = VideoPanel.ActualHeight,
                            ready, stable, geometryMatches, transform, capture, windowDpiBefore, windowDpi, displayBounds,
                            properties, controls = new { bounds.X, bounds.Y, bounds.Width, bounds.Height },
                            webOpen = _web is not null, webReady = _webReady, webChildren = WebProbeContainer.Children.Count, webGeneration = _webGeneration,
                            blurEnabled = _blurEnabled, blurStrength = _blurStrength, blurParameters = _blurParameters,
                            powerEventCount = sleepSequence?.Events.Count, powerEventsBefore, powerEventsStable,
                            wakeConfirmed = sleepSequence?.ConfirmedMs is not null,
                            sessionLocked = lockObserver?.Sequence.Locked, sessionEventCount = lockObserver?.Sequence.Events.Count, sessionEventsStable };
                        timeline.Add(observation);
                        // Keep the last twelve consecutive locked samples until the actual unlock event.
                        // An unlock delivered during an awaited capture must not erase the completed window.
                        bool unlockedDuringCapture = sessionLock && hidden && lockObserver!.Sequence.CycleCompleted(priorEvents);
                        if (!unlockedDuringCapture)
                        {
                            if (stable)
                            {
                                readyFileLoadedCount ??= state.FileLoadedCount; observations.Add(observation);
                                if (sessionLock && hidden && observations.Count > 12) observations.RemoveAt(0);
                            }
                            else observations.Clear();
                        }
                        if (observations.Count == 12 && !(sessionLock && hidden))
                        {
                            if (expectOverlay)
                            {
                                string raw = await _web!.CoreWebView2.ExecuteScriptAsync("JSON.stringify({position:window.maniProbe?.state?.position,paused:window.maniProbe?.state?.paused,width:innerWidth,height:innerHeight})")
                                    .AsTask(cancellation).WaitAsync(TimeSpan.FromSeconds(10), cancellation);
                                using var dom = JsonDocument.Parse(JsonSerializer.Deserialize<string>(raw)!);
                                webSnapshot = dom.RootElement.Clone();
                                webNativePosition = (await session.ReadAsync()).Position;
                                if (webSnapshot.Value.GetProperty("paused").GetBoolean() ||
                                    Math.Abs(webSnapshot.Value.GetProperty("position").GetDouble() - webNativePosition.Value) > 1 ||
                                    webSnapshot.Value.GetProperty("width").GetDouble() <= 0 || webSnapshot.Value.GetProperty("height").GetDouble() <= 0)
                                    throw new InvalidOperationException("Web playback state did not follow the resized window.");
                            }
                            stageStatus = "completed"; break;
                        }
                        if (clock.Elapsed.TotalSeconds > (sleep && id == "awake-paused" ? 180 : sessionLock && hidden ? 180 : dpi && id != "initial" ? 90 : 20)) throw new TimeoutException("Timed out waiting for " + id + ". Partial evidence saved.");
                        if (clock.ElapsedMilliseconds - lastSave >= 2000) { lastSave = clock.ElapsedMilliseconds; await SaveAsync(); }
                    }
                }
                catch (OperationCanceledException) { stageStatus = "cancelled"; throw; }
                catch (Exception error) { stageStatus = "failed"; stageFailure = error.ToString(); throw; }
                finally
                {
                    stages.Add(new { Id = id, status = stageStatus, failure = stageFailure, expectedWindow = expected, expectedScale,
                        observations, webSnapshot, webNativePosition, logStart = stageLogStart, logEnd = LogLength() });
                    await SaveAsync();
                }
            }
            status = "completed";
        }
        catch (OperationCanceledException) { status = "cancelled"; throw; }
        catch (Exception error) { status = "failed"; failure = error.ToString(); throw; }
        finally
        {
            if (sleepSequence is not null)
            {
                await _sleepPauseTask;
                _sleepCheck = null;
                SleepContinueButton.Visibility = Visibility.Collapsed;
                SleepContinueButton.IsEnabled = false;
                if (sleepSequence.Failure is not null || _powerObserver?.Failure is not null)
                { cleanupFailure = sleepSequence.Failure ?? _powerObserver?.Failure; status = "failed"; }
            }
            if (lockObserver is not null)
            {
                lockObserver.Dispose();
                if (!lockObserver.Released || lockObserver.CleanupFailure is not null || lockObserver.Sequence.Failure is not null)
                { cleanupFailure = lockObserver.CleanupFailure ?? lockObserver.Sequence.Failure ?? "Session observer was not released."; status = "failed"; }
            }
            if (overlayWindow)
            {
                _webCheckAcceptsCommands = false;
                async Task CleanOverlayAsync(Func<Task> action)
                {
                    try { await action(); }
                    catch (Exception error) { cleanupFailure ??= error.ToString(); status = "failed"; }
                }
                await CleanOverlayAsync(async () => { CloseWebProbe(); await _webCommandTask; });
                await CleanOverlayAsync(() => SetBlurEnabledAsync(false, CancellationToken.None));
                await CleanOverlayAsync(() => session.SetAsync("glsl-shaders", configuration["glsl-shaders"]));
                await CleanOverlayAsync(() => session.SetAsync("glsl-shader-opts", configuration["glsl-shader-opts"]));
                _blurParameters = "";
                await CleanOverlayAsync(async () =>
                {
                    finalOverlaySettings = await session.GetPropertiesAsync("glsl-shaders", "glsl-shader-opts");
                    if (finalOverlaySettings.Any(kv => configuration[kv.Key] != kv.Value) || _web is not null || _blurEnabled || WebProbeContainer.Children.Count != 0)
                        throw new InvalidOperationException("Overlay cleanup did not restore the original settings.");
                });
            }
            try
            {
                await session.SetAsync("pause", "yes");
                finalPaused = (await session.ReadAsync()).Paused;
                finalForceSdr = await session.GetForceSdrOutputAsync();
                audioSettingsAfter = await session.GetPropertiesAsync("mute", "volume", "speed", "audio-device");
                if (finalPaused != true || finalForceSdr != previousForceSdr || audioSettingsAfter.Any(kv => configuration[kv.Key] != kv.Value))
                    throw new InvalidOperationException("Playback settings were not preserved during cleanup.");
            }
            catch (Exception error) { cleanupFailure ??= error.ToString(); status = "failed"; }
            try
            {
                if (_closing) windowRestoreSkippedForClose = true;
                else
                {
                    RestoreWindow();
                    await Task.Delay(250);
                    finalWindow = ReadCheckWindow();
                    displayAfter = ReadSwitchDisplay();
                    finalWindowDpi = dpi ? GetDpiForWindow(dpiWindow) : null;
                    finalDisplayBounds = ReadMonitorBounds();
                    if (dpi && (JsonSerializer.Serialize(finalDisplayBounds) != JsonSerializer.Serialize(originalDisplayBounds) || finalWindowDpi != originalWindowDpi ||
                        Math.Abs(VideoPanel.CompositionScaleX - originalScale) > 0.0001 || Math.Abs(VideoPanel.CompositionScaleY - originalScale) > 0.0001))
                        throw new InvalidOperationException($"Restore Windows display Scale to {originalScale * 100:F0}% manually. The diagnostic does not change system settings.");
                    if (!WindowMatches(finalWindow, initialWindow)) throw new InvalidOperationException("The original window state was not restored.");
                    WindowCheckBanner.Visibility = Visibility.Collapsed;
                    LabPanel.Visibility = overlayWindow ? oldLab : Visibility.Visible;
                }
            }
            catch (Exception error) { cleanupFailure ??= error.ToString(); status = "failed"; }
            try
            {
                await using var source = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                logEnd = source.Length; source.Position = logStart;
                await using var copy = File.Create(Path.Combine(directory, "mpv-" + prefix + "-checks.log"));
                byte[] buffer = new byte[65536];
                for (long remaining = logEnd - logStart; remaining > 0;)
                {
                    int count = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)));
                    if (count == 0) throw new EndOfStreamException("The window check log changed while copying.");
                    await copy.WriteAsync(buffer.AsMemory(0, count)); remaining -= count;
                }
            }
            catch (Exception error) { logCopyFailure = error.ToString(); status = "failed"; }
            await SaveAsync();
            if (!_closing)
            {
                WindowCheckBanner.Visibility = Visibility.Collapsed;
                LabPanel.Visibility = overlayWindow ? oldLab : Visibility.Visible;
                WindowCheckStatus.Text = status == "completed"
                    ? (sleep ? "Sleep/wake evidence saved. Sample paused. Report the picture and sound after wake; silence during sleep and the paused stage is expected."
                        : dpi ? "DPI evidence saved. Original scale and window restored; sample paused. Report any cropping or unexpected audio interruption."
                        : sessionLock ? "Lock/unlock evidence saved. Original window restored; sample paused. Report any missing picture or unexpected audio interruption."
                        : minimize ? "Minimize evidence saved. Original window restored; sample paused. Report any missing picture or unexpected audio interruption."
                        : "Window evidence saved. Original window restored; sample paused. Report any cropping, trails or audio interruptions.")
                    : dpi ? $"DPI check incomplete. Restore Windows display Scale to {originalScale * 100:F0}% manually. Partial evidence saved; see the report."
                        : "Window check incomplete. Partial evidence saved; see the report for restoration details.";
                if (overlayWindow) { InfoPanel.Visibility = oldInfo; WebProbeContainer.IsHitTestVisible = oldWebHitTest; }
                LabResults.Text = WindowCheckStatus.Text;
            }
        }
    }
}

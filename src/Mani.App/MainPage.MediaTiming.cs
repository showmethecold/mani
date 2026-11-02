using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Mani.Playback;
using Mani_App.Diagnostics;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace Mani_App;

public sealed partial class MainPage
{
    private MediaTimingRequest? _mediaTimingRequest;
    private string _mediaTimingTests = "", _mediaTimingDirectory = "", _mediaTimingStatus = "starting", _mediaTimingStage = "starting";
    private string? _mediaTimingFailure, _mediaTimingEnvironment;
    private object? _mediaTimingBuild, _mediaTimingWindowPreparation;
    private long _mediaTimingCreation;
    private readonly List<object> _mediaTimingOperations = [], _mediaTimingCapabilities = [], _mediaTimingIdentities = [];
    private bool _mediaTimingFinalPaused;
    private long? _mediaTimingTraceReadyQpc, _mediaTimingTraceReleasedQpc, _mediaTimingTraceStopQpc;
    private FirstOpenTraceGate? _mediaTimingTraceGate;
    private Interop.SessionLockObserver? _mediaTimingSessionObserver;

    private void InitializeMediaTimingIfRequested()
    {
        if (!DiagnosticsBuild.Enabled) return;
        string tests = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mani", "Tests");
        if (!File.Exists(Path.Combine(tests, "pending-media-timing.json"))) return;
        _mediaTimingRequest = MediaTimingRequest.Claim(tests);
        _mediaTimingTests = tests;
        _mediaTimingDirectory = Path.Combine(_logDirectory, "media-timing-" + _mediaTimingRequest.automationId);
        Directory.CreateDirectory(_mediaTimingDirectory);
        using var process = Process.GetCurrentProcess();
        _mediaTimingCreation = process.StartTime.ToUniversalTime().ToFileTimeUtc();
        static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }
        _mediaTimingBuild = new { hostSha256 = Hash(typeof(MainPage).Assembly.Location),
            playbackSha256 = Hash(typeof(PlaybackSession).Assembly.Location), libmpvSha256 = Hash(Path.Combine(AppContext.BaseDirectory, "libmpv-2.dll")) };
        SaveMediaTiming();
    }

    private void FailMediaTimingStartup(Exception error)
    {
        if (_mediaTimingRequest is null) return;
        _mediaTimingStatus = "failed"; _mediaTimingStage = "startup-failed";
        _mediaTimingFailure = error.ToString(); SaveMediaTiming();
    }

    private bool StartMediaTimingIfRequested()
    {
        if (!DiagnosticsBuild.Enabled) return false;
        if (_mediaTimingRequest is null) return false;
        _ = RunProbeAsync(RunMediaTimingAsync);
        return true;
    }

    private void SaveMediaTiming()
    {
        if (_mediaTimingRequest is null) return;
        var identity = new { schema_version = 1, _mediaTimingRequest.automationId,
            scope = _mediaTimingRequest.IsFirstOpenTrace ? MediaTimingRequest.FirstOpenTraceScope : "full-media-timing",
            status = _mediaTimingStatus, stage = _mediaTimingStage, failure = _mediaTimingFailure,
            hostProcessId = Environment.ProcessId, processCreationFileTime = _mediaTimingCreation,
            sessionDirectoryName = Path.GetFileName(_logDirectory), runDirectoryName = Path.GetFileName(_mediaTimingDirectory),
            completedOperations = _mediaTimingOperations.Count, finalPaused = _mediaTimingFinalPaused };
        StartupProbe.WriteAtomic(Path.Combine(_mediaTimingDirectory, "result.json"), new {
            schema_version = 1, graphicsDeviceSchemaVersion = 1, identity, build = _mediaTimingBuild, request = _mediaTimingRequest,
            media = _mediaTimingIdentities, operations = _mediaTimingOperations, capabilities = _mediaTimingCapabilities,
            windowPreparation = _mediaTimingWindowPreparation,
            trace = _mediaTimingRequest.IsFirstOpenTrace ? new { readyQpc = _mediaTimingTraceReadyQpc,
                releasedQpc = _mediaTimingTraceReleasedQpc, stopRequestedQpc = _mediaTimingTraceStopQpc, gate = _mediaTimingTraceGate } : null,
            environment = _mediaTimingEnvironment is null ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(_mediaTimingEnvironment),
            sessionEvents = _mediaTimingSessionObserver?.Sequence.Events,
            sessionFailure = _mediaTimingSessionObserver?.Sequence.Failure,
            sessionObserverReleased = _mediaTimingSessionObserver?.Released,
            clockFrequency = Stopwatch.Frequency, fullM1Acceptance = false, performanceAccepted = false,
            firstMediaFrameIdentified = false,
            limitations = new[] { "Client-observed restart timing, not DWM first media frame or process startup.",
                "Files are SHA-256 verified before timing; filesystem cache is uncontrolled and may be warm.",
                _mediaTimingRequest.IsFirstOpenTrace ? "One traced 1080p first open only; not the 18-operation protocol or an untraced performance budget sample."
                    : "Three samples per condition; no population percentiles or performance budget acceptance.",
                "Read-only DXGI capability is sampled separately during playback; no media frame identity.",
                "Graphics identity and node-0 process memory budgets are read at capability boundaries only; no GPU utilization, power or allocation ownership.",
                "Window/display guards are sampled every 100 ms; native log offsets are client boundaries and logs may flush later." }
        });
        StartupProbe.WriteAtomic(Path.Combine(_mediaTimingTests, _mediaTimingRequest.automationId + ".media-timing-status.json"), identity);
    }

    private string MediaTimingEnvironment()
    {
        if (Owner is null || !Owner.AppWindow.IsVisible || VideoPanel.ActualWidth <= 0 || VideoPanel.ActualHeight <= 0 ||
            Owner.AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized })
            throw new InvalidOperationException("The player must remain visible and not minimized.");
        return JsonSerializer.Serialize(new { size = new { Owner.AppWindow.Size.Width, Owner.AppWindow.Size.Height },
            position = new { Owner.AppWindow.Position.X, Owner.AppWindow.Position.Y },
            presenter = Owner.AppWindow.Presenter.Kind.ToString(),
            windowState = Owner.AppWindow.Presenter is OverlappedPresenter p ? p.State.ToString() : "other",
            panel = new { VideoPanel.ActualWidth, VideoPanel.ActualHeight, VideoPanel.CompositionScaleX, VideoPanel.CompositionScaleY },
            display = DisplayDetails(), webEnabled = _web is not null, blurEnabled = _blurEnabled });
    }

    private async Task PrepareMediaTimingWindowAsync(CancellationToken cancellation)
    {
        if (_mediaTimingRequest?.referenceEnvironment is not JsonElement expected) return;
        _mediaTimingStage = "preparing-reference-window";
        SaveMediaTiming();
        await _pollTask;
        LabPanel.Visibility = InfoPanel.Visibility = Visibility.Collapsed;
        var before = JsonSerializer.Deserialize<JsonElement>(MediaTimingEnvironment());
        var target = TimingWindowPreparation.Read(expected);
        if (Owner!.AppWindow.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Restored })
            throw new InvalidOperationException("Start the collector in a restored window.");
        var window = Owner.AppWindow;
        var work = DisplayArea.GetFromWindowId(window.Id, DisplayAreaFallback.Nearest).WorkArea;
        TimingWindowPreparation.RequireSameWorkArea(target,
            before.GetProperty("display").GetProperty("monitor").GetString()!, work.X, work.Y, work.Width, work.Height);
        long startQpc = Stopwatch.GetTimestamp();
        window.MoveAndResize(new Windows.Graphics.RectInt32(target.X, target.Y, target.Width, target.Height));
        int stableSamples = 0;
        while (Stopwatch.GetElapsedTime(startQpc).TotalSeconds < 10)
        {
            await Task.Delay(100, cancellation);
            var actual = JsonSerializer.Deserialize<JsonElement>(MediaTimingEnvironment());
            stableSamples = TimingWindowPreparation.Matches(actual, expected) ? stableSamples + 1 : 0;
            if (stableSamples < 5) continue;
            _mediaTimingWindowPreparation = new { schema_version = 1, before, requested = expected, after = actual,
                startQpc, readyQpc = Stopwatch.GetTimestamp(), frequency = Stopwatch.Frequency, stableSamples };
            SaveMediaTiming();
            return;
        }
        throw new InvalidOperationException("Reference window/display conditions did not settle before measurement.");
    }

    private bool MediaTimingHeartbeatReady()
    {
        string token = _mediaTimingRequest!.automationId;
        if (File.Exists(Path.Combine(_mediaTimingTests, token + ".cancel"))) throw new OperationCanceledException("Collector stopped.");
        string path = Path.Combine(_mediaTimingTests, token + ".media-timing-heartbeat.json");
        if (!File.Exists(path)) return false;
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var doc = JsonDocument.Parse(file);
        var root = doc.RootElement;
        if (root.GetProperty("automationId").GetString() != token ||
            root.GetProperty("hostProcessId").GetInt32() != Environment.ProcessId ||
            root.GetProperty("processCreationFileTime").GetInt64() != _mediaTimingCreation)
            throw new InvalidDataException("Collector heartbeat identity differs.");
        double age = (DateTimeOffset.UtcNow - root.GetProperty("atUtc").GetDateTimeOffset()).TotalSeconds;
        if (_mediaTimingRequest.IsFirstOpenTrace) _mediaTimingRequest.traceController!.RequireAlive();
        return age is >= -5 and <= 10;
    }

    private async Task WaitForFirstOpenTraceAsync(CancellationToken cancellation)
    {
        if (!_mediaTimingRequest!.IsFirstOpenTrace) return;
        var before = await _session!.ReadAsync();
        if (!before.Idle || before.FileLoadedCount != 0)
            throw new InvalidOperationException("Trace requires the first media file in a fresh player.");
        _mediaTimingTraceReadyQpc = Stopwatch.GetTimestamp();
        _mediaTimingStage = "trace-ready";
        SaveMediaTiming(); // Full hash, window preparation, settle and settings reads have finished.
        string path = Path.Combine(_mediaTimingTests, _mediaTimingRequest.automationId + ".trace-gate.json");
        while (!File.Exists(path)) await Task.Delay(50, cancellation);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        _mediaTimingTraceGate = JsonSerializer.Deserialize<FirstOpenTraceGate>(file)
            ?? throw new InvalidDataException("Empty trace gate.");
        _mediaTimingTraceGate.Validate(_mediaTimingRequest, new(Environment.ProcessId, _mediaTimingCreation),
            Stopwatch.GetTimestamp(), _mediaTimingTraceReadyQpc.Value);
        if (MediaTimingEnvironment() != _mediaTimingEnvironment || !MediaTimingHeartbeatReady())
            throw new InvalidOperationException("Trace preflight conditions expired.");
        before = await _session.ReadAsync();
        if (!before.Idle || before.FileLoadedCount != 0)
            throw new InvalidOperationException("Media was opened while waiting for the trace.");
        _mediaTimingTraceReleasedQpc = Stopwatch.GetTimestamp();
        // Leave enough time for the existing 20-second open deadline and stop.
        if (_mediaTimingTraceGate.deadlineQpc - _mediaTimingTraceReleasedQpc < 25 * Stopwatch.Frequency)
            throw new TimeoutException("Too little recording time remains to open media.");
        _mediaTimingStage = "1080p/open-1-traced";
    }

    private void StopFirstOpenTrace()
    {
        if (_mediaTimingRequest?.IsFirstOpenTrace != true || _mediaTimingTraceStopQpc is not null) return;
        _mediaTimingTraceStopQpc = Stopwatch.GetTimestamp();
        StartupProbe.WriteAtomic(Path.Combine(_mediaTimingTests, _mediaTimingRequest.automationId + ".trace-stop.json"), new {
            scope = MediaTimingRequest.FirstOpenTraceScope, _mediaTimingRequest.automationId,
            hostProcessId = Environment.ProcessId, processCreationFileTime = _mediaTimingCreation,
            qpc = _mediaTimingTraceStopQpc, frequency = Stopwatch.Frequency });
    }

    private async Task WatchMediaTimingAsync(CancellationTokenSource deadline, CancellationToken stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                _mediaTimingSessionObserver?.Sequence.RequireHealthy();
                if (_mediaTimingSessionObserver?.Sequence.Events.Count > 0)
                    throw new InvalidOperationException("Session lock/unlock occurred during collection.");
                if (!MediaTimingHeartbeatReady()) throw new IOException("Collector heartbeat expired.");
                if (_mediaTimingEnvironment is not null && MediaTimingEnvironment() != _mediaTimingEnvironment)
                    throw new InvalidOperationException("Window, display, or overlay changed during collection.");
                if (_notice.Length > 0) throw new InvalidOperationException("Host error: " + _notice);
                await Task.Delay(100, stop);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception error) { _mediaTimingFailure ??= error.ToString(); deadline.Cancel(); }
    }

    private sealed record TimingPresentation(long ObservedQpc, bool Attached,
        PresentationStatistics.ReadResult? Statistics, object? Description);
    private async Task<TimingPresentation> ReadTimingPresentationAsync()
    {
        using var lease = await _session!.AcquireSwapChainAsync();
        bool attached = lease is not null && _bridge?.IsAttachedTo(lease.Pointer) == true;
        return new(Stopwatch.GetTimestamp(), attached, attached ? PresentationStatistics.Read(lease!.Pointer) : null,
            attached ? CompositionSwapChain.ReadDescription(lease!.Pointer) : null);
    }

    private async Task<GraphicsDeviceInfo.Result> ReadTimingGraphicsAsync()
    {
        using var lease = await _session!.AcquireSwapChainAsync();
        if (lease is null || _bridge?.IsAttachedTo(lease.Pointer) != true)
            throw new InvalidOperationException("Graphics observation requires the attached swapchain.");
        return GraphicsDeviceInfo.Read(lease.Pointer);
    }

    private long MediaTimingLogOffset() => File.Exists(Path.Combine(_logDirectory, "mpv.log"))
        ? new FileInfo(Path.Combine(_logDirectory, "mpv.log")).Length : 0;

    private async Task RunMediaTimingAsync(CancellationToken shutdown)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        deadline.CancelAfter(TimeSpan.FromMinutes(8));
        var cancellation = deadline.Token;
        using var watchStop = new CancellationTokenSource();
        Task watch = Task.CompletedTask;
        var streams = new List<FileStream>();
        var session = _session!;
        try
        {
            var wait = Stopwatch.StartNew();
            while (!MediaTimingHeartbeatReady())
            {
                if (wait.Elapsed.TotalSeconds > 30) throw new TimeoutException("Collector did not acknowledge startup.");
                await Task.Delay(100, cancellation);
            }
            var sessionClock = Stopwatch.StartNew();
            _mediaTimingSessionObserver = new(WinRT.Interop.WindowNative.GetWindowHandle(Owner!), () => sessionClock.ElapsedMilliseconds);
            watch = WatchMediaTimingAsync(deadline, watchStop.Token);
            _mediaTimingStatus = "running";
            await PrepareMediaTimingWindowAsync(cancellation);
            _mediaTimingStage = "verifying-media";
            SaveMediaTiming();
            foreach (var media in _mediaTimingRequest!.media)
            {
                var stream = new FileStream(media.path, FileMode.Open, FileAccess.Read, FileShare.Read,
                    1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                streams.Add(stream); // Retain read-only leases to prevent replacement during the batch.
                if (stream.Length != media.length) throw new InvalidDataException("Media length differs: " + media.id);
                string hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellation)).ToLowerInvariant();
                if (hash != media.sha256) throw new InvalidDataException("Media SHA-256 differs: " + media.id);
                _mediaTimingIdentities.Add(new { media.id, media.path, media.length, sha256 = hash, verifiedAtUtc = DateTimeOffset.UtcNow });
            }
            if (_web is not null || _blurEnabled || !(await session.ReadAsync()).Idle)
                throw new InvalidOperationException("A fresh idle player with overlays disabled is required.");
            LabPanel.Visibility = InfoPanel.Visibility = Visibility.Collapsed;
            await _pollTask;
            _pendingSeek = null; _pendingVolume = null;
            await SyncDisplayColorAsync();
            await Task.Delay(700, cancellation);
            _mediaTimingEnvironment = MediaTimingEnvironment();
            if (_mediaTimingRequest.referenceEnvironment is JsonElement expected &&
                !TimingWindowPreparation.Matches(JsonSerializer.Deserialize<JsonElement>(_mediaTimingEnvironment), expected))
                throw new InvalidOperationException("Reference window/display changed before measurement.");
            foreach (var media in _mediaTimingRequest.media)
            {
                double duration = 180;
                IReadOnlyDictionary<string, string>? referenceSettings = null;
                var plan = _mediaTimingRequest.Plan(duration);
                foreach (var step in plan)
                {
                    cancellation.ThrowIfCancellationRequested();
                    _mediaTimingStage = media.id + "/" + step.Id;
                    SaveMediaTiming();
                    var state = await session.ReadAsync();
                    if (!state.Idle && state.Paused != step.Paused) await session.SetAsync("pause", step.Paused ? "yes" : "no");
                    await MediaTimingProbe.SettleAsync(session, state.Idle ? null : state.Path, step.Paused, cancellation);
                    var settingsBefore = await session.GetPropertiesAsync(OverheadSettings.Names);
                    var presentationBefore = await ReadTimingPresentationAsync();
                    long nativeStart = MediaTimingLogOffset();
                    await WaitForFirstOpenTraceAsync(cancellation);
                    var operation = await MediaTimingProbe.MeasureAsync(session, media.path, step, MediaTimingLogOffset, cancellation,
                        _mediaTimingRequest.IsFirstOpenTrace ? StopFirstOpenTrace : null);
                    // Save every trace before a later window/settings/metadata check can fail.
                    var entryPath = Path.Combine(_mediaTimingDirectory, media.id + "-" + step.Id + ".json");
                    StartupProbe.WriteAtomic(entryPath, operation);
                    var presentationAfter = await ReadTimingPresentationAsync();
                    var settingsAfter = await session.GetPropertiesAsync(OverheadSettings.Names);
                    var boundary = new List<string>();
                    if (operation.Status != "measured") boundary.Add(operation.Failure ?? "Timing blocked.");
                    if (step.Operation == "seek" && (!presentationBefore.Attached || !presentationAfter.Attached))
                        boundary.Add("Seek presentation boundary was not attached to the visible panel.");
                    if (MediaTimingEnvironment() != _mediaTimingEnvironment) boundary.Add("Window/display changed.");
                    if (referenceSettings is not null) boundary.AddRange(OverheadSettings.Changes(referenceSettings, settingsAfter));
                    if (step.Operation == "seek") boundary.AddRange(OverheadSettings.Changes(settingsBefore, settingsAfter));
                    var after = operation.After!;
                    if (after.VideoOutput != "gpu-next" || after.HardwareDecoder != "d3d11va" || after.AudioOutput != "wasapi")
                        boundary.Add("Unexpected video/decode/audio path.");
                    if (after.Duration < 180) boundary.Add("Real movie duration is shorter than three minutes.");
                    using (var metadata = JsonDocument.Parse(settingsAfter["video-params"]))
                    {
                        int width = metadata.RootElement.GetProperty("w").GetInt32();
                        int height = metadata.RootElement.GetProperty("h").GetInt32();
                        if (width < (media.id == "4k" ? 3840 : 1920) || height < (media.id == "4k" ? 1600 : 800))
                            boundary.Add("Unexpected real-media dimensions.");
                    }
                    referenceSettings ??= settingsAfter;
                    var record = new { mediaId = media.id, operation,
                        openContext = step.Operation == "open" ? (step.Id == "open-1" ? (media.id == "1080p" ? "first-in-process" : "first-for-media") : "same-process-reopen") : null,
                        settingsBefore, settingsAfter, presentationBefore, presentationAfter,
                        environment = JsonSerializer.Deserialize<JsonElement>(_mediaTimingEnvironment),
                        nativeLogStart = nativeStart, nativeLogEnd = MediaTimingLogOffset(), blockers = boundary,
                        status = boundary.Count == 0 ? "measured" : "blocked" };
                    _mediaTimingOperations.Add(record);
                    StartupProbe.WriteAtomic(entryPath, record);
                    SaveMediaTiming();
                    if (boundary.Count > 0) throw new InvalidOperationException(string.Join("; ", boundary));
                    // No capability calls inside event timing. Sample attached playback separately after opens.
                    if (step.Id == "open-3")
                    {
                        _mediaTimingStage = media.id + "/presentation-capability";
                        SaveMediaTiming();
                        await session.SetAsync("pause", "no");
                        await MediaTimingProbe.SettleAsync(session, media.path, false, cancellation);
                        long logStart = MediaTimingLogOffset();
                        var graphicsBefore = await ReadTimingGraphicsAsync();
                        var samples = new List<TimingPresentation>();
                        for (int i = 0; i < 20; i++)
                        {
                            cancellation.ThrowIfCancellationRequested();
                            samples.Add(await ReadTimingPresentationAsync());
                            await Task.Delay(100, cancellation);
                        }
                        var graphicsAfter = await ReadTimingGraphicsAsync();
                        var first = samples[0]; var last = samples[^1];
                        var progress = first.Statistics is not null && last.Statistics is not null
                            ? PresentationStatistics.Compare(first.Statistics, last.Statistics) : null;
                        _mediaTimingCapabilities.Add(new { mediaId = media.id, samples, progress, logStart, graphicsBefore, graphicsAfter,
                            logEnd = MediaTimingLogOffset(), allAttached = samples.All(s => s.Attached),
                            firstMediaFrameIdentified = false, performanceAccepted = false });
                        SaveMediaTiming();
                        if (samples.Any(s => !s.Attached)) throw new InvalidOperationException("The swapchain was not attached during the capability interval.");
                        await session.SetAsync("pause", "yes");
                    }
                }
            }
            cancellation.ThrowIfCancellationRequested();
            _mediaTimingStatus = "collected";
        }
        catch (OperationCanceledException error) { _mediaTimingStatus = "cancelled"; _mediaTimingFailure ??= error.ToString(); }
        catch (Exception error) { _mediaTimingStatus = "failed"; _mediaTimingFailure ??= error.ToString(); }
        finally
        {
            try { StopFirstOpenTrace(); }
            catch (Exception error) { _mediaTimingFailure ??= error.ToString(); }
            watchStop.Cancel();
            await watch;
            try
            {
                await session.SetAsync("pause", "yes");
                _mediaTimingFinalPaused = (await session.ReadAsync()).Paused;
                if (!_mediaTimingFinalPaused) throw new InvalidOperationException("Final pause was not confirmed.");
            }
            catch (Exception error) { _mediaTimingFailure ??= error.ToString(); }
            foreach (var stream in streams) stream.Dispose();
            _mediaTimingSessionObserver?.Dispose();
            if (_mediaTimingSessionObserver is { Released: false } || _mediaTimingSessionObserver?.CleanupFailure is not null)
                _mediaTimingFailure ??= _mediaTimingSessionObserver?.CleanupFailure ?? "Session observer was not released.";
            if (_mediaTimingFailure is not null && _mediaTimingStatus == "collected") _mediaTimingStatus = "failed";
            _mediaTimingStage = "finished";
            SaveMediaTiming();
            if (!_closing) LabResults.Text = _mediaTimingStatus == "collected"
                ? "Media timing collected. Playback is paused. See the collector terminal."
                : "Media timing incomplete. Playback is paused. See the collector terminal.";
        }
    }
}

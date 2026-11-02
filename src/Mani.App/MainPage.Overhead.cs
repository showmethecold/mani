using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Mani.Playback;
using Mani_App.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Windowing;

namespace Mani_App;

public sealed partial class MainPage
{
    private OverheadRequest? _overheadRequest;
    private OverheadPollGate? _overheadGate;
    private string _overheadDirectory = "", _overheadTests = "", _overheadStatus = "starting";
    private string? _overheadFailure;
    private int _overheadPhase = -1;
    private long _overheadCreation;
    private object? _overheadBuild, _overheadMedia;
    private readonly List<object> _overheadMemory = [], _overheadPhases = [];
    private readonly List<OverheadInputSnapshot> _overheadInputs = [];

    private void InitializeOverheadIfRequested()
    {
        if (!DiagnosticsBuild.Enabled) return;
        string tests = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mani", "Tests");
        if (!File.Exists(Path.Combine(tests, "pending-overhead.json"))) return;
        if (File.Exists(Path.Combine(tests, "pending-lifecycle.json")) || File.Exists(Path.Combine(tests, "pending-long-playback.json")))
            throw new InvalidOperationException("Run one unattended test at a time.");
        _overheadRequest = OverheadRequest.Claim(tests);
        _overheadTests = tests;
        _overheadDirectory = Path.Combine(_logDirectory, "overhead-" + _overheadRequest.automationId);
        Directory.CreateDirectory(_overheadDirectory);
        _overheadGate = new();
        using (var process = Process.GetCurrentProcess())
            _overheadCreation = process.StartTime.ToUniversalTime().ToFileTimeUtc();
        static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }
        _overheadBuild = new {
            hostAssemblyId = typeof(MainPage).Module.ModuleVersionId,
            playbackAssemblyId = typeof(PlaybackSession).Module.ModuleVersionId,
            hostSha256 = Hash(typeof(MainPage).Assembly.Location), playbackSha256 = Hash(typeof(PlaybackSession).Assembly.Location),
            libmpvSha256 = Hash(Path.Combine(AppContext.BaseDirectory, "libmpv-2.dll"))
        };
        _overheadMemory.Add(OverheadRuntime.MemoryCheckpoint("before-engine"));
        CaptureOverheadInput("before-engine");
    }

    private OverheadInputSnapshot CaptureOverheadInput(string label)
    {
        var snapshot = OverheadInput.Capture(label, DispatcherQueue.HasThreadAccess, _overheadGate!);
        File.WriteAllText(Path.Combine(_overheadDirectory, "input-" + label + ".json"),
            JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
        _overheadInputs.Add(snapshot);
        SaveOverhead(); // Persist the raw failure boundary before condition validation.
        return snapshot;
    }

    private bool StartOverheadIfRequested()
    {
        if (!DiagnosticsBuild.Enabled) return false;
        if (_overheadRequest is null) return false;
        _overheadMemory.Add(OverheadRuntime.MemoryCheckpoint("engine-ready-no-media"));
        _ = RunProbeAsync(RunOverheadAsync);
        return true;
    }

    private void SaveOverhead()
    {
        if (_overheadRequest is null) return;
        var identity = new { schema_version = 1, _overheadRequest.automationId, _overheadRequest.diagnosticOnly, _overheadRequest.inputCondition, status = _overheadStatus,
            failure = _overheadFailure, phaseIndex = _overheadPhase,
            mode = _overheadPhase >= 0 ? OverheadPollGate.Plan[_overheadPhase] : "startup",
            hostProcessId = Environment.ProcessId, processCreationFileTime = _overheadCreation,
            sessionDirectoryName = Path.GetFileName(_logDirectory), runDirectoryName = Path.GetFileName(_overheadDirectory) };
        StartupProbe.WriteAtomic(Path.Combine(_overheadDirectory, "result.json"), new {
            schema_version = 3, _overheadRequest.diagnosticOnly, identity, build = _overheadBuild, media = _overheadMedia, plan = OverheadPollGate.Plan,
            measurementSeconds = OverheadPollGate.MeasurementSeconds, warmupSeconds = 5,
            memoryCheckpoints = _overheadMemory, phases = _overheadPhases,
            inputCondition = new { policy = OverheadInput.Policy, checkpoints = _overheadInputs },
            pollingRestored = _overheadGate?.Mode == "normal" && _overheadGate.Measuring == false,
            finalPaused = _snapshot?.Paused,
            limitation = "Controlled diagnostic modes in one process; fixed synthetic media. No automatic performance acceptance or full native/GPU memory attribution."
        });
        StartupProbe.WriteAtomic(Path.Combine(_overheadTests, _overheadRequest.automationId + ".overhead-status.json"), identity);
    }

    private string OverheadEnvironment()
    {
        if (Owner is null || _display is null) throw new InvalidOperationException("Display/window information is unavailable.");
        return JsonSerializer.Serialize(new {
            size = new { Owner.AppWindow.Size.Width, Owner.AppWindow.Size.Height },
            position = new { Owner.AppWindow.Position.X, Owner.AppWindow.Position.Y },
            presenter = Owner.AppWindow.Presenter.Kind.ToString(),
            windowState = Owner.AppWindow.Presenter is OverlappedPresenter presenter ? presenter.State.ToString() : "other",
            Owner.AppWindow.IsVisible,
            panel = new { VideoPanel.ActualWidth, VideoPanel.ActualHeight, VideoPanel.CompositionScaleX, VideoPanel.CompositionScaleY },
            display = DisplayDetails(), panelAttached = _bridge?.HasAttachedSwapChain == true, webEnabled = _web is not null, blurEnabled = _blurEnabled
        });
    }

    private async Task GuardOverheadAsync(int seconds, string environment, CancellationToken cancellation,
        ExternalResourceProbe? external = null)
    {
        for (int i = 0; i < seconds; i++)
        {
            await Task.Delay(1000, cancellation);
            if (File.Exists(Path.Combine(_overheadTests, _overheadRequest!.automationId + ".cancel")))
                throw new OperationCanceledException("The overhead launcher stopped.");
            if (_notice.Length > 0) throw new InvalidOperationException("Host error during overhead measurement: " + _notice);
            if (OverheadEnvironment() != environment)
                throw new InvalidOperationException("Window, display or overlay state changed during overhead measurement.");
            if (external is not null && i % 5 == 0) await external.CheckHeartbeatAsync(cancellation);
        }
    }

    private async Task SetOverheadModeAsync(string mode)
    {
        _timer.Stop();
        await _pollTask;
        _overheadGate!.SetMode(mode);
        if (!_closing) _timer.Start();
    }

    private static void ValidateOverheadPlayback(PlaybackSnapshot state, string path)
    {
        if (state.Path != path || state.Idle || state.Loading || state.Ended || state.Paused ||
            state.LoadFailure.Length > 0 || state.PausedForCache || state.VideoOutput != "gpu-next" ||
            state.HardwareDecoder != "d3d11va" || state.AudioOutput != "wasapi")
            throw new InvalidOperationException("The overhead media is not playing on the expected D3D11VA/WASAPI path: " + JsonSerializer.Serialize(state));
    }

    private async Task RunOverheadAsync(CancellationToken shutdown)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        deadline.CancelAfter(TimeSpan.FromMinutes(6));
        var cancellation = deadline.Token;
        var session = _session!;
        ExternalResourceProbe? external = null;
        var settings = OverheadSettings.Names;
        try
        {
            OverheadInput.Validate(_overheadInputs[0], _overheadInputs[0]);
            if (_web is not null || _blurEnabled || _hostCycleAttempts != 0 || _performanceProbeCount != 0)
                throw new InvalidOperationException("Overhead checks need a fresh process with overlays disabled.");
            LabPanel.Visibility = InfoPanel.Visibility = Visibility.Collapsed;
            string media = LifecycleProbe.Fixture(Path.Combine(AppContext.BaseDirectory, "Assets"), "display-media.json", "hdr-av");
            using (var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Assets", "display-media.json"))))
                _overheadMedia = new { path = media, sha256 = fixture.RootElement.GetProperty("hdr-av").GetProperty("sha256").GetString(), bytes = new FileInfo(media).Length };
            await LifecycleProbe.OpenReadyAsync(session, media, LifecycleHostReady, cancellation);
            _overheadMemory.Add(OverheadRuntime.MemoryCheckpoint("media-ready-paused"));
            await session.SetAsync("pause", "no");
            string environment = OverheadEnvironment();
            await GuardOverheadAsync(10, environment, cancellation);
            _overheadMemory.Add(OverheadRuntime.MemoryCheckpoint("playing-warmed"));
            _overheadStatus = "running";
            SaveOverhead();

            for (int index = 0; index < OverheadPollGate.Plan.Length; index++)
            {
                _overheadPhase = index;
                string mode = OverheadPollGate.Plan[index];
                await SetOverheadModeAsync(mode);
                var inputBefore = CaptureOverheadInput($"phase-{index:D2}-before");
                OverheadInput.Validate(inputBefore, _overheadInputs[0]);
                await GuardOverheadAsync(5, environment, cancellation);
                string directory = Path.Combine(_overheadDirectory, $"phase-{index:D2}-{mode}");
                Directory.CreateDirectory(directory);
                external = await ExternalResourceProbe.PrepareAsync(directory, "host-overhead", cancellation,
                    requestedDurationSeconds: OverheadPollGate.MeasurementSeconds, automationId: _overheadRequest!.automationId);
                _timer.Stop();
                await _pollTask;
                var before = await session.ReadAsync();
                ValidateOverheadPlayback(before, media);
                var settingsBefore = await session.GetPropertiesAsync(settings);
                var runtimeBefore = OverheadRuntime.Capture();
                _overheadGate!.Begin();
                await external.StartAsync();
                _timer.Start();
                await GuardOverheadAsync(OverheadPollGate.MeasurementSeconds, environment, cancellation, external);
                _timer.Stop();
                await _pollTask;
                await external.StopAsync();
                _overheadGate.End();
                var runtimeAfter = OverheadRuntime.Capture();
                var after = await session.ReadAsync();
                var settingsAfter = await session.GetPropertiesAsync(settings);
                string environmentAfter = OverheadEnvironment();
                string requestId = external.RequestId;
                var inputAfter = CaptureOverheadInput($"phase-{index:D2}-after");
                // Save both boundaries before validation, including failed measurements.
                await File.WriteAllTextAsync(Path.Combine(directory, "boundary.json"), JsonSerializer.Serialize(new {
                    index, mode, before, after, settingsBefore, settingsAfter, runtimeBefore, runtimeAfter, inputBefore, inputAfter,
                    environment = JsonSerializer.Deserialize<JsonElement>(environment),
                    environmentAfter = JsonSerializer.Deserialize<JsonElement>(environmentAfter),
                    polls = _overheadGate.Polls, uiUpdates = _overheadGate.UiUpdates, skippedTicks = _overheadGate.SkippedTicks,
                    externalRequestId = requestId
                }, new JsonSerializerOptions { WriteIndented = true }));
                // Let the collector consume the completed interval before rejecting its comparison conditions.
                // A failed validation must not overwrite the already-written done boundary with cancelled.
                var resources = await external.CompleteAsync(cancellation);
                external = null;
                OverheadInput.Validate(inputAfter, _overheadInputs[0]);
                ValidateOverheadPlayback(after, media);
                var changes = OverheadSettings.Changes(settingsBefore, settingsAfter).ToList();
                if (after.FileLoadedCount != before.FileLoadedCount) changes.Add("FileLoadedCount");
                if (after.ColorOutput != before.ColorOutput) changes.Add("ColorOutput");
                changes.AddRange(OverheadWindow.Problems(before, after, runtimeBefore, runtimeAfter));
                if (environmentAfter != environment) changes.Add("window/display environment");
                if (changes.Count > 0)
                    throw new InvalidOperationException("Measurement boundary changed: " + string.Join(", ", changes) + ". See boundary.json.");
                var phase = new { index, mode, status = "completed", before, after, settingsBefore, settingsAfter,
                    runtimeBefore, runtimeAfter, inputBeforeLabel = inputBefore.label, inputAfterLabel = inputAfter.label,
                    environment = JsonSerializer.Deserialize<JsonElement>(environment),
                    polls = _overheadGate.Polls, uiUpdates = _overheadGate.UiUpdates, skippedTicks = _overheadGate.SkippedTicks,
                    externalRequestId = requestId, resourceSampler = ExternalResourceProbe.Profile,
                    externalResources = resources };
                await File.WriteAllTextAsync(Path.Combine(directory, "phase.json"),
                    JsonSerializer.Serialize(phase, new JsonSerializerOptions { WriteIndented = true }));
                _overheadPhases.Add(phase);
                external = null;
                if (!_closing) _timer.Start();
                SaveOverhead();
            }
            _overheadStatus = "completed";
        }
        catch (OperationCanceledException error) { _overheadStatus = "cancelled"; _overheadFailure = error.Message; }
        catch (Exception error) { _overheadStatus = "failed"; _overheadFailure = error.ToString(); }
        finally
        {
            if (external is not null)
                try { await external.CancelAsync(); } catch (Exception error) { _overheadFailure ??= error.ToString(); }
            _timer.Stop();
            await _pollTask;
            _overheadGate!.Reset();
            try
            {
                await session.SetAsync("pause", "yes");
                if (!_closing)
                {
                    await PollAsync();
                    _timer.Start();
                }
                _overheadMemory.Add(OverheadRuntime.MemoryCheckpoint("finished-paused"));
            }
            catch (Exception error) { _overheadFailure ??= error.ToString(); }
            if (_overheadFailure is not null && _overheadStatus == "completed") _overheadStatus = "failed";
            SaveOverhead();
            if (!_closing) LabResults.Text = _overheadStatus == "completed"
                ? "Overhead measurements completed. Playback is paused. See the collector terminal."
                : "Overhead measurements incomplete. See the collector terminal.";
        }
    }
}

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Mani.Playback;
using Mani_App.Diagnostics;

namespace Mani_App;

public sealed partial class MainPage
{
    private LifecycleRequest? _lifecycleRequest;
    private string _lifecycleDirectory = "", _lifecycleTests = "", _lifecycleStatus = "starting";
    private string? _lifecycleFailure;
    private bool _lifecycleCloseRequested;
    private long _lifecycleCreation;
    private object? _lifecycleBuild;
    private readonly List<object> _lifecycleStages = [];
    private Task[] _lifecycleLoads = [];

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(nint hwnd, uint message, nuint wParam, nint lParam);

    private bool StartLifecycleIfRequested()
    {
        if (!DiagnosticsBuild.Enabled) return false;
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mani", "Tests");
        if (!File.Exists(Path.Combine(directory, "pending-lifecycle.json"))) return false;
        if (File.Exists(Path.Combine(directory, "pending-long-playback.json")))
            throw new InvalidOperationException("Conflicting unattended requests. Run one test at a time.");
        _lifecycleRequest = LifecycleRequest.Claim(directory);
        _lifecycleTests = directory;
        _lifecycleDirectory = Path.Combine(_logDirectory, "lifecycle-" + _lifecycleRequest.automationId);
        Directory.CreateDirectory(_lifecycleDirectory);
        using (var process = Process.GetCurrentProcess())
            _lifecycleCreation = process.StartTime.ToUniversalTime().ToFileTimeUtc();
        static string Hash(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant();
        _lifecycleBuild = new {
            hostAssemblyId = typeof(MainPage).Module.ModuleVersionId,
            playbackAssemblyId = typeof(PlaybackSession).Module.ModuleVersionId,
            hostSha256 = Hash(typeof(MainPage).Assembly.Location),
            playbackSha256 = Hash(typeof(PlaybackSession).Assembly.Location),
            libmpvSha256 = Hash(Path.Combine(AppContext.BaseDirectory, "libmpv-2.dll"))
        };
        RecordLifecycleStage("starting");
        _ = RunProbeAsync(RunLifecycleAsync);
        return true;
    }

    private object LifecycleHost() => new {
        panelAttached = _bridge?.HasAttachedSwapChain == true, _hostStress,
        panelWidth = VideoPanel.ActualWidth, panelHeight = VideoPanel.ActualHeight,
        scaleX = VideoPanel.CompositionScaleX, scaleY = VideoPanel.CompositionScaleY,
        uiPath = _snapshot?.Path, uiLoadFailure = _snapshot?.LoadFailure, uiError = _snapshot?.Error,
        uiIdle = _snapshot?.Idle, uiLoading = _snapshot?.Loading, uiFileLoadedCount = _snapshot?.FileLoadedCount,
        uiPaused = _snapshot?.Paused, statusText = Status.Text
    };

    private bool LifecycleHostReady(PlaybackSnapshot state)
    {
        if (state.LoadFailure.Length > 0)
            return LifecycleProbe.IsFailureShown(state, _snapshot, Status.Text);
        return _bridge?.HasAttachedSwapChain == true && VideoPanel.ActualWidth > 0 && VideoPanel.ActualHeight > 0 &&
            _snapshot is { Loading: false, Idle: false } shown && shown.Path == state.Path &&
            shown.FileLoadedCount == state.FileLoadedCount && shown.LoadFailure.Length == 0 && shown.Paused == state.Paused;
    }

    private void RecordLifecycleStage(string stage, object? details = null)
    {
        if (_lifecycleRequest is null) return;
        _lifecycleStages.Add(new { stage, atUtc = DateTimeOffset.UtcNow, details });
        Trace("lifecycle-" + stage, details ?? new { });
        try
        {
            var json = JsonSerializer.Serialize(new {
                schema_version = 1, _lifecycleRequest.automationId, _lifecycleRequest.testCase,
                status = _lifecycleStatus, failure = _lifecycleFailure,
                hostProcessId = Environment.ProcessId, processCreationFileTime = _lifecycleCreation,
                sessionDirectoryName = Path.GetFileName(_logDirectory), build = _lifecycleBuild,
                stages = _lifecycleStages, closeRequested = _lifecycleCloseRequested,
                limitation = "Normal WM_CLOSE and current display only. Process exit is checked by the external launcher."
            }, new JsonSerializerOptions { WriteIndented = true });
            foreach (string path in new[] { Path.Combine(_lifecycleDirectory, "result.json"),
                Path.Combine(_lifecycleTests, _lifecycleRequest.automationId + ".lifecycle-status.json") })
            {
                File.WriteAllText(path + ".tmp", json);
                File.Move(path + ".tmp", path, overwrite: true);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _lifecycleFailure ??= error.ToString();
            Trace("lifecycle-evidence-error", new { error = error.ToString() });
        }
    }

    private async Task RunLifecycleAsync(CancellationToken shutdown)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        var cancellation = deadline.Token;
        var request = _lifecycleRequest!;
        try
        {
            // The external observer must own the exact process handle before any automatic close.
            var clock = Stopwatch.StartNew();
            while (!File.Exists(Path.Combine(_lifecycleTests, request.automationId + ".observing")))
            {
                cancellation.ThrowIfCancellationRequested();
                if (File.Exists(Path.Combine(_lifecycleTests, request.automationId + ".cancel")))
                    throw new OperationCanceledException("Lifecycle launcher stopped.");
                if (clock.Elapsed.TotalSeconds > 30) throw new TimeoutException("The external process observer did not acknowledge startup.");
                await Task.Delay(100, cancellation);
            }
            _lifecycleStatus = "running";
            RecordLifecycleStage("observer-attached");
            var assets = Path.Combine(AppContext.BaseDirectory, "Assets");
            if (request.testCase == "recovery-close")
            {
                await LifecycleProbe.RunAsync(_session!, assets, _lifecycleDirectory, LifecycleHostReady, LifecycleHost, cancellation);
                RecordLifecycleStage("recovery-completed", LifecycleHost());
                RequestLifecycleClose();
                await Task.Delay(Timeout.Infinite, cancellation);
            }
            else
            {
                string hdr = LifecycleProbe.Fixture(assets, "display-media.json", "hdr-av");
                await LifecycleProbe.OpenReadyAsync(_session!, hdr, LifecycleHostReady, cancellation);
                if (request.testCase == "load-burst-close")
                {
                    string sdr = LifecycleProbe.Fixture(assets, "baseline-media.json", "1080p");
                    _lifecycleLoads = Enumerable.Range(0, 20).Select(i => _session!.OpenAsync(i % 2 == 0 ? sdr : hdr, paused: true)).ToArray();
                    RecordLifecycleStage("loads-submitted", new { count = _lifecycleLoads.Length,
                        pending = _lifecycleLoads.Count(task => !task.IsCompleted), host = LifecycleHost() });
                    RequestLifecycleClose();
                    await Task.WhenAll(_lifecycleLoads);
                    RecordLifecycleStage("load-commands-settled");
                    await Task.Delay(Timeout.Infinite, cancellation);
                }
                else
                {
                    await RunHostCyclesAsync(Path.Combine(_lifecycleDirectory, "host-cycles"), 50, cancellation, async () =>
                    {
                        RecordLifecycleStage("probe-surface-attached", LifecycleHost());
                        RequestLifecycleClose();
                        // Keep the real temporary panel/session alive until ShutdownProbesAsync cancels it.
                        await Task.Delay(Timeout.Infinite, cancellation);
                    });
                    throw new InvalidOperationException("The host-cycle task completed without the requested close.");
                }
            }
        }
        catch (OperationCanceledException) when (_closing && _lifecycleCloseRequested && shutdown.IsCancellationRequested)
        {
            RecordLifecycleStage("probe-cancelled", new { requestedByWindowClose = true });
        }
        catch (Exception error)
        {
            _lifecycleStatus = "failed";
            _lifecycleFailure = error.ToString();
            RecordLifecycleStage("test-failed", new { host = LifecycleHost() });
            if (!_closing) await _session!.SetAsync("pause", "yes");
            throw;
        }
    }

    private void RequestLifecycleClose()
    {
        if (File.Exists(Path.Combine(_lifecycleTests, _lifecycleRequest!.automationId + ".cancel")))
            throw new OperationCanceledException("Lifecycle launcher stopped.");
        _lifecycleCloseRequested = true;
        _lifecycleStatus = "closing";
        RecordLifecycleStage("close-requested", LifecycleHost());
        if (Owner is null || !PostMessageW(WinRT.Interop.WindowNative.GetWindowHandle(Owner), 0x0010, 0, 0))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Could not request normal window close.");
    }

    internal void RecordLifecycleCloseReceived() => RecordLifecycleStage("close-received", new {
        probeRunning = !_m1Task.IsCompleted, _hostStress,
        pendingLoads = _lifecycleLoads.Count(task => !task.IsCompleted), host = LifecycleHost()
    });

    internal void FinishLifecycleShutdown(Exception? error)
    {
        if (_lifecycleRequest is null) return;
        _lifecycleFailure ??= error?.ToString();
        if (!_lifecycleCloseRequested) _lifecycleFailure ??= "The window closed before the automatic close checkpoint.";
        _lifecycleStatus = _lifecycleFailure is null ? "shutdown-completed" : "failed";
        RecordLifecycleStage("shutdown-finished");
    }
}

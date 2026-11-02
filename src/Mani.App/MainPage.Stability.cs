using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Xaml;

namespace Mani_App;

public sealed partial class MainPage
{
    private async void StabilityAndPerformance_Click(object sender, RoutedEventArgs e) =>
        await RunProbeAsync(RunStabilityAndPerformanceAsync);

    private async Task RunStabilityAndPerformanceAsync(CancellationToken cancellation)
    {
        var session = _session!;
        await _pollTask;
        _pendingSeek = null;
        _pendingVolume = null;
        var before = await session.ReadAsync();
        if (before.Idle || before.Loading || before.VideoFormat.Length == 0 ||
            before.Duration < 15 || !File.Exists(before.Path))
            throw new InvalidOperationException("Open a local video at least 15 seconds long.");
        if (_web is not null || _blurEnabled)
        {
            LabResults.Text = "Turn off the WebView2 overlay and GPU blur before running this test.";
            return;
        }
        var run = NewProbeDirectory("stability-performance");
        var idleSamples = new List<object>();
        var rounds = new List<string>();
        var clock = Stopwatch.StartNew();
        string status = "running";
        string? failure = null, restoreFailure = null, performanceReport = null;
        var displayAtStart = DisplayDetails();
        using var process = Process.GetCurrentProcess();
        async Task SaveAsync()
        {
            var report = new { schema_version = 1, status, failure, restoreFailure,
                atUtc = DateTimeOffset.UtcNow, mediaPath = before.Path, hostProcessId = Environment.ProcessId,
                dllSha256 = ProbeOptions("unused").DllSha256, displayAtStart,
                rounds, idleSamples, performanceReport, forcedGc = false,
                limitations = "Two 50-cycle rounds with 30-second paused idle windows. No leak acceptance threshold or process-exit memory measurement. GPU utilization and first DWM presentation remain unmeasured." };
            await File.WriteAllTextAsync(Path.Combine(run, "result.json"),
                JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }
        async Task ObserveIdleAsync(string phase)
        {
            var idleClock = Stopwatch.StartNew();
            for (int i = 0; i <= 6; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                process.Refresh();
                idleSamples.Add(new { phase, seconds = clock.Elapsed.TotalSeconds,
                    idleSeconds = idleClock.Elapsed.TotalSeconds,
                    process.HandleCount, process.PrivateMemorySize64, process.WorkingSet64,
                    threadCount = process.Threads.Count, managedBytes = GC.GetTotalMemory(false),
                    gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2) });
                LabResults.Text = $"Resource check: {phase}, {i * 5} / 30 seconds. Keep the window size and display unchanged.";
                await SaveAsync();
                if (i < 6) await Task.Delay(TimeSpan.FromSeconds(5), cancellation);
            }
        }
        try
        {
            await session.SetAsync("pause", "yes");
            await ObserveIdleAsync("baseline");
            for (int round = 1; round <= 2; round++)
            {
                var directory = Path.Combine(run, $"round-{round}");
                Directory.CreateDirectory(directory);
                rounds.Add($"round-{round}/result.json");
                await SaveAsync();
                await RunHostCyclesAsync(directory, 50, cancellation);
                await ObserveIdleAsync($"after-round-{round}");
            }
            performanceReport = await RunPerformanceProbeAsync(cancellation);
            status = "completed";
        }
        catch (OperationCanceledException) { status = "cancelled"; throw; }
        catch (Exception error) { status = "failed"; failure = error.ToString(); throw; }
        finally
        {
            try
            {
                if (!_closing) await session.SetAsync("pause", before.Paused ? "yes" : "no");
            }
            catch (Exception error) { status = "failed"; restoreFailure = error.ToString(); }
            await SaveAsync();
            if (!_closing && status == "completed")
                LabResults.Text = "Host tests completed. Close Mani, then run python scripts/run_m1_comparison.py from the project folder. Reports: " + run;
        }
        if (restoreFailure is not null) throw new InvalidOperationException("Playback restoration failed. See the stability report.");
    }
}

using System.Diagnostics;
using System.Text.Json;
using Mani.Playback;
using Mani_App.Interop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Mani_App;

public sealed partial class MainPage
{
    private async void HostStress_Click(object sender, RoutedEventArgs e) =>
        await RunProbeAsync(async cancellation =>
        {
            var run = NewProbeDirectory("host-cycles");
            await RunHostCyclesAsync(run, 50, cancellation);
            if (!_closing) LabResults.Text = "50 host cycles completed. Main playback restored. Resource samples saved for review.";
        });

    private string NewProbeDirectory(string prefix)
    {
        var path = Path.Combine(_logDirectory, prefix + "-" + DateTime.UtcNow.ToString("HHmmss-fff") + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(path);
        return path;
    }

    private async Task RunHostCyclesAsync(string run, int count, CancellationToken cancellation, Func<Task>? surfaceAttached = null)
    {
        Directory.CreateDirectory(run);
        var main = _session!;
        var state = await main.ReadAsync();
        if (state.Idle || !File.Exists(state.Path) || state.VideoFormat.Length == 0)
            throw new InvalidOperationException("Open a local video before running host cycles.");
        var originalPanel = VideoPanel;
        var originalBridge = _bridge!;
        _hostStress = true;
        var samples = new List<object>();
        var clock = Stopwatch.StartNew();
        int completed = 0;
        string status = "interrupted";
        string? failure = null, restoreFailure = null;
        using var process = Process.GetCurrentProcess();
        object Sample(int cycle)
        {
            process.Refresh();
            return new { cycle, seconds = clock.Elapsed.TotalSeconds, process.HandleCount, process.PrivateMemorySize64, process.WorkingSet64,
                threadCount = process.Threads.Count, managedBytes = GC.GetTotalMemory(false),
                gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2) };
        }
        try
        {
            await _pollTask;
            _pendingSeek = null;
            await main.SetAsync("pause", "yes");
            originalBridge.Attach(0);
            VideoArea.Children.Remove(originalPanel);
            samples.Add(Sample(0));
            for (int i = 0; i < count; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                _hostCycleAttempts++;
                // A new XAML panel and COM bridge on every cycle, not just a new mpv instance.
                var panel = new SwapChainPanel { IsHitTestVisible = false };
                VideoArea.Children.Insert(0, panel);
                VideoPanel = panel;
                panel.UpdateLayout();
                try
                {
                    using var bridge = new SwapChainPanelBridge(panel);
                    _bridge = bridge;
                    await using var probe = await PlaybackSession.CreateAsync(ProbeOptions("host-cycle-" + Guid.NewGuid().ToString("N")));
                    try
                    {
                        await probe.OpenAsync(state.Path, paused: true);
                        var timer = Stopwatch.StartNew();
                        while (true)
                        {
                            cancellation.ThrowIfCancellationRequested();
                            panel.UpdateLayout();
                            var observed = await probe.ReadAsync();
                            using var chain = await probe.AcquireSwapChainAsync();
                            if (chain is not null && !observed.Loading && observed.Path == state.Path &&
                                observed.VideoFormat.Length > 0 && panel.ActualWidth > 0 && panel.ActualHeight > 0)
                            {
                                await probe.ResizeAsync((int)Math.Round(panel.ActualWidth * panel.CompositionScaleX),
                                    (int)Math.Round(panel.ActualHeight * panel.CompositionScaleY));
                                bridge.Attach(chain.Pointer);
                                if (surfaceAttached is not null) await surfaceAttached();
                                await Task.Delay(100, cancellation); // Yield while an actual XAML composition surface is attached.
                                break;
                            }
                            if (timer.Elapsed.TotalSeconds > 15) throw new TimeoutException("Host cycle did not receive a video frame and panel layout.");
                            await Task.Delay(25, cancellation);
                        }
                    }
                    finally { bridge.Attach(0); } // Detach before disposing the native session.
                }
                finally
                {
                    VideoArea.Children.Remove(panel);
                    _bridge = originalBridge;
                    VideoPanel = originalPanel;
                }
                completed++;
                samples.Add(Sample(completed));
                if (!_closing) LabResults.Text = $"Video host cycles: {completed} / {count}";
            }
            status = "completed";
        }
        catch (OperationCanceledException) { status = "cancelled"; throw; }
        catch (Exception error) { status = "failed"; failure = error.ToString(); throw; }
        finally
        {
            // Keep polling stopped until restoration completes; errors still get an evidence file.
            try
            {
                VideoPanel = originalPanel;
                _bridge = originalBridge;
                if (!VideoArea.Children.Contains(originalPanel)) VideoArea.Children.Insert(0, originalPanel);
                originalPanel.UpdateLayout();
                if (!_closing)
                {
                    using var chain = await main.AcquireSwapChainAsync();
                    originalBridge.Attach(chain?.Pointer ?? 0);
                    await main.SetAsync("pause", state.Paused ? "yes" : "no");
                }
            }
            catch (Exception error)
            {
                status = "failed"; restoreFailure = error.ToString();
                Trace("host-cycle-restore-error", new { restoreFailure });
            }
            finally
            {
                _hostStress = false;
                var report = new { status, completed, requested = count, failure, restoreFailure, samples,
                    scaleX = originalPanel.CompositionScaleX, scaleY = originalPanel.CompositionScaleY,
                    panelRecreatedEachCycle = true,
                    limitation = "Real XAML panel/bridge/session recreation at current DPI. No visual assertion, system HDR/lock/monitor transitions or long-term leak certification." };
                await File.WriteAllTextAsync(Path.Combine(run, "result.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        if (restoreFailure is not null) throw new InvalidOperationException("Main playback restoration failed. See host-cycle report.");
    }
}

using System.Diagnostics;
using System.Text.Json;
using Mani_App.Diagnostics;

namespace Mani_App;

public sealed partial class MainPage
{
    private string? _unattendedRunId;
    private string? _longPlaybackReportPath;

    private void StartUnattendedPlaybackIfRequested()
    {
        if (!DiagnosticsBuild.Enabled) return;
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mani", "Tests");
        if (!File.Exists(Path.Combine(directory, "pending-long-playback.json"))) return;
        var request = UnattendedPlaybackRequest.Claim(directory);
        // Do not await the 30-minute task from StartAsync: shutdown must be able to cancel it.
        _ = RunProbeAsync(cancellation => RunUnattendedPlaybackAsync(directory, request, cancellation));
    }

    private async Task RunUnattendedPlaybackAsync(string directory, UnattendedPlaybackRequest request, CancellationToken cancellation)
    {
        _unattendedRunId = request.automationId;
        string status = "starting";
        string? failure = null;
        var statusPath = Path.Combine(directory, request.automationId + ".status.json");
        async Task SaveStatusAsync()
        {
            await File.WriteAllTextAsync(statusPath + ".tmp", JsonSerializer.Serialize(new {
                schema_version = 1, request.automationId, status, failure,
                atUtc = DateTimeOffset.UtcNow, hostProcessId = Environment.ProcessId,
                reportPath = _longPlaybackReportPath, finishPolicy = "pause"
            }));
            File.Move(statusPath + ".tmp", statusPath, overwrite: true);
        }
        void CheckCancellation()
        {
            cancellation.ThrowIfCancellationRequested();
            if (File.Exists(Path.Combine(directory, request.automationId + ".cancel")))
                throw new OperationCanceledException("The unattended launcher was cancelled.");
        }
        try
        {
            await SaveStatusAsync();
            CheckCancellation();
            var restartsBeforeOpen = await _session!.GetPlaybackRestartCountAsync();
            await _session.OpenAsync(request.mediaPath, paused: true);
            var clock = Stopwatch.StartNew();
            while (true)
            {
                CheckCancellation();
                var state = await _session.ReadAsync();
                var restarts = await _session.GetPlaybackRestartCountAsync();
                if (UnattendedPlaybackReadiness.IsReady(state, request.mediaPath,
                    _bridge?.HasAttachedSwapChain == true && VideoPanel.ActualWidth > 0 && VideoPanel.ActualHeight > 0,
                    restarts > restartsBeforeOpen))
                {
                    Trace("unattended-ready", new { request.automationId, state.VideoOutput, state.HardwareDecoder,
                        state.Error, state.LoadFailure, restarts, panelAttached = true });
                    break;
                }
                if (clock.Elapsed.TotalSeconds > 30)
                    throw new TimeoutException("The movie or video panel did not become ready within 30 seconds. Last diagnostic: " + state.Error);
                await Task.Delay(100, cancellation);
            }
            status = "running";
            await SaveStatusAsync();
            CheckCancellation();
            await RunLongPlaybackAsync(cancellation);
            if (_longPlaybackReportPath is null) throw new InvalidOperationException(LabResults.Text);
            using var report = JsonDocument.Parse(await File.ReadAllTextAsync(_longPlaybackReportPath));
            if (report.RootElement.GetProperty("status").GetString() != "completed")
                throw new IOException("The long-playback report did not complete. See the archived evidence.");
            status = "completed";
        }
        catch (OperationCanceledException error) { status = "cancelled"; failure = error.Message; throw; }
        catch (Exception error) { status = "failed"; failure = error.ToString(); throw; }
        finally
        {
            try { if (!_closing) await _session!.SetAsync("pause", "yes"); }
            catch (Exception error) { status = "failed"; failure ??= error.ToString(); }
            await SaveStatusAsync();
            if (!_closing && status == "completed")
                LabResults.Text = "30-minute test completed. Playback is paused. See the report in the collector terminal.";
            _unattendedRunId = null;
        }
    }
}

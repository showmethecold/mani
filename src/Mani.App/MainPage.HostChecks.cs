using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Mani.Playback;
using Microsoft.UI.Xaml;

namespace Mani_App;

public sealed partial class MainPage
{
    private sealed record HostCheck(string Id, string Status, object Details);
    private async void AutomatedHostChecks_Click(object sender, RoutedEventArgs e) => await RunProbeAsync(RunAutomatedHostChecksAsync);

    private async void WebHostChecks_Click(object sender, RoutedEventArgs e) =>
        await RunProbeAsync(cancellation => RunAutomatedHostChecksAsync(cancellation, webOnly: true));

    private Task RunAutomatedHostChecksAsync(CancellationToken cancellation) => RunAutomatedHostChecksAsync(cancellation, webOnly: false);
    private async Task RunAutomatedHostChecksAsync(CancellationToken cancellation, bool webOnly)
    {
        var session = _session!;
        await _pollTask;
        _pendingSeek = null;
        _pendingVolume = null;
        var before = await session.ReadAsync();
        if (before.Idle || before.Loading || before.VideoFormat.Length == 0 || before.Duration < 2 || !File.Exists(before.Path))
            throw new InvalidOperationException("Open a local video at least two seconds long and wait for its first frame.");
        var directory = NewProbeDirectory(webOnly ? "web-host-checks" : "automated-host-checks");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromMinutes(4));
        var token = deadline.Token;
        var results = new List<HostCheck>();
        var originalWebOpen = _web is not null;
        var loop = await session.GetPropertiesAsync("ab-loop-a", "ab-loop-b");
        string status = "running";
        string? cleanupFailure = null;
        var initialDisplay = DisplayDetails();
        async Task SaveAsync()
        {
            var report = new { schema_version = 1, suite = webOnly ? "web-only" : "full", scopeVersion = 2, deferredChecks = new[] { "ocr-en-US:M6", "ocr-zh-Hans-CN:M6" }, status, atUtc = DateTimeOffset.UtcNow, results, cleanupFailure,
                source = before.Path, dllSha256 = ProbeOptions("unused").DllSha256, webView2Version = _webVersion,
                display = initialDisplay,
                limitations = "In-process checks on a real WinUI window. DOM focus is not physical keyboard/IME input. No pixel visibility, HDR luminance, mixed-DPI, lock/disconnect or long-term leak certification." };
            await File.WriteAllTextAsync(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }
        async Task CheckAsync(string id, Func<Task<HostCheck>> action)
        {
            token.ThrowIfCancellationRequested();
            LabResults.Text = "Running: " + id;
            try { results.Add(await action()); }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) { results.Add(new(id, "failed", new { error = error.ToString() })); }
            await SaveAsync();
        }
        try
        {
            await session.SetAsync("pause", "yes");
            await session.SetAsync("ab-loop-a", "no");
            await session.SetAsync("ab-loop-b", "no");
            await session.CommandAsync("seek", "0", "absolute+exact");
            CloseWebProbe();
            await _webCommandTask;
            WebProbeContainer.IsHitTestVisible = false;
            await CheckAsync("webview-message-roundtrip", async () =>
            {
                await OpenWebProbeAsync(token);
                _webCheckAcceptsCommands = true;
                try
                {
                    var core = _web!.CoreWebView2;
                    await ClickWebProbeAsync("pause", 1, token);
                    await WaitForWebAsync("window.maniProbe.response?.requestId === 1 && window.maniProbe.state?.paused === true", token);
                    if (!(await session.ReadAsync()).Paused) throw new InvalidOperationException("Web Pause did not pause the engine.");
                    await ClickWebProbeAsync("play", 2, token);
                    await WaitForWebAsync("window.maniProbe.response?.requestId === 2 && window.maniProbe.state?.paused === false && window.maniProbe.state?.position > 0.05", token);
                    if ((await session.ReadAsync()).Paused) throw new InvalidOperationException("Web Play did not resume the engine.");
                    await core.ExecuteScriptAsync("document.querySelector('input').focus(); document.querySelector('input').value = 'focus test 123';").AsTask(token);
                    await WaitForWebAsync("document.activeElement === document.querySelector('input')", token);
                    return new HostCheck("webview-message-roundtrip", "pass", new { requests = 2, domFocus = true, _webVersion });
                }
                finally
                {
                    _webCheckAcceptsCommands = false;
                    CloseWebProbe();
                    await _webCommandTask;
                    await session.SetAsync("pause", "yes");
                }
            });
            await CheckAsync("webview-recreate", async () =>
            {
                await OpenWebProbeAsync(token);
                PublishWebState(await session.ReadAsync());
                await WaitForWebAsync("window.maniProbe.state?.paused === true", token);
                CloseWebProbe();
                if (WebProbeContainer.Children.Count != 0 || _web is not null) throw new InvalidOperationException("Web surface remained attached.");
                return new HostCheck("webview-recreate", "pass", new { detached = true });
            });
            if (!webOnly)
            {
            await CheckAsync("independent-frame", async () =>
            {
                await session.SetAsync("pause", "yes");
                var start = await session.ReadAsync();
                string png = await FrameCapture.CaptureAsync(ProbeOptions("host-check-capture"), start.Path,
                    Math.Max(0, start.Duration * .5), Path.Combine(directory, "independent-frame.png"), token);
                var end = await session.ReadAsync();
                if (!end.Paused || Math.Abs(start.Position - end.Position) > .01 || start.Path != end.Path)
                    throw new InvalidOperationException("Independent capture changed main playback.");
                return new HostCheck("independent-frame", "pass", new { frame = png, positionBefore = start.Position, positionAfter = end.Position });
            });
            await CheckAsync("frame-cancellation", async () =>
            {
                using var cancelled = new CancellationTokenSource();
                cancelled.Cancel();
                var destination = Path.Combine(directory, "cancelled-frame.png");
                try
                {
                    await FrameCapture.CaptureAsync(ProbeOptions("host-check-cancel"), before.Path, 0, destination, cancelled.Token);
                    throw new InvalidOperationException("A cancelled capture completed.");
                }
                catch (OperationCanceledException)
                {
                    if (File.Exists(destination)) throw new InvalidOperationException("Cancelled capture published a file.");
                }
                return new HostCheck("frame-cancellation", "pass", new { partialFilePublished = false, scope = "Already-cancelled request; mid-capture native test is tracked separately." });
            });
            await CheckAsync("xaml-host-50-cycles", async () =>
            {
                var cycles = Path.Combine(directory, "host-cycles");
                Directory.CreateDirectory(cycles);
                await RunHostCyclesAsync(cycles, 50, token);
                return new HostCheck("xaml-host-50-cycles", "pass", new { completed = 50, report = Path.Combine(cycles, "result.json"), leakAcceptance = "pending resource trend and visual review" });
            });
            }
            status = results.Any(r => r.Status == "failed") ? "failed" : results.Any(r => r.Status == "blocked") ? "partial" : "pass";
        }
        catch (OperationCanceledException)
        {
            status = cancellation.IsCancellationRequested ? "cancelled" : "timed-out";
        }
        catch (Exception error)
        {
            status = "failed";
            results.Add(new HostCheck("setup-or-report", "failed", new { error = error.ToString() }));
        }
        finally
        {
            _webCheckAcceptsCommands = false;
            WebProbeContainer.IsHitTestVisible = true;
            try
            {
                CloseWebProbe();
                await _webCommandTask;
                if (!_closing)
                {
                    await session.SetAsync("pause", "yes");
                    await session.CommandAsync("seek", before.Position.ToString(CultureInfo.InvariantCulture), "absolute+exact");
                    foreach (var value in loop) await session.SetAsync(value.Key, value.Value);
                    await session.SetAsync("pause", before.Paused ? "yes" : "no");
                    if (originalWebOpen)
                    {
                        using var restore = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                        await OpenWebProbeAsync(restore.Token);
                    }
                }
            }
            catch (Exception error) { cleanupFailure = error.ToString(); status = "failed"; }
            await SaveAsync();
            if (!_closing) LabResults.Text = $"Host checks: {status}. Passed: {results.Count(r => r.Status == "pass")}; failed: {results.Count(r => r.Status == "failed")}; blocked: {results.Count(r => r.Status == "blocked")}. Report: {directory}";
        }
    }

    private async Task ClickWebProbeAsync(string button, int expectedRequest, CancellationToken cancellation)
    {
        var core = _web!.CoreWebView2;
        string value = await core.ExecuteScriptAsync("(() => { document.getElementById(" + JsonSerializer.Serialize(button) +
            ").click(); return {sent:window.maniProbe.sent,lastSendError:window.maniProbe.lastSendError}; })()").AsTask(cancellation);
        Trace("web-click-dispatch", new { button, expectedRequest, state = value });
        using var data = JsonDocument.Parse(value);
        if (data.RootElement.ValueKind != JsonValueKind.Object ||
            !data.RootElement.TryGetProperty("sent", out var sent) || !sent.TryGetInt32(out var request) || request != expectedRequest)
            throw new InvalidOperationException("Web button did not post its command: " + value);
    }

    private async Task WaitForWebAsync(string expression, CancellationToken cancellation)
    {
        var timer = Stopwatch.StartNew();
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!_webReady || _web?.CoreWebView2 is not { } core) throw new InvalidOperationException("Web surface is unavailable.");
            if (await core.ExecuteScriptAsync("Boolean(" + expression + ")").AsTask(cancellation) == "true") return;
            if (timer.Elapsed.TotalSeconds > 8)
            {
                string state = await core.ExecuteScriptAsync("JSON.stringify(window.maniProbe)").AsTask(cancellation);
                Trace("web-wait-timeout", new { expression, state });
                throw new TimeoutException("Web message or DOM state was not observed: " + expression + "; state=" + state);
            }
            await Task.Delay(25, cancellation);
        }
    }
}

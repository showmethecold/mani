using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Mani.Playback;
using Mani_App.Diagnostics;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace Mani_App;

// Sequential reuse of existing probes; scope/decisions: docs/m1/combined-checks.md.
public sealed partial class MainPage
{
    private async void CombinedChecks_Click(object sender, RoutedEventArgs e) =>
        await RunProbeAsync(cancellation => RunCombinedChecksAsync(cancellation));

    private async void WindowAnd4kChecks_Click(object sender, RoutedEventArgs e) =>
        await RunProbeAsync(cancellation => RunCombinedChecksAsync(cancellation, includeHdrTiming: false));

    private async Task RunCombinedChecksAsync(CancellationToken cancellation, bool includeHdrTiming = true)
    {
        if (Owner?.AppWindow.Presenter is not OverlappedPresenter presenter || presenter.State == OverlappedPresenterState.Minimized)
            throw new InvalidOperationException("Start in a visible normal or maximized window.");
        if (_web is not null || _blurEnabled) throw new InvalidOperationException("Close WebView2 and GPU blur before starting.");
        if (DisplayText(ReadSwitchDisplay(), "kind") != "HighDynamicRange")
            throw new InvalidOperationException("Keep Windows HDR on for these combined checks.");
        var session = _session!;
        await _pollTask; _pendingSeek = _pendingVolume = null;
        string directory = NewProbeDirectory("combined-checks");
        var configuration = await session.GetPropertiesAsync("volume", "mute", "speed", "audio-device", "glsl-shaders", "glsl-shader-opts");
        var initialWindow = ReadCheckWindow(); var displayBefore = ReadSwitchDisplay();
        var initial = await session.ReadAsync();
        var oldLab = LabPanel.Visibility; var oldInfo = InfoPanel.Visibility;
        string status = "running"; string? failure = null, cleanupFailure = null;
        bool? finalPaused = null; WindowCheckState? finalWindow = null; JsonElement? displayAfter = null;
        IReadOnlyDictionary<string, string>? finalSettings = null;
        object? selected4k = null;
        string? fourKPath = null;
        string fourKReason = "No eligible real 4K SDR video was open before starting.";
        var checks = new List<Dictionary<string, object?>> {
            new() { ["id"] = "hdr-gpu", ["suite"] = "visible-gpu-timing", ["status"] = includeHdrTiming ? "not-run" : "not-selected" },
            new() { ["id"] = "overlay-window", ["suite"] = "visible-overlay-window-checks", ["status"] = "not-run" },
            new() { ["id"] = "current-4k", ["suite"] = "visible-overlay-matrix", ["status"] = "pending-media" }
        };
        async Task<string> HashAsync(string path)
        {
            await using var stream = File.OpenRead(path);
            return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellation));
        }
        string appSha256 = await HashAsync(Path.Combine(AppContext.BaseDirectory, "Mani.App.dll"));
        string playbackSha256 = await HashAsync(typeof(PlaybackSession).Assembly.Location);
        async Task SaveAsync()
        {
            var report = new { schema_version = includeHdrTiming ? 1 : 2, includeHdrTiming, suite = "m1-local-combined-checks", status, failure, cleanupFailure,
                atUtc = DateTimeOffset.UtcNow, hostProcessId = Environment.ProcessId,
                hostAssemblyId = typeof(MainPage).Module.ModuleVersionId, playbackAssemblyId = typeof(PlaybackSession).Module.ModuleVersionId,
                appSha256, playbackSha256, dllSha256 = ProbeOptions("unused").DllSha256,
                checks, selected4k, fourKReason, configuration, finalSettings, initialWindow, finalWindow, displayBefore, displayAfter,
                finalPaused, finalWebOpen = _web is not null, finalBlurEnabled = _blurEnabled, finalWebChildren = WebProbeContainer.Children.Count,
                skipped = new[] { "multi-monitor-and-disconnect-by-user", "sleep-by-user" },
                pending = fourKPath is null ? new[] { "representative-real-4k-media", "remaining-performance-attribution" } : new[] { "remaining-performance-attribution" },
                visualAccepted = false, audioAccepted = false, fullM1Accepted = false,
                limitations = "Sequential evidence collection only. Optional 4K means a user-selected BT.709 SDR file, not independently certified representative footage. Individual child reports retain their measurement boundaries. Display changes and sleep are excluded by user decision." };
            string path = Path.Combine(directory, "result.json");
            await File.WriteAllTextAsync(path + ".tmp", JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(path + ".tmp", path, overwrite: true);
        }
        async Task RunAsync(int index, Func<string, Task> action)
        {
            var entry = checks[index]; string id = (string)entry["id"]!;
            string output = Path.Combine(directory, id); Directory.CreateDirectory(output);
            entry["status"] = "running"; entry["startedUtc"] = DateTimeOffset.UtcNow;
            await SaveAsync();
            try
            {
                await action(output);
                using var child = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(output, "result.json")));
                if (child.RootElement.GetProperty("status").GetString() != "completed")
                    throw new InvalidOperationException(id + " did not complete its cleanup. See the child report.");
                entry["status"] = "completed";
            }
            catch (OperationCanceledException) { entry["status"] = "cancelled"; throw; }
            catch (Exception error) { entry["status"] = "failed"; entry["failure"] = error.ToString(); throw; }
            finally
            {
                entry["finishedUtc"] = DateTimeOffset.UtcNow;
                string reportPath = Path.Combine(output, "result.json");
                if (File.Exists(reportPath))
                {
                    entry["report"] = id + "/result.json";
                    entry["reportSha256"] = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(reportPath)));
                }
                await SaveAsync();
            }
        }
        try
        {
            await SaveAsync();
            await session.SetAsync("pause", "yes");
            // Inspect only the file explicitly open in the player; never search the user's media folders.
            if (!initial.Idle && !initial.Loading && File.Exists(initial.Path) && initial.Duration >= 40)
            {
                var native = await session.GetPropertiesAsync("video-params", "track-list");
                using var video = JsonDocument.Parse(native["video-params"]);
                var v = video.RootElement;
                if (v.ValueKind == JsonValueKind.Object && v.TryGetProperty("w", out var videoWidth) && v.TryGetProperty("h", out var videoHeight) &&
                    videoWidth.GetInt32() >= 3840 && videoHeight.GetInt32() >= 1600)
                {
                    string? transfer = v.GetProperty("gamma").GetString();
                    bool sdr = v.GetProperty("primaries").GetString() == "bt.709" && transfer is
                        ("bt.709" or "srgb" or "bt.1886" or "gamma1.8" or "gamma2.0" or "gamma2.2" or "gamma2.4" or "gamma2.6" or "gamma2.8");
                    using var tracks = JsonDocument.Parse(native["track-list"]);
                    bool audio = tracks.RootElement.EnumerateArray().Any(t => t.GetProperty("type").GetString() == "audio" &&
                        t.TryGetProperty("selected", out var selected) && selected.GetBoolean());
                    if (sdr && audio)
                    {
                        string digest = await HashAsync(initial.Path);
                        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Assets", "baseline-media.json"), cancellation));
                        bool synthetic = manifest.RootElement.EnumerateObject().Any(p => digest.Equals(p.Value.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase));
                        if (!synthetic)
                        {
                            fourKPath = initial.Path; fourKReason = "User-selected 4K SDR video included; actual content remains subject to review.";
                            selected4k = new { Path = initial.Path, Sha256 = digest, Width = v.GetProperty("w").GetInt32(), Height = v.GetProperty("h").GetInt32(), initial.Duration, initial.Position };
                            checks[2]["status"] = "not-run";
                        }
                        else fourKReason = "The open 4K file is a known synthetic fixture; real 4K remains unverified.";
                    }
                    else fourKReason = "The optional 4K stage requires a BT.709 SDR source with a selected audio track; the open file does not qualify.";
                }
            }
            checks[2]["reason"] = fourKReason; await SaveAsync();
            if (includeHdrTiming)
                await RunAsync(0, output => RunGpuTimingAsync(cancellation, hdrFixture: true, outputDirectory: output));
            cancellation.ThrowIfCancellationRequested();
            await RunAsync(1, output => RunWindowChecksAsync(cancellation, overlayWindow: true, outputDirectory: output));
            cancellation.ThrowIfCancellationRequested();
            if (fourKPath is not null)
                await RunAsync(2, async output =>
                {
                    await LifecycleProbe.OpenReadyAsync(session, fourKPath, LifecycleHostReady, cancellation);
                    double position = Math.Clamp(initial.Position, 0, initial.Duration - 25);
                    await session.CommandAsync("seek", position.ToString(CultureInfo.InvariantCulture), "absolute+exact");
                    await LifecycleProbe.WaitAsync(session, s => s.Paused && !s.Loading && Math.Abs(s.Position - position) < .15, cancellation);
                    await RunOverlayMatrixAsync(cancellation, currentVideo: true, outputDirectory: output);
                });
            status = "completed";
        }
        catch (OperationCanceledException) { status = "cancelled"; throw; }
        catch (Exception error) { status = "failed"; failure = error.ToString(); throw; }
        finally
        {
            async Task CleanAsync(Func<Task> action)
            {
                try { await action(); }
                catch (Exception error) { cleanupFailure ??= error.ToString(); status = "failed"; }
            }
            await CleanAsync(() => session.SetAsync("pause", "yes"));
            await CleanAsync(async () => { CloseWebProbe(); await _webCommandTask; });
            await CleanAsync(() => SetBlurEnabledAsync(false, CancellationToken.None));
            await CleanAsync(() => session.SetAsync("glsl-shaders", configuration["glsl-shaders"]));
            await CleanAsync(() => session.SetAsync("glsl-shader-opts", configuration["glsl-shader-opts"]));
            _blurParameters = "";
            await CleanAsync(async () =>
            {
                finalPaused = (await session.ReadAsync()).Paused;
                finalSettings = await session.GetPropertiesAsync(configuration.Keys.ToArray());
                if (finalPaused != true || finalSettings.Any(kv => configuration[kv.Key] != kv.Value) || _web is not null || _blurEnabled)
                    throw new InvalidOperationException("Combined check playback settings did not restore.");
            });
            if (!_closing)
            {
                finalWindow = ReadCheckWindow(); displayAfter = ReadSwitchDisplay();
                if (!WindowMatches(finalWindow, initialWindow) || displayAfter.Value.GetRawText() != displayBefore.GetRawText())
                { cleanupFailure ??= "Window or display changed."; status = "failed"; }
                WindowCheckBanner.Visibility = Visibility.Collapsed; LabPanel.Visibility = oldLab; InfoPanel.Visibility = oldInfo;
            }
            await SaveAsync();
            if (!_closing) LabResults.Text = WindowCheckStatus.Text = status == "completed"
                ? $"Combined checks saved. Playback paused; overlays off. {(fourKPath is null ? "Real 4K was not included. " : "The selected 4K video was included. ")}Report any picture or sound issues once for the whole run."
                : "Combined checks incomplete. Completed and partial child reports were saved; see the batch report.";
        }
    }
}

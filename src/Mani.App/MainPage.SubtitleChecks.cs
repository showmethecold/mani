using System.Security.Cryptography;
using System.Text.Json;
using Mani.Playback;
using Mani_App.Diagnostics;
using Microsoft.UI.Xaml;

namespace Mani_App;

public sealed partial class MainPage
{
    private async void SubtitleChecks_Click(object sender, RoutedEventArgs e) => await RunProbeAsync(async cancellation =>
    {
        string directory = NewProbeDirectory("subtitle-checks");
        var session = _session!;
        await _pollTask;
        _pendingSeek = null; _seeking = false;
        var oldLab = LabPanel.Visibility; var oldInfo = InfoPanel.Visibility;
        var oldControls = Controls.Visibility; var oldWeb = WebProbePanel.Visibility;
        bool oldBlur = _blurEnabled;
        var beforeDisplay = ReadSwitchDisplay();
        var audioBefore = await session.GetPropertiesAsync("volume", "mute", "audio-device");
        var logOffsets = new Dictionary<string, long>();
        foreach (var name in new[] { "mpv.log", "host-state.jsonl" })
        {
            using var stream = new FileStream(Path.Combine(_logDirectory, name), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            logOffsets[name] = stream.Length;
        }
        SubtitleVisualProbe.Result? checks = null;
        string? failure = null;
        var cleanupFailures = new List<string>();
        var copiedLogs = new Dictionary<string, object>();
        string appSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Mani.App.dll"), cancellation)));
        async Task SaveAsync()
        {
            var report = new { schema_version = 1, suite = "visible-subtitle-checks",
                status = failure is not null || cleanupFailures.Count > 0 ? "failed" : checks?.Status ?? "running",
                failure, cleanupFailures, atUtc = DateTimeOffset.UtcNow, checks, beforeDisplay,
                audioBefore, audioAfter = await session.GetPropertiesAsync("volume", "mute", "audio-device"),
                appSha256, dllSha256 = ProbeOptions("unused").DllSha256,
                hostAssemblyId = typeof(MainPage).Module.ModuleVersionId, playbackAssemblyId = typeof(PlaybackSession).Module.ModuleVersionId,
                hostProcessId = Environment.ProcessId, realTime = true, visualAccepted = false,
                previousOverlays = new { webVisible = oldWeb.ToString(), blurEnabled = oldBlur },
                currentOverlays = new { webVisible = WebProbePanel.Visibility.ToString(), blurEnabled = _blurEnabled },
                logSourceDirectory = _logDirectory, copiedLogs,
                finishPolicy = "restore changed settings and overlay visibility; remove test tracks; leave test video paused",
                limitations = "Nine diagnostic stages use libmpv paths. Completion is technical collection only; user must judge visible text, positioning and animation. Screenshots are mpv window output, excluding WinUI/DWM and not calibrated HDR images. No physical subtitle-control, other display or full M5 acceptance." };
            string file = Path.Combine(directory, "result.json");
            await File.WriteAllTextAsync(file + ".tmp", JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(file + ".tmp", file, overwrite: true);
        }
        try
        {
            await SaveAsync();
            if (_pendingVolume is double volume)
            {
                _pendingVolume = null;
                await session.SetAsync("volume", volume.ToString(System.Globalization.CultureInfo.InvariantCulture));
                audioBefore = await session.GetPropertiesAsync("volume", "mute", "audio-device");
            }
            _oscSuppressed = true;
            LabPanel.Visibility = InfoPanel.Visibility = Controls.Visibility = WebProbePanel.Visibility = Visibility.Collapsed;
            SubtitleCheckBanner.Visibility = Visibility.Visible;
            SubtitleCheckProgress.Text = "Preparing subtitle checks";
            SubtitleCheckHint.Text = "Loading the test video and two ASS tracks...";
            if (oldBlur) await SetBlurEnabledAsync(false, cancellation);
            checks = await SubtitleVisualProbe.RunAsync(session, Path.Combine(AppContext.BaseDirectory, "Assets"), directory,
                realTime: true,
                stage =>
                {
                    if (!_closing) { SubtitleCheckProgress.Text = stage.Label; SubtitleCheckHint.Text = stage.Hint; }
                    Trace("subtitle-stage", new { stage.Id, stage.Label });
                    return Task.CompletedTask;
                },
                async () =>
                {
                    await _pollTask;
                    if (_bridge?.HasAttachedSwapChain != true) throw new InvalidOperationException("The video panel is not attached.");
                    var display = ReadSwitchDisplay();
                    if (display.GetRawText() != beforeDisplay.GetRawText())
                        throw new InvalidOperationException("Display mode or scale changed during subtitle checks.");
                    var capture = await DisplaySignalProbe.CaptureAsync(session);
                    if (!DisplaySignalProbe.OutputMatchesPolicy(capture))
                        throw new InvalidOperationException("Video output no longer matches the normal display policy.");
                    return new { display, panelAttached = true,
                        width = VideoPanel.ActualWidth * VideoPanel.CompositionScaleX,
                        height = VideoPanel.ActualHeight * VideoPanel.CompositionScaleY };
                }, cancellation, async partial => { checks = partial; await SaveAsync(); });
        }
        catch (OperationCanceledException) { checks ??= new(); checks.Status = "cancelled"; }
        catch (Exception error) { failure = error.ToString(); }
        finally
        {
            if (!_closing)
            {
                LabPanel.Visibility = oldLab; InfoPanel.Visibility = oldInfo;
                _oscSuppressed = false;
                Controls.Visibility = oldControls; WebProbePanel.Visibility = oldWeb;
                ApplySurface(_snapshot);
                SubtitleCheckBanner.Visibility = Visibility.Collapsed;
                if (oldBlur && !_blurEnabled)
                    try { await SetBlurEnabledAsync(true, CancellationToken.None); }
                    catch (Exception error) { cleanupFailures.Add("Restore blur: " + error); }
            }
            try
            {
                var audioAfter = await session.GetPropertiesAsync("volume", "mute", "audio-device");
                if (audioBefore.Any(p => !SubtitleVisualProbe.EqualValue(audioAfter[p.Key], p.Value)))
                    throw new InvalidOperationException("Audio settings changed during subtitle checks.");
                await RefreshSubtitleControlsAsync();
            }
            catch (Exception error) { cleanupFailures.Add("Restore verification: " + error); }
            foreach (var pair in logOffsets)
            {
                try
                {
                    await using var source = new FileStream(Path.Combine(_logDirectory, pair.Key), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    long end = source.Length;
                    source.Position = pair.Value;
                    await using var target = File.Create(Path.Combine(directory, pair.Key));
                    byte[] buffer = new byte[65536];
                    for (long left = end - pair.Value; left > 0;)
                    {
                        int count = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(left, buffer.Length)));
                        if (count == 0) throw new EndOfStreamException("Subtitle log changed while copying.");
                        await target.WriteAsync(buffer.AsMemory(0, count)); left -= count;
                    }
                    copiedLogs[pair.Key] = new { start = pair.Value, end };
                }
                catch (Exception error) { cleanupFailures.Add("Copy log " + pair.Key + ": " + error); }
            }
            await SaveAsync();
            Trace("subtitle-checks", new { directory, status = checks?.Status, failure, cleanupFailures });
            if (!_closing)
            {
                string status = failure is not null || cleanupFailures.Count > 0 ? "failed" : checks?.Status ?? "failed";
                SubtitleCheckStatus.Text = status == "completed"
                    ? "Subtitle evidence saved (9/9 stages). The sample is paused and settings are restored. Report any text, position or animation problems."
                    : $"Subtitle checks {status}. Partial evidence saved. " + (failure ?? checks?.Failure ?? cleanupFailures.FirstOrDefault() ?? "");
                LabResults.Text = SubtitleCheckStatus.Text;
            }
        }
    });

    private async Task RefreshSubtitleControlsAsync()
    {
        var properties = await _session!.GetPropertiesAsync("sub-delay", "secondary-sub-delay", "sub-pos", "secondary-sub-pos", "sub-ass-override", "secondary-sub-ass-override");
        if (_closing) return;
        _labUpdating = true;
        try
        {
            foreach (var input in new[] { PrimaryDelay, SecondaryDelay, PrimaryPosition, SecondaryPosition })
                if (double.TryParse(properties[(string)input.Tag], System.Globalization.CultureInfo.InvariantCulture, out var value)) input.Value = value;
            foreach (var input in new[] { PrimaryStyle, SecondaryStyle })
                input.SelectedItem = input.Items.Cast<Microsoft.UI.Xaml.Controls.ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == properties[(string)input.Tag]);
        }
        finally { _labUpdating = false; }
    }
}

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Mani_App;

public sealed partial class MainPage
{
    private int _hostCycleAttempts, _performanceProbeCount;

    private async void PlaybackBaseline_Click(object sender, RoutedEventArgs e) =>
        await RunProbeAsync(async cancellation =>
        {
            if (_hostCycleAttempts != 0 || _performanceProbeCount != 0)
            {
                LabResults.Text = "Restart Mani, open your video, then measure this baseline before other performance or rebuild tests.";
                return;
            }
            await RunPerformanceProbeAsync(cancellation, playbackFirst: true, baselineCase: "current");
        });

    private async void ResolutionBaseline_Click(object sender, RoutedEventArgs e) =>
        await RunProbeAsync(async cancellation =>
        {
            if (_hostCycleAttempts != 0)
            {
                LabResults.Text = "Restart Mani before measuring playback baselines. Do not run rebuild tests in this session.";
                return;
            }
            if (_web is not null || _blurEnabled)
            {
                LabResults.Text = "Turn off WebView2 and GPU blur first.";
                return;
            }
            string label = (string)((Button)sender).Tag;
            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(
                Path.Combine(AppContext.BaseDirectory, "Assets", "baseline-media.json"), cancellation));
            var fixture = manifest.RootElement.GetProperty(label);
            string path = Path.Combine(AppContext.BaseDirectory, "Assets", fixture.GetProperty("file").GetString()!);
            await using (var stream = File.OpenRead(path))
            {
                string hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellation));
                if (!hash.Equals(fixture.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Baseline video hash mismatch.");
            }
            await _pollTask;
            _pendingSeek = null;
            _pendingVolume = null;
            _notice = "";
            await _session!.OpenAsync(path, paused: true);
            var timer = Stopwatch.StartNew();
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                var state = await _session.ReadAsync();
                if (state.Path == path && !state.Loading && state.VideoFormat.Length > 0) break;
                if (timer.Elapsed.TotalSeconds > 20)
                    throw new TimeoutException("Test video did not become ready.");
                await Task.Delay(50, cancellation);
            }
            // The button explicitly opens the test video; leave it selected afterwards.
            await RunPerformanceProbeAsync(cancellation, playbackFirst: true, baselineCase: label);
        });
}

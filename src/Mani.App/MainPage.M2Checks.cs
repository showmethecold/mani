using System.Text.Json;
using Mani.Playback;

namespace Mani_App;

public sealed partial class MainPage
{
    private static int _m2VerificationStarted;
    // Opt-in local integration harness. Normal startup never runs these checks.
    private bool StartM2VerificationIfRequested()
    {
        if (!DiagnosticsBuild.Enabled) return false;
        var directory = App.M2VerificationDirectory ?? Environment.GetEnvironmentVariable("MANI_M2_VERIFY");
        if (string.IsNullOrWhiteSpace(directory)) return false;
        // One opt-in request per process. A recovery window must be a normal
        // player, not recursively launch this same fault-injection harness.
        if (Interlocked.Exchange(ref _m2VerificationStarted, 1) != 0) return false;
        directory = Path.GetFullPath(directory);
        _ = RunProbeAsync(token => VerifyM2Async(directory, token));
        return true;
    }
    private async Task VerifyM2Async(string directory, CancellationToken cancellation)
    {
        Directory.CreateDirectory(directory);
        var results = new List<object>();
        string? failure = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        var token = timeout.Token;
        var session = _session!;
        var saved = _settings;
        // Integration scenarios must not replace the user's persisted preferences.
        bool settingsAvailable = _settingsAvailable;
        _settingsAvailable = false;
        try
        {
            string a = Path.Combine(AppContext.BaseDirectory, "Assets", "1080p-sdr.mkv");
            string b = Path.Combine(directory, "中文 空格 # [B].mkv");
            File.Copy(a, b, true);
            async Task WaitUi(long request, string path)
            {
                while (_snapshot is not { Loading: false } state || state.RequestId != request || state.Path != path ||
                    _bridge?.HasAttachedSwapChain != true || state.VideoOutput != "gpu-next")
                    await Task.Delay(25, token);
            }
            var old = session.LoadAsync(a, paused: true, cancellationToken: token);
            var current = session.LoadAsync(b, paused: true, cancellationToken: token);
            try { await old; } catch (OperationCanceledException) { }
            var ready = await current;
            await WaitUi(ready.RequestId, b);
            results.Add(new { scenario = "replace-and-unicode", passed = true, ready, panelAttached = _bridge!.HasAttachedSwapChain });
            var seeks = Enumerable.Range(1, 20).Select(i => session.SeekAsync(i / 5d, token)).ToArray();
            foreach (var seek in seeks) try { await seek; } catch (OperationCanceledException) { }
            while (_snapshot is not { Paused: true, Seeking: false } at || Math.Abs(at.Position - 4) > 0.2)
                await Task.Delay(25, token);
            results.Add(new { scenario = "paused-seek-burst", passed = true, _snapshot.Position });
            string bad = Path.Combine(directory, "bad.bin");
            await File.WriteAllTextAsync(bad, "not media", token);
            bool rejected = false;
            try { await session.LoadAsync(bad, cancellationToken: token); }
            catch (InvalidOperationException) { rejected = true; }
            if (!rejected || (await session.ReadAsync()).State != PlaybackState.Failed)
                throw new InvalidOperationException("Bad file was accepted as ready.");
            while (_snapshot?.State != PlaybackState.Failed || string.IsNullOrWhiteSpace(Status.Text)) await Task.Delay(25, token);
            ready = await session.LoadAsync(b, paused: true, cancellationToken: token);
            await WaitUi(ready.RequestId, b);
            results.Add(new { scenario = "failure-and-recovery-ui", passed = true, status = Status.Text });
            string script = Path.Combine(directory, "observed.lua");
            await File.WriteAllTextAsync(script, "mp.set_property_number('volume', 23)\nmp.set_property_number('speed', 1.5)\nmp.set_property_bool('pause', true)\n", token);
            await session.CommandAsync(token, "load-script", script);
            while (_snapshot is not { Volume: 23, Speed: 1.5, Paused: true } || Volume.Value != 23) await Task.Delay(25, token);
            results.Add(new { scenario = "script-to-ui", passed = true, _snapshot.Volume, _snapshot.Speed, _snapshot.Paused });
            await session.SetVolumeAsync(saved.VolumePercent);
            await session.SetAsync("mute", saved.Muted ? "yes" : "no");
            try { await session.CommandAsync(token, "quit"); }
            catch (InvalidOperationException) { }
            while (!_sessionUnavailable) await Task.Delay(25, token);
            if (_timer.IsEnabled || OpenButton.IsEnabled || PlayButton.IsEnabled || Timeline.IsEnabled || CanReuseWindow ||
                Status.Text != UiText.Get("SessionUnavailable")) throw new InvalidOperationException("Failed session did not enter its recovery surface.");
            int errors = File.ReadAllLines(Path.Combine(_logDirectory, "host-errors.txt")).Length;
            await Task.Delay(1200, token);
            if (File.ReadAllLines(Path.Combine(_logDirectory, "host-errors.txt")).Length != errors)
                throw new InvalidOperationException("Session failure produced repeated polling errors.");
            _m1Busy = false; // Prove recovery does not rely on the diagnostic busy flag.
            if (CanReuseWindow) throw new InvalidOperationException("A failed idle window is reusable.");
            int windows = App.Instance.PlayerWindows.Windows.Count;
            App.Instance.NewWindow();
            if (App.Instance.PlayerWindows.Windows.Count != windows + 1)
                throw new InvalidOperationException("The unavailable window was incorrectly reused.");
            results.Add(new { scenario = "session-failure-stops-polling-and-opens-new-window", passed = true, status = Status.Text });
        }
        catch (Exception error) { failure = error.ToString(); }
        finally
        {
            if (!_closing && session.IsAvailable)
            {
                try
                {
                    await session.SetVolumeAsync(saved.VolumePercent);
                    await session.SetAsync("mute", saved.Muted ? "yes" : "no");
                    await session.SetSpeedAsync(1);
                    await session.SetPausedAsync(true);
                }
                catch (Exception error) { failure ??= error.ToString(); }
            }
            _settings = saved;
            // Keep persistence disabled until this verification process exits.
            if (_closing) _settingsAvailable = settingsAvailable;
            await File.WriteAllTextAsync(Path.Combine(directory, "window-results.json"), JsonSerializer.Serialize(new
            {
                completed = failure is null, failure, results, processId = Environment.ProcessId,
                sessionLog = _logDirectory, scope = "Engine, WinUI projection and composition attachment; not a visual or audio judgment."
            }, new JsonSerializerOptions { WriteIndented = true }));
            if (!_closing && Owner is not null)
                PostMessageW(WinRT.Interop.WindowNative.GetWindowHandle(Owner), 0x0010, 0, 0);
        }
    }
}

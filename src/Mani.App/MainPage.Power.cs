using Mani_App.Diagnostics;
using Mani_App.Interop;
using Microsoft.UI.Xaml;

namespace Mani_App;

public sealed partial class MainPage
{
    private PowerObserver? _powerObserver;
    private Task _sleepPauseTask = Task.CompletedTask;
    private SleepWakeSequence? _sleepCheck;
    private void WatchPower() => _powerObserver = new(WinRT.Interop.WindowNative.GetWindowHandle(Owner!), PowerNotification);
    private void PowerNotification(int code)
    {
        if (_closing) return;
        var check = _sleepCheck;
        check?.Receive(code);
        Trace("power-notification", new { code });
        // IINA 1.5.1 defaults pauseWhenGoesToSleep=true. No automatic play on wake.
        if (code == 4 && _session is not null)
        {
            _playbackRevision++;
            _sleepPauseTask = PauseForSleepAsync(_session, check);
        }
    }
    private async Task PauseForSleepAsync(Mani.Playback.PlaybackSession session, SleepWakeSequence? check)
    {
        try
        {
            var result = await session.PauseForSystemSleepAsync();
            check?.Paused(result);
            Trace("sleep-pause-completed", result);
        }
        catch (Exception error)
        {
            check?.Fail("Sleep pause failed: " + error.Message);
            Trace("sleep-pause-failed", new { error = error.ToString() });
            if (!_closing) ShowError(error);
        }
    }
    private void StopPowerWatch()
    {
        _powerObserver?.Dispose();
        if (_powerObserver is { Released: false } || _powerObserver?.CleanupFailure is not null)
            Trace("power-observer-cleanup-failed", new { _powerObserver?.CleanupFailure });
    }
    private async void SleepChecks_Click(object sender, RoutedEventArgs e) =>
        await RunProbeAsync(cancellation => RunWindowChecksAsync(cancellation, sleep: true));
    private void SleepContinue_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_sleepCheck?.Confirm() == true)
            {
                SleepContinueButton.IsEnabled = false;
                WindowCheckProgress.Text = "Checking the paused picture after wake, then testing playback.";
            }
        }
        catch (Exception error) { ShowError(error); }
    }
}


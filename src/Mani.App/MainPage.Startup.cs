using Mani_App.Diagnostics;

namespace Mani_App;

public sealed partial class MainPage
{
    private StartupProbe? _startupProbe;
    private object StartupContext() => new {
        sessionInitialized = _session is not null, closing = _closing, mediaPath = _snapshot?.Path ?? "",
        webEnabled = _web is not null, blurEnabled = _blurEnabled,
        uiThread = DispatcherQueue.HasThreadAccess, display = DisplayDetails(),
        window = Owner is null ? null : new { width = Owner.AppWindow.Size.Width, height = Owner.AppWindow.Size.Height,
            x = Owner.AppWindow.Position.X, y = Owner.AppWindow.Position.Y, Owner.AppWindow.IsVisible },
        panel = new { VideoPanel.ActualWidth, VideoPanel.ActualHeight, VideoPanel.CompositionScaleX, VideoPanel.CompositionScaleY }
    };
    private void InitializeStartupIfRequested()
    {
        if (!DiagnosticsBuild.Enabled) return;
        string tests = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mani", "Tests");
        if (!File.Exists(Path.Combine(tests, "pending-startup.json"))) return;
        var request = StartupRequest.Claim(tests);
        _startupProbe = new StartupProbe(request, tests, _logDirectory, Path.Combine(AppContext.BaseDirectory, "libmpv-2.dll"));
        _startupProbe.Capture("before-engine", StartupContext());
    }
    private bool StartStartupIfRequested()
    {
        if (!DiagnosticsBuild.Enabled) return false;
        if (_startupProbe is null) return false;
        _ = RunProbeAsync(RunStartupAsync);
        return true;
    }
    private async Task RunStartupAsync(CancellationToken shutdown)
    {
        var probe = _startupProbe!;
        try
        {
            probe.Capture("engine-ready", StartupContext());
            await probe.WaitForObserverAsync(shutdown);
            for (int i = 0; i < 10; i++)
            {
                await Task.Delay(1000, shutdown);
                probe.CheckCancellation();
                if (_notice.Length > 0) throw new InvalidOperationException("Host error during startup observation: " + _notice);
                if (_web is not null || _blurEnabled || !string.IsNullOrEmpty(_snapshot?.Path))
                    throw new InvalidOperationException("Startup observation requires an idle player without overlays.");
            }
            probe.Capture("idle-settled", StartupContext());
            probe.RequestClose();
            if (Owner is null || !PostMessageW(WinRT.Interop.WindowNative.GetWindowHandle(Owner), 0x0010, 0, 0))
                throw new InvalidOperationException("Could not request normal window close.");
            await Task.Delay(Timeout.Infinite, shutdown);
        }
        catch (OperationCanceledException) when (_closing && probe.CloseRequested && shutdown.IsCancellationRequested) { }
        catch (Exception error) { probe.Fail(error); throw; }
    }
    internal void FinishStartupShutdown(Exception? error)
    {
        if (_startupProbe is null) return;
        try { _startupProbe.Finish(error, StartupContext()); }
        catch (Exception saveError) { ShowError(saveError); }
    }
}

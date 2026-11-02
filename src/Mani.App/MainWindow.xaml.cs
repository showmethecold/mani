using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using System.Runtime.InteropServices;

namespace Mani_App;

public sealed partial class MainWindow : Window
{
    private bool _closing;
    private bool _allowClose;
    private Task? _closeTask;
    internal bool CanReuse => !_closing && ((MainPage)RootFrame.Content).CanReuseWindow;
    internal bool IsClosing => _closing;
    internal void ActivatePlayer()
    {
        if (_closing) return;
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter) presenter.Restore();
        Activate();
    }
    public MainWindow()
    {
        InitializeComponent();
        // Use the system caption's UIA provider. The pinned App SDK custom caption
        // leaves stale provider references after fullscreen/client teardown.
        ExtendsContentIntoTitleBar = false;
        AppWindow.SetIcon("Assets/AppIcon.ico");
        UpdateCaptionColors(false);
        InitializeWindowSizing();
        // Fixed IINA's initial content is 640 x 400 points; adapt the native caption
        // and display scaling, then keep the complete frame within this work area.
        var metrics = SizingMetrics();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(
            (int)Math.Min(metrics.Work.Width, Math.Round(640 * metrics.Scale + metrics.Chrome.Width)),
            (int)Math.Min(metrics.Work.Height, Math.Round(400 * metrics.Scale + metrics.Chrome.Height))));
        RootFrame.Navigate(typeof(MainPage));
        ((MainPage)RootFrame.Content).Owner = this;
        AppWindow.Closing += OnClosing;
    }
    internal bool IsFullscreen => AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen;
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, uint attribute, ref int value, uint size);
    internal void UpdateCaptionColors(bool highContrast)
    {
        // DWM retains native buttons, hit testing, Snap and accessibility. In high
        // contrast, let Windows choose the caption colors instead of forcing dark.
        int dark = highContrast ? 0 : 1;
        _ = DwmSetWindowAttribute(WinRT.Interop.WindowNative.GetWindowHandle(this), 20, ref dark, sizeof(int));
    }
    internal void SetMediaTitle(string title)
    {
        string next = string.IsNullOrEmpty(title) ? "Mani" : title + " — Mani";
        if (Title != next) Title = next;
    }
    internal void ToggleFullscreen()
    {
        if (_closing) return;
        ((MainPage)RootFrame.Content).EndWindowDrag();
        if (IsMusicMode)
        {
            ((MainPage)RootFrame.Content).ReturnFromMusicMode();
            if (IsFullscreen) return;
        }
        if (IsPictureInPicture)
        {
            ExitPictureInPicture();
            if (IsFullscreen) return;
        }
        if (!IsFullscreen) { Floating.EnterFullscreen(); ApplyWindowFloating(); }
        try { ToggleSizingFullscreen(); }
        finally
        {
            if (!IsFullscreen) Floating.ExitFullscreen();
            ApplyWindowFloating();
        }
    }
    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose) return;
        args.Cancel = true;
        await CloseSessionAsync();
    }
    internal Task CloseSessionAsync(bool exitApp = false)
    {
        if (_closeTask is not null) return _closeTask;
        // Closing the PiP view returns to its main window; it does not stop the session.
        if (!exitApp && IsPictureInPicture) { ExitPictureInPicture(); return Task.CompletedTask; }
        return _closeTask = CloseCoreAsync();
    }
    private async Task CloseCoreAsync()
    {
        _closing = true;
        var page = (MainPage)RootFrame.Content;
        Exception? shutdownFailure = null;
        page.RecordLifecycleCloseReceived();
        try { await page.ShutdownAsync(); }
        catch (Exception error)
        {
            shutdownFailure = error;
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mani", "Logs");
            try { Directory.CreateDirectory(directory); File.AppendAllText(Path.Combine(directory, "shutdown-errors.txt"), $"{DateTimeOffset.Now:O} {error}\n"); }
            catch (Exception logError) when (logError is IOException or UnauthorizedAccessException) { }
        }
        finally
        {
            try { page.FinishLifecycleShutdown(shutdownFailure); }
            finally
            {
                try { page.CloseTrace(); page.FinishStartupShutdown(shutdownFailure); }
                finally { _allowClose = true; Close(); }
            }
        }
    }
}

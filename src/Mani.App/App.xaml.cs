using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace Mani_App;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    internal static App Instance => (App)Current;
    internal WindowRegistry<MainWindow> PlayerWindows { get; } = new();
    internal SharedPlayerSettings Settings { get; } = new(new Mani.Playback.PlayerSettingsStore(
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mani", "settings.json")));
    internal static string DataDirectory => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mani");
    internal Mani.Playback.PlaybackHistoryStore History { get; } = new(System.IO.Path.Combine(DataDirectory, "history.json"));
    internal static string WatchLaterDirectory => System.IO.Path.Combine(DataDirectory, "watch-later");
    private readonly Lazy<Task<Mani.Playback.ThumbnailFrameCapture>> _thumbnailDecoder = new(async () =>
    {
        using var runtime = System.Text.Json.JsonDocument.Parse(await System.IO.File.ReadAllTextAsync(
            System.IO.Path.Combine(AppContext.BaseDirectory, "mpv-runtime.json")).ConfigureAwait(false));
        return new Mani.Playback.ThumbnailFrameCapture(new Mani.Playback.PlaybackOptions(
            System.IO.Path.Combine(AppContext.BaseDirectory, "libmpv-2.dll"), runtime.RootElement.GetProperty("sha256").GetString()!, ""));
    });
    internal async Task<string> CaptureThumbnailAsync(string source, double seconds, string destination, CancellationToken token)
    {
        var decoder = await _thumbnailDecoder.Value.ConfigureAwait(false);
        return await decoder.CaptureAsync(source, seconds, destination, token).ConfigureAwait(false);
    }
    private Task? _quitTask;
    internal static string? M2VerificationDirectory { get; private set; }
    
    /// <summary>
    /// Initializes the singleton application object.  This is the first line of authored code
    /// executed, and as such is the logical equivalent of main() or WinMain().
    /// </summary>
    public App()
    {
        UnhandledException += (_, error) =>
        {
            try
            {
                var directory = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mani", "Logs");
                System.IO.Directory.CreateDirectory(directory);
                System.IO.File.AppendAllText(System.IO.Path.Combine(directory, "startup-errors.txt"), $"{DateTimeOffset.Now:O} {error.Message}\n{error.Exception}\n");
            }
            catch (Exception logError) when (logError is System.IO.IOException or UnauthorizedAccessException) { }
        };
        Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = "en-US";
        UiText.SetLanguage("en-US");
        Settings.Changed += settings => UiText.SetLanguage(settings.Language);
        InitializeComponent();
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        const string verification = "--m2-verify=";
        var request = Environment.GetCommandLineArgs().Skip(1).FirstOrDefault(value => value.StartsWith(verification, StringComparison.Ordinal));
        if (request is null && args.Arguments.StartsWith(verification, StringComparison.Ordinal)) request = args.Arguments;
        if (DiagnosticsBuild.Enabled && request is not null)
            M2VerificationDirectory = System.IO.Path.GetFullPath(request[verification.Length..].Trim().Trim('"'));
        try { UiText.SetLanguage((await Settings.LoadAsync()).Language); }
        catch (Exception error) when (SharedPlayerSettings.IsStorageError(error)) { UiText.SetLanguage("en-US"); }
        NewWindow();
    }

    // IINA menuNewWindow -> newPlayerCore: reuse idle, otherwise retain a new session.
    internal void NewWindow()
    {
        var window = PlayerWindows.GetOrCreate(window => window.CanReuse, () =>
        {
            var created = new MainWindow();
            created.Closed += (_, _) => PlayerWindows.Remove(created);
            return created;
        });
        window?.ActivatePlayer();
    }
    internal Task QuitAsync() => _quitTask ??= CloseAllAsync();
    private async Task CloseAllAsync()
    {
        var windows = PlayerWindows.BeginQuit();
        // Window shutdown cancels previews before waiting for the decoder gate.
        await Task.WhenAll(windows.Select(window => window.CloseSessionAsync(exitApp: true)));
        if (_thumbnailDecoder.IsValueCreated) await (await _thumbnailDecoder.Value).DisposeAsync();
    }
}

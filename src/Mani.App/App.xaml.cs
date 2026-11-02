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
    private Microsoft.Windows.AppLifecycle.AppInstance? _activationInstance;
    private Task _activationTail = Task.CompletedTask;
    // The installer checks this marker before replacing or removing program files.
    private readonly Mutex _installationLifetime = new(false,
#if MANI_ISOLATED_TEST
        "Mani.Desktop.InstallTest.Running");
#else
        "Mani.Desktop.Running");
#endif
    internal static App Instance => (App)Current;
    internal WindowRegistry<MainWindow> PlayerWindows { get; } = new();
    internal SharedPlayerSettings Settings { get; } = new(new Mani.Playback.PlayerSettingsStore(
        System.IO.Path.Combine(DataDirectory, "settings.json")));
    internal static string DataDirectory =>
#if MANI_ISOLATED_TEST
        System.IO.Path.Combine(AppContext.BaseDirectory, "Mani.IsolatedFileActivationTests");
#else
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mani");
#endif
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
                var directory = System.IO.Path.Combine(DataDirectory, "Logs");
                System.IO.Directory.CreateDirectory(directory);
                System.IO.File.AppendAllText(System.IO.Path.Combine(directory, "startup-errors.txt"), $"{DateTimeOffset.Now:O} {error.Message}\n{error.Exception}\n");
            }
            catch (Exception logError) when (logError is System.IO.IOException or UnauthorizedAccessException) { }
        };
        try
        {
            // Windows App SDK supports both MSIX and ordinary desktop installs.
            Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = "en-US";
            UiText.SetLanguage("en-US");
            Settings.Changed += settings => UiText.SetLanguage(settings.Language);
            InitializeComponent();
        }
        catch (Exception error)
        {
            try
            {
                var directory = System.IO.Path.Combine(DataDirectory, "Logs");
                System.IO.Directory.CreateDirectory(directory);
                System.IO.File.AppendAllText(System.IO.Path.Combine(directory, "startup-errors.txt"), $"{DateTimeOffset.Now:O} {error}\n");
            }
            catch (Exception logError) when (logError is System.IO.IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        // Read once, before the first await: packaged rich activation is consumable.
        var current = Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent();
        var activation = current.GetActivatedEventArgs();
        _activationInstance = Microsoft.Windows.AppLifecycle.AppInstance.FindOrRegisterForKey("player");
        if (!_activationInstance.IsCurrent)
        {
            await _activationInstance.RedirectActivationToAsync(activation);
            Exit();
            return;
        }
        var files = ReadActivationFiles(activation);
        _activationTail = LoadInterfaceLanguageAsync();
        var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _activationInstance.Activated += (_, redirected) =>
        {
            // Materialize the payload before acknowledging the redirect. Its
            // originating process may exit while a prior open is still queued.
            var redirectedFiles = ReadActivationFiles(redirected);
            dispatcher.TryEnqueue(() =>
                _activationTail = OpenActivationAfterAsync(_activationTail, redirectedFiles));
        };
        const string verification = "--m2-verify=";
        var request = Environment.GetCommandLineArgs().Skip(1).FirstOrDefault(value => value.StartsWith(verification, StringComparison.Ordinal));
        if (request is null && args.Arguments.StartsWith(verification, StringComparison.Ordinal)) request = args.Arguments;
        if (DiagnosticsBuild.Enabled && request is not null)
            M2VerificationDirectory = System.IO.Path.GetFullPath(request[verification.Length..].Trim().Trim('"'));
        // Assign the initial operation before yielding so a redirected launch
        // cannot overtake initialization or race a partially opened playlist.
        _activationTail = OpenActivationAfterAsync(_activationTail, files);
        await _activationTail;
#if MANI_ISOLATED_TEST
        // Exercise the actual MRT resource system and shared preference writer
        // in isolated installation tests. This code is absent in ordinary builds.
        if (Environment.GetCommandLineArgs().Contains("--desktop-language-test"))
        {
            var samples = new List<object>();
            var original = Settings.Current;
            foreach (var language in new[] { "en-US", "zh-CN", original.Language })
            {
                await Settings.UpdateAsync(Settings.Current, Settings.Current with { Language = language });
                samples.Add(new { language, open = UiText.Get("Open"), settings = UiText.Get("SettingsTitle") });
            }
            await Settings.FlushAsync();
            await System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(DataDirectory, "language-test.json"),
                System.Text.Json.JsonSerializer.Serialize(samples));
        }
#endif
    }

    private async Task LoadInterfaceLanguageAsync()
    {
#if !MANI_ISOLATED_TEST
        try
        {
            var legacy = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Packages", "Mani.Beta.Test_h6anv01wm286t", "LocalCache", "Local", "Mani");
            await Task.Run(() => LegacyUserDataMigration.ImportIfMissing(legacy, DataDirectory));
        }
        catch (Exception error) when (SharedPlayerSettings.IsStorageError(error))
        {
            // The old data is never deleted. Existing store recovery still applies.
            System.Diagnostics.Trace.TraceError("Previous installation data could not be imported: {0}", error.Message);
        }
#endif
        try { UiText.SetLanguage((await Settings.LoadAsync()).Language); }
        catch (Exception error) when (SharedPlayerSettings.IsStorageError(error)) { UiText.SetLanguage("en-US"); }
    }

    private static IReadOnlyList<string> ReadActivationFiles(Microsoft.Windows.AppLifecycle.AppActivationArguments activation) =>
        activation.Data is IFileActivatedEventArgs fileArgs
            ? FileActivationRequest.FromPaths(fileArgs.Files.Select(file => file.Path))
            : activation.Data is ILaunchActivatedEventArgs launch
                ? FileActivationRequest.FromCommandLine(launch.Arguments, Environment.ProcessPath!)
                : Array.Empty<string>();

    private async Task OpenActivationAfterAsync(Task previous, IReadOnlyList<string> files)
    {
        await previous;
        if (PlayerWindows.IsQuitting) return;
        var window = NewWindow();
        if (window is not null && files.Count > 0) await window.OpenActivatedFilesAsync(files);
    }

    // IINA menuNewWindow -> newPlayerCore: reuse idle, otherwise retain a new session.
    internal MainWindow? NewWindow()
    {
        var window = PlayerWindows.GetOrCreate(window => window.CanReuse, () =>
        {
            var created = new MainWindow();
            created.Closed += (_, _) => PlayerWindows.Remove(created);
            return created;
        });
        window?.ActivatePlayer();
        return window;
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

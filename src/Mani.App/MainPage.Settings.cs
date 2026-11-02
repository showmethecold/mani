using Mani.Playback;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Mani_App;

public sealed partial class MainPage
{
    private SharedPlayerSettings? _settingsStore;
    private PlayerSettings _settingsBaseline = new();
    private bool _publishingSettings, _sharedLayoutPending;
    private PlayerSettings _settings = new();
    private bool _settingsAvailable;
    private Task _settingsWrite = Task.CompletedTask;
    private CancellationTokenSource? _openCancellation;

    private async Task LoadSettingsAsync()
    {
        _settingsStore = App.Instance.Settings;
        try
        {
            _settings = _settingsBaseline = await _settingsStore.LoadAsync();
            _settingsAvailable = true;
            if (_settingsStore.Recovered) Trace("settings-restored-from-backup", new { _settingsStore.RecoveryAdjustedAutoHide });
            if (!_closing) _settingsStore.Changed += ReceiveSharedSettings;
        }
        catch (Exception error) when (SharedPlayerSettings.IsStorageError(error))
        { Trace("settings-load-failed", new { error.Message }); ShowNotice("SettingsUnavailable"); }
        Owner?.SetAutomaticFloating(_settings.AlwaysFloatOnTop);
        if (_session is not null)
        {
            await ConfigureResumeAsync();
            await ConfigureMediaPreferencesAsync();
            await _session.SetVolumeAsync(_settings.VolumePercent);
            await _session.SetAsync("mute", _settings.Muted ? "yes" : "no");
        }
    }
    private void PersistObservedSettings(PlaybackSnapshot state)
    {
        var next = _settings with { VolumePercent = Math.Clamp(state.Volume, 0, _settings.Media.VolumeLimit), Muted = state.Muted };
        if (next == _settings) return;
        _settings = next;
        SaveSettings();
    }
    private void SaveSettings()
    {
        if (!_settingsAvailable || _settingsStore is null) return;
        var baseline = _settingsBaseline;
        _settingsBaseline = _settings;
        _publishingSettings = true;
        try { _settingsWrite = ObserveSettingsWriteAsync(_settingsStore.UpdateAsync(baseline, _settings)); }
        finally { _publishingSettings = false; }
    }
    private async Task ObserveSettingsWriteAsync(Task write)
    {
        try { await write; }
        catch (Exception error) when (SharedPlayerSettings.IsStorageError(error))
        { Trace("settings-save-failed", new { error.Message }); }
    }
    private void ReceiveSharedSettings(PlayerSettings shared)
    {
        if (_closing) return;
        var before = _settings;
        _settings = SharedPlayerSettings.ForExistingWindow(shared, _settingsBaseline, _settings);
        _settingsBaseline = shared with { VolumePercent = _settings.VolumePercent, Muted = _settings.Muted };
        if (_publishingSettings || before == _settings) return;
        _sharedLayoutPending = true;
        // Keep the flyout anchor stable until its accessible popup has closed.
        if (!_menuOpen) ApplySharedSettings();
    }
    private void ApplySharedSettings()
    {
        if (!_sharedLayoutPending || _closing) return;
        _sharedLayoutPending = false;
        Owner?.SetAutomaticFloating(_settings.AlwaysFloatOnTop);
        LayoutOsc(); UpdateLayoutSidebar(); ConfigureWindowSizing();
        if (WindowSizing.AspectLocked(_settings)) Owner?.ResizeForVideo(false, false, false, keepSize: true);
        RefreshWindowFloating(); UpdateWindowSizeMenu();
        ApplyInterfacePreferences();
        _ = ConfigureResumeAsync();
        _ = ConfigureMediaPreferencesAsync();
        Trace("shared-settings-applied", new { _settings.OscPosition, _settings.EdgeToEdgeVideo, _settings.AlwaysFloatOnTop });
    }
    private void StopSettingsWatch()
    {
        if (_settingsStore is not null) _settingsStore.Changed -= ReceiveSharedSettings;
    }
    private async void OpenUrl_Click(object sender, RoutedEventArgs e) => await PromptUrlAsync();
    private async Task PromptUrlAsync(bool append = false)
    {
        if (_dialogOpen || _pickerOpen || _closing || _m1Busy || _session is null) return;
        _dialogOpen = true;
        RefreshOscHolds(); UpdateOsc();
        try
        {
            var input = new TextBox { Header = UiText.Get("UrlHeader"), PlaceholderText = "https://", MinWidth = 200, AcceptsReturn = false };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(input, "UrlInput");
            string titleKey = append ? "AddUrlTitle" : "OpenUrlTitle", actionKey = append ? "Add" : "Open";
            var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = UiText.Get(titleKey), Content = input,
                PrimaryButtonText = UiText.Get(actionKey), CloseButtonText = UiText.Get("Cancel"), DefaultButton = ContentDialogButton.Primary };
            _refreshOtherDialog = () => { dialog.Title = UiText.Get(titleKey); input.Header = UiText.Get("UrlHeader"); dialog.PrimaryButtonText = UiText.Get(actionKey); dialog.CloseButtonText = UiText.Get("Cancel"); };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || _closing) return;
            if (!Uri.TryCreate(input.Text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            { ShowNotice("InvalidUrl"); return; }
            if (append) await OpenSourcesAsync([input.Text.Trim()], append: true);
            else await OpenFileAsync(input.Text.Trim());
        }
        catch (Exception error) { ShowError(error); }
        finally { _refreshOtherDialog = null; _dialogOpen = false; RefreshOscHolds(); UpdateOsc(); RestorePlayerMenuFocus(); }
    }
    private async void Speed_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && double.TryParse(item.Tag?.ToString(), System.Globalization.CultureInfo.InvariantCulture, out var speed))
            await Execute(s => s.SetSpeedAsync(speed));
    }
    private async void FileLoop_Click(object sender, RoutedEventArgs e) => await ToggleFileLoopAsync();
    // IINA's default binding cycles the engine property atomically, including repeats.
    private Task ToggleFileLoopAsync() => Execute(s => s.CommandAsync("cycle-values", "loop", "inf", "no"));
    private async void LoopOff_Click(object sender, RoutedEventArgs e) => await Execute(s => s.SetLoopAsync(PlaybackLoop.Off));
}

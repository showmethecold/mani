// IINA 1.5.1/build 180 SidebarAudioPane/SidebarSubtitlesPane and SettingsPageAudio/Subtitles.
// Copyright (c) 2016-2026 lhc and contributors. GPL-3.0; licenses/IINA-GPL-3.0.txt.
using System.Globalization;
using Mani.Playback;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Windows.Storage.Pickers;

namespace Mani_App;

public sealed partial class MainPage
{
    private readonly List<Action> _mediaControlRefresh = [];
    private readonly List<ComboBox> _audioDeviceBoxes = [];
    private readonly List<TextBlock> _audioStatusLabels = [];
    private readonly List<Action> _subtitleContextRefresh = [];
    private MediaInfo? _subtitleContextInfo;
    private readonly HashSet<string> _unsupportedMediaPreferences = [];
    private Task _mediaPreferencesTask = Task.CompletedTask;
    private MediaPreferences? _mediaPreferencesApplied;
    private readonly Dictionary<string, string> _subtitleChannelValues = new(StringComparer.Ordinal);
    private int _audioDeviceSelectionRevision;
    private bool _mediaControlsUpdating;
    private AudioOutputState? _audioOutput;
    private long _nextAudioRead;
    private CancellationTokenSource? _matchingCancellation;
    private long _matchingRequest = -1;
    private long _matchingEntry = -1;
    private Task _matchingTask = Task.CompletedTask;
    private int _subtitleManualRevision;
    private TextBlock _trackOperationStatus = new() { TextWrapping = TextWrapping.Wrap };
    private string _trackOperationStatusKey = "";

    private void InitializeMediaControls()
    {
        void Button(StackPanel pane, string key, Func<Task> action)
        {
            var button = new Button { HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetAutomationId(button, key);
            _mediaControlRefresh.Add(() => { button.Content = new TextBlock { Text = UiText.Get(key), TextWrapping = TextWrapping.Wrap }; AutomationProperties.SetName(button, UiText.Get(key)); });
            button.Click += async (_, _) => await action();
            if (key == "M5LoadAudio" || key == "M5LoadPrimary") pane.Children.Insert(0, button);
            else if (key == "M5LoadSecondary" && QuickSecondaryTrack.Parent is StackPanel secondary) secondary.Children.Insert(0, button);
            else pane.Children.Add(button);
        }
        Button(QuickAudioPane, "M5LoadAudio", () => PickExternalTrackAsync(ExternalTrackKind.Audio));
        QuickAudioPane.Children.Add(BuildMediaPreferencePanel(true, _mediaControlRefresh, quick: true));
        Button(QuickSubtitlesPane, "M5LoadPrimary", () => PickExternalTrackAsync(ExternalTrackKind.PrimarySubtitle));
        Button(QuickSubtitlesPane, "M5LoadSecondary", () => PickExternalTrackAsync(ExternalTrackKind.SecondarySubtitle));
        Button(QuickSubtitlesPane, "M5Reload", ReloadProductSubtitlesAsync);
        AutomationProperties.SetAutomationId(_trackOperationStatus, "M5TrackStatus");
        AutomationProperties.SetLiveSetting(_trackOperationStatus, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        QuickSubtitlesPane.Children.Add(_trackOperationStatus);
        QuickSubtitlesPane.Children.Add(BuildMediaPreferencePanel(false, _mediaControlRefresh, quick: true));
        RefreshMediaControls();
    }
    private StackPanel BuildMediaPreferencePanel(bool audio, List<Action> refresh, bool quick = false)
    {
        var panel = new StackPanel { Spacing = 12 };
        bool Updating() => _mediaControlsUpdating || _updatingInterfaceSettings;
        bool sessionControls = quick && !audio;
        var fields = sessionControls ? MediaPreferences.SubtitleChannelFields : MediaPreferences.Fields;
        foreach (var group in fields.Where(f => audio ? f.Group is "Audio" or "Equalizer" : f.Group.StartsWith("Subtitle", StringComparison.Ordinal))
            .Where(f => !quick || audio || f.Group == "SubtitleChannels").GroupBy(f => f.Group))
        {
            var content = new StackPanel { Spacing = 10 };
            var expander = new Expander { HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Content = content, IsExpanded = group.Key is "Audio" or "SubtitleChannels" or "SubtitleLoad" };
            panel.Children.Add(expander);
            refresh.Add(() => expander.Header = UiText.Get("M5Group" + (group.Key == "SubtitleChannels" && !sessionControls ? "SubtitleDefaults" : group.Key)));
            if (group.Key == "Equalizer") content.Children.Add(BuildEqualizerProfiles(refresh));
            if (group.Key is "SubtitleStyle" or "SubtitleChannels" or "Audio" or "SubtitleLoad")
            {
                var hint = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
                content.Children.Add(hint); refresh.Add(() => hint.Text = UiText.Get("M5Hint" + (group.Key == "SubtitleChannels" && !sessionControls ? "SubtitleDefaults" : group.Key)));
                if (group.Key is "SubtitleStyle" or "SubtitleChannels")
                {
                    var context = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
                    content.Children.Add(context);
                    Action update = () => context.Text = SubtitleStyleContext();
                    refresh.Add(update);
                    context.Loaded += (_, _) => { _subtitleContextRefresh.Add(update); update(); };
                    context.Unloaded += (_, _) => _subtitleContextRefresh.Remove(update);
                }
            }
            foreach (var field in group)
            {
                string id = "M5" + (!sessionControls && field.Group == "SubtitleChannels" ? "Default" : "") + field.Key;
                string Value() => sessionControls ? _subtitleChannelValues.GetValueOrDefault(field.Property, field.Default) : _settings.Media.Get(field.Key);
                Task Commit(string value) => sessionControls ? CommitSubtitleChannelAsync(field, value) : CommitMediaPreferenceAsync(field, value);
                var heading = new TextBlock { TextWrapping = TextWrapping.Wrap };
                Control control;
                if (field.Key == "AudioDevice")
                {
                    var box = new ComboBox { Header = heading, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, DisplayMemberPath = "Description", MaxDropDownHeight = 300 };
                    control = box;
                    box.Loaded += (_, _) => { if (!_audioDeviceBoxes.Contains(box)) _audioDeviceBoxes.Add(box); };
                    box.Unloaded += (_, _) => _audioDeviceBoxes.Remove(box);
                    refresh.Add(() => RefreshAudioDeviceBox(box));
                    box.SelectionChanged += async (_, _) => { if (!Updating() && box.SelectedItem is AudioDevice device) await CommitAudioDeviceAsync(device.Name); };
                    var status = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
                    status.Loaded += (_, _) => { if (!_audioStatusLabels.Contains(status)) _audioStatusLabels.Add(status); RefreshAudioStatus(); };
                    status.Unloaded += (_, _) => _audioStatusLabels.Remove(status);
                    content.Children.Add(status);
                    var reload = new Button();
                    AutomationProperties.SetAutomationId(reload, "M5RefreshDevices");
                    refresh.Add(() => reload.Content = UiText.Get("M5RefreshDevices"));
                    reload.Click += async (_, _) => { _nextAudioRead = 0; await ObserveAudioOutputAsync(); };
                    content.Children.Add(reload);
                }
                else if (field.Choices is { } choices || field.Key == "SubtitleEncoding")
                {
                    var values = field.Choices ?? MediaPreferences.Encodings.ToArray();
                    var box = new ComboBox { Header = heading, HorizontalAlignment = HorizontalAlignment.Stretch, MaxDropDownHeight = 300 };
                    foreach (string value in values) box.Items.Add(new ComboBoxItem { Tag = value });
                    control = box;
                    string? choicesLanguage = null;
                    void RefreshChoices()
                    {
                        if (box.IsDropDownOpen) return;
                        if (choicesLanguage != UiText.Language)
                        {
                            foreach (ComboBoxItem item in box.Items)
                            {
                                string text = PreferenceChoiceText(field, (string)item.Tag);
                                item.Content = text;
                                // Existing WinUI item peers can retain the old inferred name.
                                AutomationProperties.SetName(item, text);
                            }
                            // WinUI caches the selected item's string separately from its Content.
                            // Re-select under the update guard so language refresh never writes settings.
                            box.SelectedIndex = -1;
                            choicesLanguage = UiText.Language;
                        }
                        box.SelectedIndex = Array.IndexOf(values, Value());
                    }
                    refresh.Add(RefreshChoices);
                    box.DropDownClosed += (_, _) =>
                    {
                        bool previous = _mediaControlsUpdating;
                        _mediaControlsUpdating = true;
                        try { RefreshChoices(); }
                        finally { _mediaControlsUpdating = previous; }
                    };
                    box.SelectionChanged += async (_, _) => { if (!Updating() && box.SelectedItem is ComboBoxItem { Tag: string value }) await Commit(value); };
                }
                else if (field.Minimum is double minimum)
                {
                    var box = new NumberBox { Header = heading, Minimum = minimum, Maximum = field.Maximum!.Value,
                        SmallChange = field.Key.Contains("Scale", StringComparison.Ordinal) ? .1 : 1, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
                    control = box;
                    refresh.Add(() =>
                    {
                        if (XamlRoot is not null && Within(FocusManager.GetFocusedElement(XamlRoot) as DependencyObject, box)) return;
                        // Persisted JSON can contain more precision than the input field.
                        // Normalize before calling WinUI, including the initial refresh.
                        double number = double.Parse(field.Validate(Value()), CultureInfo.InvariantCulture);
                        if (box.Value != number) box.Value = number;
                    });
                    box.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) e.Handled = true; };
                    box.ValueChanged += async (_, args) => { if (!Updating() && double.IsFinite(args.NewValue)) await Commit(args.NewValue.ToString(CultureInfo.InvariantCulture)); };
                }
                else
                {
                    var box = new TextBox { Header = heading, MaxLength = 4096 };
                    control = box;
                    refresh.Add(() => { if (XamlRoot is null || !Within(FocusManager.GetFocusedElement(XamlRoot) as DependencyObject, box)) box.Text = Value(); });
                    box.LostFocus += async (_, _) => { if (!Updating()) await Commit(box.Text); };
                    box.KeyDown += async (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) { e.Handled = true; await Commit(box.Text); } };
                }
                AutomationProperties.SetAutomationId(control, id);
                Action refreshControl = () => { heading.Text = UiText.Get(id); AutomationProperties.SetName(control, UiText.Get(id));
                    control.IsEnabled = (sessionControls ? QuickCurrent : _settingsAvailable && !_unsupportedMediaPreferences.Contains(field.Key)) &&
                        !(sessionControls && (field.Key is "PrimaryOverride" or "SecondaryOverride") && IsBitmapSubtitle(CurrentSubtitle(field.Key == "PrimaryOverride"))); };
                refresh.Add(refreshControl);
                if (field.Key is "PrimaryOverride" or "SecondaryOverride")
                {
                    control.Loaded += (_, _) => { _subtitleContextRefresh.Add(refreshControl); refreshControl(); };
                    control.Unloaded += (_, _) => _subtitleContextRefresh.Remove(refreshControl);
                }
                content.Children.Add(control);
            }
        }
        return panel;
    }
    private static string PreferenceChoiceText(MediaPreference field, string value)
    {
        if (field.Key == "SubtitleEncoding") return value == "auto" ? UiText.Get("M5Auto") : value;
        if (field.Key.EndsWith("Override", StringComparison.Ordinal)) return UiText.Get("M5Override" + value);
        if (field.Key == "SubtitleMatching") return UiText.Get("M5Matching" + value);
        return UiText.Get("M5Choice" + value.Replace("-", "", StringComparison.Ordinal));
    }
    private void RefreshMediaControls()
    {
        if (_closing) return;
        _mediaControlsUpdating = true;
        try
        {
            foreach (var refresh in _mediaControlRefresh) refresh();
            _trackOperationStatus.Text = _trackOperationStatusKey.Length == 0 ? "" : UiText.Get(_trackOperationStatusKey);
            RefreshAudioStatus();
        }
        finally { _mediaControlsUpdating = false; }
    }
    private void ObserveMediaChannelSettings(IReadOnlyDictionary<string, string> values)
    {
        if (!_mediaPreferencesTask.IsCompleted || _closing) return;
        bool changed = false;
        foreach (var field in MediaPreferences.SubtitleChannelFields)
            if (values.TryGetValue(field.Property, out string? value))
            {
                try
                {
                    value = field.Validate(value);
                    if (_subtitleChannelValues.GetValueOrDefault(field.Property) == value) continue;
                    _subtitleChannelValues[field.Property] = value; changed = true;
                }
                catch (ArgumentException) { }
            }
        // Engine readback belongs to this window. Never turn it into a shared preference edit.
        if (changed) RefreshMediaControls();
    }
    private async Task CommitSubtitleChannelAsync(MediaPreference field, string value)
    {
        if (!QuickCurrent || _session is not { } session || _quickInfo is not { } media) return;
        int revision = _quickRevision + 1;
        try
        {
            await SetQuickPropertyAsync(field.Property, field.Validate(value));
            var actual = await session.GetSubtitleChannelValuesAsync(media.RequestId, media.PlaylistEntryId);
            if (!_closing && revision == _quickRevision && _snapshot is { } current && QuickSettings.Matches(media, current))
                ObserveMediaChannelSettings(actual);
        }
        catch (OperationCanceledException) { }
        catch (ArgumentException) { ShowNotice("M5InvalidValue"); }
        catch (Exception error) { if (!_closing && _snapshot is { } current && QuickSettings.Matches(media, current)) ShowError(error, "M5SettingsError"); }
    }
    private async Task CommitAudioDeviceAsync(string name)
    {
        if (_closing || !_settingsAvailable || _session is not { } session) return;
        int revision = ++_audioDeviceSelectionRevision;
        try
        {
            var next = _settings.Media.With("AudioDevice", name);
            if (next != _settings.Media) { _settings = _settings with { Media = next }; SaveSettings(); }
            await ConfigureMediaPreferencesAsync();
            if (_closing || revision != _audioDeviceSelectionRevision || _settings.Media.AudioDevice != name) return;
            await session.SelectAudioDeviceAsync(name);
            _nextAudioRead = 0; await ObserveAudioOutputAsync();
        }
        catch (Exception error) { if (!_closing && revision == _audioDeviceSelectionRevision) ShowError(error, "M5SettingsError"); }
    }
    private async Task CommitMediaPreferenceAsync(MediaPreference field, string value)
    {
        if (_closing || !_settingsAvailable) return;
        try
        {
            var next = _settings.Media.With(field.Key, value);
            if (next == _settings.Media) return;
            _notice = "";
            _settings = _settings with { Media = next, VolumePercent = Math.Min(_settings.VolumePercent, next.VolumeLimit) };
            SaveSettings();
            await ConfigureMediaPreferencesAsync();
            if (field.Key == "SubtitleEncoding") await ReloadProductSubtitlesAsync();
        }
        catch (ArgumentException) { ShowNotice("M5InvalidValue"); }
        finally { if (!_closing) RefreshMediaControls(); }
    }
    private Task ConfigureMediaPreferencesAsync()
    {
        if (_mediaPreferencesTask.IsCompleted) _mediaPreferencesTask = ApplyMediaPreferencesLoopAsync();
        return _mediaPreferencesTask;
    }
    private async Task ApplyMediaPreferencesLoopAsync()
    {
        try
        {
            while (!_closing && _session is { } session && _mediaPreferencesApplied != _settings.Media)
            {
                var requested = _settings.Media;
                var result = await session.ApplyMediaPreferencesAsync(requested);
                if (_closing) return;
                _unsupportedMediaPreferences.Clear();
                _unsupportedMediaPreferences.UnionWith(result.Unsupported);
                _mediaPreferencesApplied = requested;
                MainVolume.Maximum = MusicVolume.Maximum = requested.VolumeLimit;
                _uiCache.InvalidateVolume();
                if (result.Unsupported.Count > 0) ShowNotice("M5Unsupported");
                Trace("media-preferences-applied", new { unsupported = result.Unsupported });
                _nextAudioRead = 0; _nextQuickRead = 0;
            }
        }
        catch (Exception error) { if (!_closing) ShowError(error, "M5SettingsError"); }
        finally { if (!_closing) { RefreshMediaControls(); _refreshInterfaceSettings?.Invoke(); } }
    }
    private void RefreshAudioDeviceBox(ComboBox box)
    {
        if (box.IsDropDownOpen || _audioOutput is null) return;
        var devices = _audioOutput.Devices.Select(d => d.Name == "auto" ? d with { Description = UiText.Get("M5DefaultDevice") } : d).ToList();
        string preferred = _settings.Media.AudioDevice;
        if (!devices.Any(d => d.Name == preferred)) devices.Add(new(preferred, UiText.Get("M5MissingDevice") + " " + preferred));
        string actual = _audioOutput.Selected;
        if (!devices.Any(d => d.Name == actual)) devices.Add(new(actual, UiText.Get("M5MissingDevice") + " " + actual));
        if (box.ItemsSource is not AudioDevice[] current || !current.SequenceEqual(devices)) box.ItemsSource = devices.ToArray();
        box.SelectedIndex = devices.FindIndex(d => d.Name == actual);
    }
    private void RefreshAudioStatus()
    {
        string message = _audioOutput is not { } output ? UiText.Get("Loading") :
            string.Format(CultureInfo.InvariantCulture, UiText.Get("M5AudioStatus"),
                output.Selected == "auto" ? UiText.Get("M5DefaultDevice") : output.Devices.FirstOrDefault(d => d.Name == output.Selected)?.Description ?? output.Selected,
                output.Driver.Length == 0 ? UiText.Get("M5NoOutput") : output.Driver,
                output.OutputFormat.Length == 0 ? UiText.Get("M5NoOutput") : output.OutputFormat) +
            (output.FellBackToDefault ? "\n" + UiText.Get("M5DeviceFallback") : "") +
            (output.RecoveryFailed ? "\n" + UiText.Get("M5DeviceRetry") : "") +
            (_settings.Media.Spdif.Length > 0 ? "\n" + UiText.Get(output.OutputFormat.Contains("spdif", StringComparison.OrdinalIgnoreCase) ? "M5PassthroughActive" : "M5PassthroughRequested") : "");
        foreach (var label in _audioStatusLabels) label.Text = message;
    }
    private async Task ObserveAudioOutputAsync()
    {
        if (_session is null || _closing || Environment.TickCount64 < _nextAudioRead) return;
        _nextAudioRead = Environment.TickCount64 + 1500;
        _audioOutput = await _session.GetAudioOutputAsync();
        if (_closing) return;
        if ((_dialogOpen || _sidebar.WantsVisible) && _snapshot is { Loading: false, Idle: false } media)
        {
            try
            {
                _subtitleContextInfo = await _session.GetMediaInfoAsync(media.RequestId, media.PlaylistEntryId);
                var channels = await _session.GetSubtitleChannelValuesAsync(media.RequestId, media.PlaylistEntryId);
                if (!_closing && _snapshot is { } current && QuickSettings.Matches(_subtitleContextInfo, current)) ObserveMediaChannelSettings(channels);
            }
            catch (OperationCanceledException) { _subtitleContextInfo = null; }
            catch (MediaUnavailableException) { _subtitleContextInfo = null; }
        }
        if (_closing) return;
        _mediaControlsUpdating = true;
        try { foreach (var box in _audioDeviceBoxes) RefreshAudioDeviceBox(box); RefreshAudioStatus(); foreach (var refresh in _subtitleContextRefresh) refresh(); }
        finally { _mediaControlsUpdating = false; }
    }
    private MediaTrack? CurrentSubtitle(bool primary)
    {
        if (_subtitleContextInfo is not { } info || _snapshot is not { } media || !QuickSettings.Matches(info, media)) return null;
        string selected = primary ? info.SubtitleTrack : info.SecondarySubtitleTrack;
        return info.Tracks.FirstOrDefault(t => t.Type == "sub" && t.Id.ToString(CultureInfo.InvariantCulture) == selected);
    }
    private static bool IsBitmapSubtitle(MediaTrack? track) => track?.Codec is "hdmv_pgs_subtitle" or "dvd_subtitle" or "dvb_subtitle" or "xsub";
    private string SubtitleStyleContext()
    {
        string Description(bool primary)
        {
            var track = CurrentSubtitle(primary);
            if (track is null) return UiText.Get("M5StyleNoTrack");
            if (IsBitmapSubtitle(track)) return UiText.Get("M5StyleBitmap");
            if (track.Codec is not ("ass" or "ssa")) return UiText.Get("M5StyleText");
            string mode = _subtitleChannelValues.GetValueOrDefault(primary ? "sub-ass-override" : "secondary-sub-ass-override", "scale");
            return UiText.Get(mode is "no" or "scale" ? "M5StylePreserved" : "M5StyleOverridden");
        }
        return string.Format(CultureInfo.InvariantCulture, UiText.Get("M5StyleContext"), Description(true), Description(false));
    }
    private async Task PickExternalTrackAsync(ExternalTrackKind kind)
    {
        if (_session is null || Owner is null || _snapshot is not { Loading: false, Idle: false } media || _pickerOpen || _dialogOpen || _closing) return;
        _pickerOpen = true; _subtitleManualRevision++; _matchingCancellation?.Cancel(); RefreshOscHolds();
        try
        {
            var picker = new FileOpenPicker(Owner.AppWindow.Id);
            if (kind == ExternalTrackKind.Audio) picker.FileTypeFilter.Add("*");
            else foreach (string extension in SubtitleMatcher.Extensions) picker.FileTypeFilter.Add(extension);
            var file = await picker.PickSingleFileAsync();
            if (file is null || _closing) return;
            var actual = await _session.LoadExternalTrackAsync(media.RequestId, media.PlaylistEntryId, file.Path, kind);
            if (_snapshot is not { } current || !QuickSettings.Matches(actual, media) || !QuickSettings.Matches(actual, current)) return;
            _notice = ""; ApplySurface(current);
            long loadedId = actual.Tracks.LastOrDefault(t => string.Equals(t.ExternalFilename?.Replace('/', '\\'), file.Path.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase))?.Id ?? 0;
            _trackOperationStatusKey = kind == ExternalTrackKind.PrimarySubtitle && QuickSettings.SubtitleConflict(actual, "sid", loadedId) ||
                kind == ExternalTrackKind.SecondarySubtitle && QuickSettings.SubtitleConflict(actual, "secondary-sid", loadedId)
                ? "M5TrackConflict" : "M5TrackLoaded";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (MediaOperationCurrent(media)) { _trackOperationStatusKey = "M5TrackFailed"; ShowError(error, "M5TrackFailed"); } }
        finally { _pickerOpen = false; _nextQuickRead = 0; RefreshMediaControls(); RefreshOscHolds(); WakeOsc(); }
    }
    private async Task ReloadProductSubtitlesAsync()
    {
        if (_session is null || _snapshot is not { Loading: false, Idle: false } media || _closing) return;
        _subtitleManualRevision++; _matchingCancellation?.Cancel();
        try { await _session.ReloadSubtitlesAsync(media.RequestId, media.PlaylistEntryId, _settings.Media.SubtitleEncoding); if (MediaOperationCurrent(media)) { _trackOperationStatusKey = "M5Reloaded"; _notice = ""; ApplySurface(_snapshot); } }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (MediaOperationCurrent(media)) { _trackOperationStatusKey = "M5ReloadFailed"; ShowError(error, "M5ReloadFailed"); } }
        finally { _nextQuickRead = 0; RefreshMediaControls(); }
    }
    private bool MediaOperationCurrent(PlaybackSnapshot media) => !_closing && _snapshot is { Loading: false } current &&
        current.RequestId == media.RequestId && current.PlaylistEntryId == media.PlaylistEntryId;
    private void ObserveSubtitleMatching(PlaybackSnapshot media)
    {
        if (media.Loading || media.Idle || _closing || _matchingRequest == media.RequestId && _matchingEntry == media.PlaylistEntryId) return;
        _matchingRequest = media.RequestId;
        _matchingEntry = media.PlaylistEntryId;
        _matchingCancellation?.Cancel();
        _matchingCancellation = new CancellationTokenSource();
        _trackOperationStatusKey = "";
        _matchingTask = MatchProductSubtitlesAsync(media, _matchingCancellation);
    }
    private async Task MatchProductSubtitlesAsync(PlaybackSnapshot media, CancellationTokenSource cancellation)
    {
        int manualRevision = _subtitleManualRevision;
        try
        {
            var paths = await SubtitleMatcher.FindAsync(media.Path, _settings.Media, cancellation.Token);
            foreach (string path in paths)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                if (_closing || _session is null || manualRevision != _subtitleManualRevision) return;
                try { await _session.LoadExternalTrackAsync(media.RequestId, media.PlaylistEntryId, path, ExternalTrackKind.PrimarySubtitle, cancellation.Token, select: false); }
                catch (IOException) { if (MediaOperationCurrent(media) && !cancellation.IsCancellationRequested) _trackOperationStatusKey = "M5MatchFailed"; }
                catch (InvalidOperationException) { if (MediaOperationCurrent(media) && !cancellation.IsCancellationRequested) _trackOperationStatusKey = "M5MatchFailed"; }
            }
            if (paths.Count > 0 && _session is { } session && manualRevision == _subtitleManualRevision)
                await session.SelectAutomaticSubtitleAsync(media.RequestId, media.PlaylistEntryId, cancellation.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!_closing) Trace("subtitle-match-failed", new { error = error.GetType().Name }); }
        finally { cancellation.Dispose(); if (ReferenceEquals(_matchingCancellation, cancellation)) _matchingCancellation = null; _nextQuickRead = 0; }
    }
}

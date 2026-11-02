// IINA 1.5.1/build 180 SidebarAudioPane profile actions, with WinUI controls.
// Copyright (c) 2024 lhc. GPL-3.0; licenses/IINA-GPL-3.0.txt.
using Mani.Playback;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Mani_App;

public sealed partial class MainPage
{
    private StackPanel BuildEqualizerProfiles(List<Action> refresh)
    {
        var panel = new StackPanel { Spacing = 8 };
        var profiles = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, MaxDropDownHeight = 280 };
        var name = new TextBox { MaxLength = EqualizerProfiles.MaximumNameLength };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        string? errorKey = null;
        AutomationProperties.SetAutomationId(status, "M5EqStatus");
        AutomationProperties.SetLiveSetting(status, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        name.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) e.Handled = true; };
        AutomationProperties.SetAutomationId(profiles, "M5EqProfile");
        AutomationProperties.SetAutomationId(name, "M5EqName");
        panel.Children.Add(profiles); panel.Children.Add(name); panel.Children.Add(status);
        string? selectedUser = null;
        object? preferredProfile = null;
        bool updating = false;
        void Refresh()
        {
            updating = true;
            try
            {
                profiles.Header = UiText.Get("M5EqProfile"); name.Header = UiText.Get("M5EqName");
                status.Text = errorKey is null ? string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    UiText.Get("M5EqCount"), EqualizerProfiles.Read(_settings.Media.EqualizerProfiles).Count, EqualizerProfiles.MaximumProfiles) : UiText.Get(errorKey);
                if (profiles.IsDropDownOpen) return;
                ComboBoxItem? preferred = null;
                profiles.Items.Clear(); profiles.Items.Add(new ComboBoxItem { Content = UiText.Get("M5EqCustom") });
                profiles.SelectedIndex = 0;
                foreach (var preset in EqualizerProfiles.Presets)
                {
                    var item = new ComboBoxItem { Content = UiText.Get("M5Preset" + preset.Key), Tag = preset };
                    profiles.Items.Add(item);
                    if (EqualizerProfiles.Matches(_settings.Media, preset.Gains))
                    { profiles.SelectedItem = item; if (Equals(preferredProfile, preset)) preferred = item; }
                }
                foreach (var profile in EqualizerProfiles.Read(_settings.Media.EqualizerProfiles))
                {
                    var item = new ComboBoxItem { Content = profile.Key, Tag = profile.Key };
                    profiles.Items.Add(item);
                    if (EqualizerProfiles.Matches(_settings.Media, profile.Value))
                    { profiles.SelectedItem = item; if (Equals(preferredProfile, profile.Key)) preferred = item; }
                }
                if (preferred is not null) profiles.SelectedItem = preferred;
                selectedUser = (profiles.SelectedItem as ComboBoxItem)?.Tag as string;
                preferredProfile = (profiles.SelectedItem as ComboBoxItem)?.Tag;
            }
            finally { updating = false; }
        }
        refresh.Add(Refresh);
        profiles.SelectionChanged += async (_, _) =>
        {
            if (updating || _mediaControlsUpdating || _updatingInterfaceSettings || _closing || !_settingsAvailable) return;
            var tag = (profiles.SelectedItem as ComboBoxItem)?.Tag;
            var gains = tag is EqualizerPreset preset ? preset.Gains : tag is string key ? EqualizerProfiles.Read(_settings.Media.EqualizerProfiles).GetValueOrDefault(key) : null;
            if (gains is null) return;
            selectedUser = tag as string;
            preferredProfile = tag;
            _settings = _settings with { Media = EqualizerProfiles.Apply(_settings.Media, gains) };
            SaveSettings(); await ConfigureMediaPreferencesAsync();
        };
        void Action(string key, Func<string, string> change)
        {
            var button = new Button { HorizontalAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetAutomationId(button, key); panel.Children.Add(button);
            refresh.Add(() => { button.Content = UiText.Get(key); button.IsEnabled = _settingsAvailable && (key == "M5EqSave" || selectedUser is not null); });
            button.Click += (_, _) =>
            {
                try
                {
                    string value = change(_settings.Media.EqualizerProfiles);
                    _notice = ""; errorKey = null;
                    _settings = _settings with { Media = _settings.Media.With("EqualizerProfiles", value) };
                    SaveSettings(); RefreshMediaControls(); _refreshInterfaceSettings?.Invoke();
                }
                catch (EqualizerProfileException error)
                {
                    errorKey = error.Reason switch { EqualizerProfileError.Count => "M5EqLimit", EqualizerProfileError.Size => "M5EqCapacity", EqualizerProfileError.Data => "M5EqData", _ => "M5EqInvalid" };
                    status.Text = UiText.Get(errorKey);
                }
                catch (ArgumentException) { errorKey = "M5EqInvalid"; status.Text = UiText.Get(errorKey); }
            };
        }
        string NewName(string json)
        {
            string value = name.Text.Trim();
            if (string.IsNullOrWhiteSpace(value) || EqualizerProfiles.Read(json).ContainsKey(value) ||
                EqualizerProfiles.Presets.Any(p => UiText.Get("M5Preset" + p.Key).Equals(value, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("Duplicate or empty name.");
            return value;
        }
        Action("M5EqSave", json =>
        {
            string next = NewName(json);
            string saved = EqualizerProfiles.Save(json, next, EqualizerProfiles.Gains(_settings.Media));
            preferredProfile = next; return saved;
        });
        Action("M5EqRename", json =>
        {
            string next = NewName(json), old = selectedUser ?? throw new ArgumentException();
            var gains = EqualizerProfiles.Read(json)[old];
            string saved = EqualizerProfiles.Save(EqualizerProfiles.Remove(json, old), next, gains);
            preferredProfile = next; return saved;
        });
        Action("M5EqDelete", json => EqualizerProfiles.Remove(json, selectedUser ?? throw new ArgumentException()));
        return panel;
    }
}

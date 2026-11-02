// IINA SettingsWindow / SettingsPageGeneral / SettingsPageUI grouped preferences.
// Windows reuses the existing owned dialog and immediate per-key settings store.
// Copyright (c) 2016-2026 lhc and contributors. GPL-3.0; licenses/IINA-GPL-3.0.txt.
using Mani.Playback;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.Globalization.NumberFormatting;

namespace Mani_App;

public sealed partial class MainPage
{
    private ContentDialog? _interfaceDialog;
    private Action? _refreshInterfaceSettings;
    private bool _updatingInterfaceSettings;
    private async void InterfaceSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_closing || _dialogOpen || _pickerOpen || IsPictureInPicture) return;
        // The compact music window cannot fit the full audio settings dialog.
        // Reuse its existing return-to-player transition; playback stays intact.
        if (IsMusicMode) ReturnFromMusicMode();
        _dialogOpen = true; MainMenu.Hide(); RefreshOscHolds(); UpdateOsc();
        try
        {
            var pages = new[] { new StackPanel { Spacing = 12 }, new StackPanel { Spacing = 12 }, new StackPanel { Spacing = 12 }, new StackPanel { Spacing = 12 }, new StackPanel { Spacing = 12 } };
            StackPanel page = pages[0], content = page;
            var refresh = new List<Action>();
            void Commit(Func<PlayerSettings, PlayerSettings> edit)
            {
                if (_updatingInterfaceSettings || _closing) return;
                var before = _settings;
                _settings = edit(_settings);
                if (_settings == before) return;
                SaveSettings();
                if (_settings.AlwaysFloatOnTop != before.AlwaysFloatOnTop)
                    Owner?.SetAutomaticFloating(_settings.AlwaysFloatOnTop);
                if (_settings.UsePhysicalResolution != before.UsePhysicalResolution ||
                    _settings.ResizeWindowTiming != before.ResizeWindowTiming || _settings.ResizeWindowOption != before.ResizeWindowOption)
                    ConfigureWindowSizing();
                UpdateActionMenu(); ApplyInterfacePreferences();
                _ = ConfigureResumeAsync();
                Trace("settings-preferences", new { _settings.PauseWhenOpen, _settings.AutoSwitchToMusicMode,
                    _settings.AlwaysFloatOnTop, _settings.UsePhysicalResolution, _settings.ResizeWindowTiming, _settings.ResizeWindowOption });
            }
            void Heading(string key)
            {
                content = new StackPanel { Spacing = 10 };
                var title = new TextBlock { Text = UiText.Get(key), FontSize = 16, TextWrapping = TextWrapping.Wrap };
                content.Children.Add(title);
                refresh.Add(() => title.Text = UiText.Get(key));
                page.Children.Add(new Border { Style = (Style)Application.Current.Resources["GlassGroup"], Child = content });
            }
            void Toggle(string id, Func<PlayerSettings, bool> get, Func<PlayerSettings, bool, PlayerSettings> set, Func<bool>? enabled = null)
            {
                var box = new CheckBox { Content = new TextBlock { Text = UiText.Get(id), TextWrapping = TextWrapping.Wrap }, HorizontalContentAlignment = HorizontalAlignment.Stretch };
                AutomationProperties.SetAutomationId(box, id);
                AutomationProperties.SetName(box, UiText.Get(id));
                content.Children.Add(box);
                refresh.Add(() => { ((TextBlock)box.Content).Text = UiText.Get(id); AutomationProperties.SetName(box, UiText.Get(id)); box.IsChecked = get(_settings); box.IsEnabled = enabled?.Invoke() ?? true; });
                box.Checked += (_, _) => Commit(s => set(s, true));
                box.Unchecked += (_, _) => Commit(s => set(s, false));
            }
            void Number(string id, Func<PlayerSettings, double> get, Func<PlayerSettings, double, PlayerSettings> set,
                double minimum, double maximum, Func<bool> enabled)
            {
                var box = new NumberBox { Header = new TextBlock { Text = UiText.Get(id), TextWrapping = TextWrapping.Wrap }, Minimum = minimum, Maximum = maximum,
                    SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, SmallChange = id == "OsdTextSize" ? 1 : .5,
                    NumberFormatter = new DecimalFormatter { FractionDigits = 3, IntegerDigits = 1 } };
                AutomationProperties.SetAutomationId(box, id);
                AutomationProperties.SetName(box, UiText.Get(id));
                content.Children.Add(box);
                refresh.Add(() => { ((TextBlock)box.Header).Text = UiText.Get(id); AutomationProperties.SetName(box, UiText.Get(id)); box.Value = get(_settings); box.IsEnabled = enabled(); });
                box.ValueChanged += (_, change) => { if (double.IsFinite(change.NewValue)) Commit(s => set(s, change.NewValue)); };
            }
            void Choice(string id, string[] labels, Func<PlayerSettings, int> get, Func<PlayerSettings, int, PlayerSettings> set,
                Func<bool>? enabled = null)
            {
                var box = new ComboBox { Header = id == "Language" ? null : new TextBlock { Text = UiText.Get(id), TextWrapping = TextWrapping.Wrap }, HorizontalAlignment = HorizontalAlignment.Stretch };
                AutomationProperties.SetAutomationId(box, id);
                AutomationProperties.SetName(box, UiText.Get(id));
                foreach (var label in labels) box.Items.Add(new ComboBoxItem { Content = UiText.Get(label) });
                content.Children.Add(box);
                refresh.Add(() => { if (box.Header is TextBlock heading) heading.Text = UiText.Get(id); AutomationProperties.SetName(box, UiText.Get(id));
                    bool relocalize = box.SelectedIndex >= 0 && !Equals(((ComboBoxItem)box.Items[box.SelectedIndex]).Content, UiText.Get(labels[box.SelectedIndex]));
                    for (int i = 0; i < labels.Length; i++) ((ComboBoxItem)box.Items[i]).Content = UiText.Get(labels[i]);
                    if (relocalize) box.SelectedIndex = -1;
                    box.SelectedIndex = get(_settings); box.IsEnabled = enabled?.Invoke() ?? true; });
                box.SelectionChanged += (_, _) => { if (box.SelectedIndex >= 0) Commit(s => set(s, box.SelectedIndex)); };
            }
            Heading("Language");
            Choice("Language", ["LanguageEnglish", "LanguageChinese"], s => s.Language == InterfaceLanguage.Chinese ? 1 : 0,
                (s, v) => s with { Language = v == 1 ? InterfaceLanguage.Chinese : InterfaceLanguage.English });
            Heading("SettingsOpening");
            Toggle("PauseWhenOpen", s => s.PauseWhenOpen, (s, v) => s with { PauseWhenOpen = v });
            Toggle("AutomaticMusic", s => s.AutoSwitchToMusicMode, (s, v) => s with { AutoSwitchToMusicMode = v });
            Heading("History");
            Toggle("RecordHistory", s => s.RecordHistory, (s, v) => s with { RecordHistory = v });
            Toggle("ResumePosition", s => s.ResumePosition, (s, v) => s with { ResumePosition = v });
            page = pages[1];
            Heading("SettingsWindowBehavior");
            Toggle("FloatWhilePlaying", s => s.AlwaysFloatOnTop, (s, v) => s with { AlwaysFloatOnTop = v });
            Heading("SettingsWindowSize");
            Toggle("PhysicalResolution", s => s.UsePhysicalResolution, (s, v) => s with { UsePhysicalResolution = v });
            Choice("ResizeTiming", ["ResizeAlways", "ResizeWhenOpening", "ResizeNever"], s => (int)s.ResizeWindowTiming,
                (s, v) => s with { ResizeWindowTiming = (ResizeWindowTiming)v });
            Choice("OpeningSize", ["SizeFitScreen", "SizeHalf", "SizeNormal", "SizeOneAndHalf", "SizeDouble"], s => (int)s.ResizeWindowOption,
                (s, v) => s with { ResizeWindowOption = (ResizeWindowOption)v }, () => _settings.ResizeWindowTiming != ResizeWindowTiming.Never);
            page = pages[2];
            Heading("ThumbnailPreview");
            Toggle("EnableThumbnailPreview", s => s.EnableThumbnailPreview, (s, v) => s with { EnableThumbnailPreview = v });
            var cacheSize = new TextBlock { TextWrapping = TextWrapping.Wrap }; AutomationProperties.SetAutomationId(cacheSize, "ThumbnailCacheSize"); content.Children.Add(cacheSize);
            var clearCache = new Button { HorizontalContentAlignment = HorizontalAlignment.Stretch }; AutomationProperties.SetAutomationId(clearCache, "ClearThumbnailCache"); content.Children.Add(clearCache);
            bool confirmCache = false;
            long? cacheBytes = null;
            void RefreshCacheText()
            {
                cacheSize.Text = cacheBytes is { } bytes ? string.Format(UiText.Get("ThumbnailCacheSize"), bytes / 1048576d) : UiText.Get("Loading");
                clearCache.Content = new TextBlock { Text = UiText.Get(confirmCache ? "ConfirmClearThumbnailCache" : "ClearThumbnailCache"), TextWrapping = TextWrapping.Wrap };
                AutomationProperties.SetName(clearCache, UiText.Get(confirmCache ? "ConfirmClearThumbnailCache" : "ClearThumbnailCache"));
            }
            refresh.Add(RefreshCacheText);
            async Task ReadCacheSize()
            {
                try { if (_thumbnailCache is not null) { cacheBytes = await _thumbnailCache.SizeAsync(); if (!_closing) RefreshCacheText(); } }
                catch (Exception error) when (SharedPlayerSettings.IsStorageError(error)) { ShowError(error); }
            }
            clearCache.Click += async (_, _) =>
            {
                if (!confirmCache) { confirmCache = true; RefreshCacheText(); return; }
                clearCache.IsEnabled = false; CancelTimelinePreview();
                try { if (_thumbnailCache is not null) await _thumbnailCache.ClearAsync(); confirmCache = false; await ReadCacheSize(); }
                catch (Exception error) when (SharedPlayerSettings.IsStorageError(error)) { ShowError(error); }
                finally { clearCache.IsEnabled = true; }
            };
            _ = ReadCacheSize();
            Heading("InterfaceControlBar");
            Toggle("ShowChapterPositions", s => s.ShowChapterPositions, (s, v) => s with { ShowChapterPositions = v });
            Toggle("ControlAutoHide", s => s.EnableControlBarAutoHide, (s, v) => s with { EnableControlBarAutoHide = v });
            Number("ControlHideDelay", s => s.ControlBarAutoHideTimeout, (s, v) => s with { ControlBarAutoHideTimeout = v },
                0, InterfacePreferenceLimits.MaximumSeconds, () => _settings.EnableControlBarAutoHide);
            Heading("InterfaceOsd");
            Toggle("OsdEnabled", s => s.EnableOsd, (s, v) => s with { EnableOsd = v });
            Number("OsdHideDelay", s => s.OsdAutoHideTimeout, (s, v) => s with { OsdAutoHideTimeout = v },
                0, InterfacePreferenceLimits.MaximumSeconds, () => _settings.EnableOsd);
            Number("OsdTextSize", s => s.OsdTextSize, (s, v) => s with { OsdTextSize = v },
                5, InterfacePreferenceLimits.MaximumTextSize, () => _settings.EnableOsd);
            Heading("OsdSuppress");
            Toggle("OsdSuppressFile", s => s.DisableOsdFileStart, (s, v) => s with { DisableOsdFileStart = v }, () => _settings.EnableOsd);
            Toggle("OsdSuppressPause", s => s.DisableOsdPauseResume, (s, v) => s with { DisableOsdPauseResume = v }, () => _settings.EnableOsd);
            Toggle("OsdSuppressSeek", s => s.DisableOsdSeek, (s, v) => s with { DisableOsdSeek = v }, () => _settings.EnableOsd);
            Toggle("OsdSuppressSpeed", s => s.DisableOsdSpeed, (s, v) => s with { DisableOsdSpeed = v }, () => _settings.EnableOsd);
            pages[3].Children.Add(BuildMediaPreferencePanel(true, refresh));
            pages[4].Children.Add(BuildMediaPreferencePanel(false, refresh));
            _refreshInterfaceSettings = () =>
            {
                _updatingInterfaceSettings = true;
                try { foreach (var update in refresh) update(); }
                finally { _updatingInterfaceSettings = false; }
            };
            _refreshInterfaceSettings();
            var tabs = new ListBox { Style = (Style)Application.Current.Resources["GlassSegments"],
                ItemContainerStyle = (Style)Application.Current.Resources["GlassSegment"], HorizontalAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetAutomationId(tabs, "SettingsPage");
            AutomationProperties.SetName(tabs, UiText.Get("SettingsCategory"));
            var compact = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, Visibility = Visibility.Collapsed };
            AutomationProperties.SetAutomationId(compact, "SettingsPageCompact");
            AutomationProperties.SetName(compact, UiText.Get("SettingsCategory"));
            foreach (var key in new[] { "SettingsGeneral", "SettingsWindow", "SettingsInterface", "M5Audio", "M5Subtitles" })
            {
                tabs.Items.Add(new ListBoxItem { Content = UiText.Get(key) });
                compact.Items.Add(new ComboBoxItem { Content = UiText.Get(key) });
                int index = compact.Items.Count - 1;
                refresh.Add(() =>
                {
                    string label = UiText.Get(key);
                    bool relocalize = compact.SelectedIndex == index && !Equals(((ComboBoxItem)compact.Items[index]).Content, label);
                    ((ListBoxItem)tabs.Items[index]).Content = label; ((ComboBoxItem)compact.Items[index]).Content = label;
                    if (relocalize) { compact.SelectedIndex = -1; compact.SelectedIndex = index; }
                });
            }
            compact.SelectionChanged += (_, _) => { if (compact.SelectedIndex >= 0) tabs.SelectedIndex = compact.SelectedIndex; };
            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var xamlRoot = XamlRoot;
            var root = new Grid { MinWidth = 260, RowSpacing = 12 };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var navigation = new Grid(); navigation.Children.Add(tabs); navigation.Children.Add(compact);
            var wholePage = new StackPanel { Spacing = 12 };
            wholePage.Children.Add(new TextBlock { Text = UiText.Get("SettingsTitle"), FontSize = 20,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            bool scrollWholePage = false;
            void ShowPage()
            {
                if (scrollWholePage)
                {
                    if (!ReferenceEquals(scroll.Content, wholePage))
                    {
                        scroll.Content = null; root.Children.Remove(navigation);
                        wholePage.Children.Add(navigation); scroll.Content = wholePage;
                    }
                    if (wholePage.Children.Count > 2) wholePage.Children.RemoveAt(2);
                    wholePage.Children.Add(pages[tabs.SelectedIndex]);
                }
                else
                {
                    if (ReferenceEquals(scroll.Content, wholePage))
                    {
                        scroll.Content = null;
                        while (wholePage.Children.Count > 1) wholePage.Children.RemoveAt(1);
                    }
                    if (!root.Children.Contains(navigation)) root.Children.Add(navigation);
                    scroll.Content = pages[tabs.SelectedIndex];
                }
                scroll.ChangeView(null, 0, null);
            }
            tabs.SelectionChanged += (_, _) =>
            {
                if (tabs.SelectedIndex < 0) return;
                compact.SelectedIndex = tabs.SelectedIndex;
                ShowPage();
                Trace("settings-page", new { page = tabs.SelectedIndex });
            };
            tabs.SelectedIndex = 0;
            void Arrange()
            {
                bool useCompact = _systemUi.TextScaleFactor > 1.5 || xamlRoot.Size.Width < 900;
                tabs.Visibility = useCompact ? Visibility.Collapsed : Visibility.Visible;
                compact.Visibility = useCompact ? Visibility.Visible : Visibility.Collapsed;
                // A short player cannot hold fixed navigation plus a useful page
                // viewport. Scroll the title, navigation and page together in
                // that case, retaining ContentDialog's fixed Done button.
                bool previousWholePage = scrollWholePage;
                scrollWholePage = xamlRoot.Size.Height < 400;
                root.MaxHeight = Math.Max(80, xamlRoot.Size.Height - (scrollWholePage ? 120 : 220));
                if (previousWholePage != scrollWholePage)
                {
                    if (_interfaceDialog is not null) _interfaceDialog.Title = scrollWholePage ? null : UiText.Get("SettingsTitle");
                    ShowPage();
                }
            }
            void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => Arrange();
            root.Loaded += (_, _) => Arrange(); root.SizeChanged += (_, _) => Arrange();
            Grid.SetRow(scroll, 1); root.Children.Add(scroll);
            _interfaceDialog = new ContentDialog { XamlRoot = XamlRoot, Title = UiText.Get("SettingsTitle"),
                CloseButtonText = UiText.Get("Done"), DefaultButton = ContentDialogButton.Close,
                Content = root };
            AutomationProperties.SetAutomationId(_interfaceDialog, "SettingsDialog");
            AutomationProperties.SetName(_interfaceDialog, UiText.Get("SettingsTitle"));
            refresh.Add(() => {
                _interfaceDialog.Title = scrollWholePage ? null : UiText.Get("SettingsTitle");
                _interfaceDialog.CloseButtonText = UiText.Get("Done");
                AutomationProperties.SetName(_interfaceDialog, UiText.Get("SettingsTitle"));
                AutomationProperties.SetName(tabs, UiText.Get("SettingsCategory"));
                AutomationProperties.SetName(compact, UiText.Get("SettingsCategory"));
                ((TextBlock)wholePage.Children[0]).Text = UiText.Get("SettingsTitle");
                Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.FromElement(_interfaceDialog)?.InvalidatePeer();
            });
            xamlRoot.Changed += RootChanged;
            try { await _interfaceDialog.ShowAsync(); }
            finally { xamlRoot.Changed -= RootChanged; }
        }
        catch (Exception error) { if (!_closing) ShowError(error); }
        finally
        {
            _interfaceDialog = null; _refreshInterfaceSettings = null; _dialogOpen = false;
            if (!_closing) { RefreshOscHolds(); WakeOsc(); RestorePlayerMenuFocus(); }
        }
    }
    private void ApplyInterfacePreferences()
    {
        _refreshInterfaceSettings?.Invoke();
        UpdateChapterMarkers();
        if (!_settings.EnableThumbnailPreview) CancelTimelinePreview();
        UpdateOsdView(); RefreshOscHolds(); UpdateOsc();
        Trace("interface-preferences", new { _settings.EnableControlBarAutoHide, _settings.ControlBarAutoHideTimeout,
            _settings.EnableOsd, _settings.DisableOsdFileStart, _settings.DisableOsdPauseResume, _settings.DisableOsdSeek,
            _settings.DisableOsdSpeed, _settings.OsdAutoHideTimeout, _settings.OsdTextSize });
    }
}

using Mani_App.Diagnostics;

namespace Mani_App;

public sealed partial class MainPage
{
    private bool StartFontVerificationIfRequested()
    {
#if MANI_DIAGNOSTICS && MANI_ISOLATED_TEST
        string? directory = Environment.GetEnvironmentVariable("MANI_FONT_VERIFY");
        string? source = Environment.GetEnvironmentVariable("MANI_FONT_SOURCE");
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(source)) return false;
        _ = RunProbeAsync(async token =>
        {
            Directory.CreateDirectory(directory);
            try
            {
                _settingsAvailable = false;
                // The harness selects its own tracks; avoid the normal auto-match
                // operation racing those selections when a new fixture opens.
                _settings = _settings with { Media = _settings.Media with { SubtitleMatching = "no" } };
                _matchingCancellation?.Cancel();
                await _matchingTask;
                await SubtitleFontProbe.RunAsync(_session!, source, directory, async () =>
                {
                    await _pollTask;
                    if (_bridge?.HasAttachedSwapChain != true || VideoPanel.ActualWidth <= 0)
                        throw new InvalidOperationException("Video panel is not attached.");
                    return new { panelAttached = true, width = VideoPanel.ActualWidth * VideoPanel.CompositionScaleX,
                        height = VideoPanel.ActualHeight * VideoPanel.CompositionScaleY, display = ReadSwitchDisplay() };
                }, token);
                await VerifyFontSettingsUiAsync(directory, token);
            }
            catch (Exception error) { await File.WriteAllTextAsync(Path.Combine(directory, "failure.txt"), error.ToString()); }
            finally
            {
                if (!_closing && Owner is not null)
                    PostMessageW(WinRT.Interop.WindowNative.GetWindowHandle(Owner), 0x0010, 0, 0);
            }
        });
        return true;
#else
        return false;
#endif
    }

#if MANI_DIAGNOSTICS && MANI_ISOLATED_TEST
    private async Task VerifyFontSettingsUiAsync(string directory, CancellationToken token)
    {
        // Exercise the real settings panel in the isolated app, without OS input automation.
        _settingsAvailable = true;
        var field = Mani.Playback.MediaPreferences.Fields.Single(f => f.Key == "Font");
        await CommitMediaPreferenceAsync(field, "Consolas");
        await CommitMediaPreferenceAsync(field, "sans-serif");
        InterfaceSettings_Click(this, new Microsoft.UI.Xaml.RoutedEventArgs());
        for (int i = 0; _interfaceDialog?.Content is null && i < 100; i++) await Task.Delay(50, token);
        if (_interfaceDialog is null) throw new InvalidOperationException("Settings dialog did not open.");
        static IEnumerable<Microsoft.UI.Xaml.DependencyObject> Children(Microsoft.UI.Xaml.DependencyObject root)
        {
            yield return root;
            for (int i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root); i++)
                foreach (var child in Children(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i))) yield return child;
        }
        await Task.Delay(300, token);
        var tabs = Children(_interfaceDialog).OfType<Microsoft.UI.Xaml.Controls.ListBox>()
            .Single(c => Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(c) == "SettingsPage");
        tabs.SelectedIndex = 4;
        await Task.Delay(200, token);
        foreach (var expander in Children(_interfaceDialog).OfType<Microsoft.UI.Xaml.Controls.Expander>().ToArray()) expander.IsExpanded = true;
        await Task.Delay(200, token);
        var box = Children(_interfaceDialog).OfType<Microsoft.UI.Xaml.Controls.TextBox>()
            .Single(c => Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(c) == "M5Font");
        if (box.Text != "sans-serif") throw new InvalidOperationException("Resolved family replaced the saved default in UI.");
        var reports = new List<object>();
        foreach (string language in new[] { "en-US", "zh-CN" })
        {
            _settings = _settings with { Language = language };
            UiText.SetLanguage(language); ApplyLanguage(); _refreshInterfaceSettings?.Invoke();
            await Task.Delay(200, token);
            if (box.Text != "sans-serif" || _settings.Media.Font != "sans-serif") throw new InvalidOperationException("Language changed font preference.");
            reports.Add(new { language, value = box.Text, accessibleName = Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(box),
                hint = UiText.Get("M5HintSubtitleStyle"), dialogWidth = _interfaceDialog.ActualWidth, dialogHeight = _interfaceDialog.ActualHeight });
        }
        _interfaceDialog.Hide();
        await File.WriteAllTextAsync(Path.Combine(directory, "ui.json"), System.Text.Json.JsonSerializer.Serialize(reports,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), token);
    }
#endif
}

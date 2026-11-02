using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Mani_App;

public sealed partial class MainPage
{
    private bool _aboutOpen;

    private async void About_Click(object sender, RoutedEventArgs e)
    {
        if (_closing || _aboutOpen || XamlRoot is null) return;
        _aboutOpen = true; _dialogOpen = true; RefreshOscHolds();
        try
        {
            // Keep the license and credits available offline, as in IINA's About window.
            string notice = await File.ReadAllTextAsync(
                Path.Combine(AppContext.BaseDirectory, "licenses", "Mani-NOTICE.txt"));
            string license = await File.ReadAllTextAsync(
                Path.Combine(AppContext.BaseDirectory, "LICENSE"));
            string credits = await File.ReadAllTextAsync(
                Path.Combine(AppContext.BaseDirectory, "licenses", "IINA-NOTICE.txt"));
            if (_closing) return;

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                RequestedTheme = RequestedTheme,
                Title = UiText.Get("About"),
                CloseButtonText = UiText.Get("Close"),
                DefaultButton = ContentDialogButton.Close,
                Content = new ScrollViewer
                {
                    MaxHeight = 420,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    Content = new TextBlock
                    {
                        Text = $"{notice}\n\nGNU General Public License\n\n{license}\n\nIINA acknowledgments\n\n{credits}",
                        TextWrapping = TextWrapping.Wrap,
                        IsTextSelectionEnabled = true
                    }
                }
            };
            _refreshOtherDialog = () => { dialog.Title = UiText.Get("About"); dialog.CloseButtonText = UiText.Get("Close"); };
            await dialog.ShowAsync();
        }
        catch (Exception error)
        {
            ShowError(error, "LicenseError");
        }
        finally
        {
            _refreshOtherDialog = null; _aboutOpen = false; _dialogOpen = false; RefreshOscHolds(); WakeOsc(); RestorePlayerMenuFocus();
        }
    }
}

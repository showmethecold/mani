using Microsoft.UI.Xaml;

namespace Mani_App;

public sealed partial class MainPage
{
    private string? _appliedLanguage;
    private Action? _refreshOtherDialog;
    private void ApplyLanguage()
    {
        if (_appliedLanguage == UiText.Language || _closing) return;
        _appliedLanguage = UiText.Language;
        Language = UiText.Language;
        RefreshLocalizedControls();
        InfoHeading.Text = UiText.Get("Static156");
        OpenLogsButton.Content = UiText.Get("OpenLogFolder");
        foreach (var refresh in _navigationText) refresh();
        _navigationSignature = "";
        _uiCache.Invalidate();
        _nextQuickRead = 0;
        _refreshInterfaceSettings?.Invoke();
        _refreshHistory?.Invoke();
        _refreshOtherDialog?.Invoke();
        RefreshMediaControls();
        SetText(Status, _notice);
        ApplySurface(_snapshot);
        if (_snapshot is { } snapshot)
        {
            SetPlayAppearance(snapshot.Paused ? "Play" : "Pause");
            UpdateObservedControls(snapshot);
            UpdatePlaybackInfo(snapshot, force: true);
        }
        UpdateOsdView();
    }
}

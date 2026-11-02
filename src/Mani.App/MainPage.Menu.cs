// IINA PlayerWindowController.rightMouseUp/performMouseAction default pause action.
// Copyright (c) 2016-2026 lhc and contributors. GPL-3.0; licenses/IINA-GPL-3.0.txt.
// Windows keyboard context menus reuse the same flyout and existing commands.
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;

namespace Mani_App;

public sealed partial class MainPage
{
    private async void Video_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (_closing || _dialogOpen || _pickerOpen || _menuOpen || !_surface.HasMedia) return;
        e.Handled = true;
        VideoFocus.Focus(FocusState.Pointer);
        WakeOsc();
        await TogglePauseAsync("secondary-click");
    }
    private void Video_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        // Pointer-origin context requests are the IINA pause action above.
        // Windows raises a request without a point for the dedicated Menu key.
        if (e.TryGetPosition(VideoFocus, out _) || _closing || _dialogOpen || _pickerOpen) return;
        e.Handled = true;
        ShowVideoMenu();
    }
    private void Video_MenuShortcut(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        if (_closing || _dialogOpen || _pickerOpen || _menuOpen ||
            !ReferenceEquals(FocusManager.GetFocusedElement(XamlRoot), VideoFocus)) return;
        e.Handled = true;
        ShowVideoMenu();
    }
    private void ShowVideoMenu()
    {
        if (_menuOpen || !_surface.HasMedia) return;
        WakeOsc();
        MainMenu.ShowAt(VideoFocus, new FlyoutShowOptions { Position = new(VideoFocus.ActualWidth / 2, VideoFocus.ActualHeight / 2) });
    }
    private void UpdateActionMenu()
    {
        UpdateWindowSizeMenu();
        RefreshWindowFloating();
        RefreshPictureInPictureMenu();
        RefreshMusicMenu();
        RefreshLayoutSettings();
        bool ready = _session is not null && !_sessionUnavailable && !_closing && !_m1Busy;
        InterfaceMenu.IsEnabled = ready && _settingsAvailable && !IsPictureInPicture;
        LayoutMenu.IsEnabled = ready && _surface.HasMedia && !IsPictureInPicture && !IsMusicMode;
        QuickSettingsMenu.IsEnabled = AudioSettingsMenu.IsEnabled = SubtitlesSettingsMenu.IsEnabled = LayoutMenu.IsEnabled;
        bool media = ready && _surface.HasMedia;
        PlayPauseMenu.IsEnabled = media;
        PlayPauseMenu.Text = UiText.Get(_snapshot?.Paused == true ? "Play" : "Pause");
        SeekBackMenu.IsEnabled = SeekForwardMenu.IsEnabled = media && _snapshot is { Duration: > 0 };
        SpeedMenu.IsEnabled = media;
        FrameForwardMenu.IsEnabled = FrameBackMenu.IsEnabled = AbLoopMenu.IsEnabled = ScreenshotMenu.IsEnabled = media;
        FileLoopMenu.IsEnabled = LoopOffMenu.IsEnabled = media;
        FileLoopMenu.IsChecked = _snapshot is { } loopState && LoopFeedback.Enabled(loopState.FileLoop);
        PlaybackInfoMenu.IsEnabled = media && !IsPictureInPicture && !IsMusicMode;
        StopMenu.IsEnabled = ready && (_surface.HasMedia || _uiOpening || _snapshot is { Loading: true });
        OpenFileMenu.IsEnabled = OpenUrlMenu.IsEnabled = ready && !_pickerOpen && !_dialogOpen;
        RevealFileMenu.IsEnabled = !_fileRevealPending && !_pickerOpen && !_dialogOpen && CurrentLocalFile is not null;
        VolumeMenu.IsEnabled = ready;
        MuteMenu.Text = UiText.Get(_snapshot?.Muted == true ? "Unmute" : "Mute");
    }
    private async void MenuPlay_Click(object sender, RoutedEventArgs e) => await TogglePauseAsync("menu");
    private void RestorePlayerMenuFocus()
    {
        // IINA returns keyboard input to the player window. Wait for WinUI's
        // flyout to restore its anchor, then relinquish More's Space activation.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_closing || _pickerOpen || _sidebarFocusPending || !_surface.HasMedia ||
                OwnsKeyboardInput() || Owner is null ||
                WindowDragNative.GetForegroundWindow() != WinRT.Interop.WindowNative.GetWindowHandle(Owner)) return;
            var focused = FocusManager.GetFocusedElement(XamlRoot);
            // A click outside the menu may already have selected a different
            // control. Preserve that control's native keyboard interaction.
            if (focused is not null && !ReferenceEquals(focused, VideoFocus) &&
                !ReferenceEquals(focused, MoreButton) && !ReferenceEquals(focused, MusicMoreButton) &&
                !ReferenceEquals(focused, WelcomeMore) && focused is not MenuFlyoutItemBase) return;
            (IsMusicMode ? MusicPlayButton : VideoFocus).Focus(FocusState.Keyboard);
        });
    }
}

// UI adaptation of IINA 1.5.1/build 180 InitialWindowController and MainWindowController.
// Copyright (c) 2016-2026 lhc and contributors. GPL-3.0; licenses/IINA-GPL-3.0.txt.
using Mani.Playback;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI.Core;
using Microsoft.UI.Input;

namespace Mani_App;

public sealed partial class MainPage
{
    private readonly MediaOpenOperation _opening = new();
    private bool _uiOpening => _opening.IsActive;
    private bool _dialogOpen;
    private PlayerSurfaceState _surface;

    private void ApplySurface(PlaybackSnapshot? snapshot)
    {
        if (_sessionUnavailable) snapshot = null;
        if (snapshot is null || _uiOpening) ResetOsd();
        _surface = PlayerSurfaceState.From(snapshot, _uiOpening);
        if (IsMusicMode && _surface.Surface is PlayerSurface.Welcome or PlayerSurface.Stopped or PlayerSurface.Failed) SetMusicMode(false, automatically: true);
        ObserveWindowFloating(snapshot);
        if (IsPictureInPicture && PictureInPictureLayout.ShouldReturn(_surface.Surface)) Owner?.ExitPictureInPicture();
        if (!_uiOpening && _quickPane != QuickPane.Playlist && _surface.Surface is PlayerSurface.Stopped or PlayerSurface.Failed) CloseLayoutSidebar(animate: false);
        bool media = _surface.HasMedia;
        EmptyBackdrop.Visibility = media ? Visibility.Collapsed : Visibility.Visible;
        VideoFocus.IsTabStop = media;
        SetText(MediaTitle, _surface.Title);
        Owner?.SetMediaTitle(_surface.Title);
        bool loading = _surface.Surface == PlayerSurface.Loading;
        LoadingRing.IsActive = loading;
        LoadingRing.Visibility = CancelOpenButton.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        SetText(EmptyHeading, UiText.Get(_surface.Surface switch
        {
            PlayerSurface.Loading => "Loading", PlayerSurface.Failed => "Failed", PlayerSurface.Stopped => "Stopped", _ => "Ready"
        }));
        SetText(EmptyMessage, _notice.Length > 0 ? _notice : UiText.Get(_surface.Surface switch
        {
            PlayerSurface.Loading => "LoadingHint", PlayerSurface.Failed => "FailedHint", _ => "DropHint"
        }));
        _osc.SetActive(media && !_oscSuppressed && !IsMusicMode, Environment.TickCount64, AnimationsEnabled);
        if (CurrentDocking != _appliedDocking) LayoutOsc();
        if (!media)
        {
            _updating = true;
            try { Timeline.Value = 0; Timeline.Maximum = 1; }
            finally { _updating = false; }
            SetText(TimeLabel, "00:00"); SetText(DurationLabel, "00:00");
            _displayedTime = null;
            PlayButton.IsEnabled = Timeline.IsEnabled = false;
        }
        SeekBackButton.IsEnabled = SeekForwardButton.IsEnabled = media && snapshot is { Duration: > 0 };
        StatusOverlay.Visibility = media && (_notice.Length > 0 || snapshot is { PausedForCache: true }) ? Visibility.Visible : Visibility.Collapsed;
        RefreshOscHolds();
        UpdateOsc();
        ConfigureWindowSizing();
        UpdateActionMenu();
        UpdateMusicView();
    }
    private void SetPlayAppearance(string label)
    {
        PlayIcon.Glyph = label == "Pause" ? "\uE769" : "\uE768";
        var text = UiText.Get(label);
        AutomationProperties.SetName(PlayButton, text);
        ToolTipService.SetToolTip(PlayButton, text);
    }
    private void UpdateObservedControls(PlaybackSnapshot snapshot)
    {
        MusicVolumeIcon.Glyph = snapshot.Muted ? "\uE74F" : "\uE767";
        MuteIcon.Glyph = snapshot.Muted ? "\uE74F" : "\uE767";
        AutomationProperties.SetName(MuteButton, UiText.Get(snapshot.Muted ? "Unmute" : "Mute"));
        SpeedMenu.Text = string.Format(System.Globalization.CultureInfo.CurrentCulture, UiText.Get("SpeedFormat"), snapshot.Speed);
    }
    private Task ToggleMuteAsync() => Execute(s => s.CommandAsync("cycle", "mute"));
    private async void Mute_Click(object sender, RoutedEventArgs e) => await ToggleMuteAsync();
    private async void VolumeStep_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && _snapshot is { } state)
            await Execute(s => s.SetVolumeAsync(state.Volume + double.Parse((string)item.Tag, System.Globalization.CultureInfo.InvariantCulture)));
    }
    private async void RelativeSeek_Click(object sender, RoutedEventArgs e)
    {
        if (_surface.HasMedia && _snapshot is { } state && sender is FrameworkElement control)
            await Execute(s => SeekWithOsdAsync(s, Math.Clamp(state.Position + double.Parse((string)control.Tag, System.Globalization.CultureInfo.InvariantCulture), 0, state.Duration)));
    }
    private void WelcomeMore_Click(object sender, RoutedEventArgs e) => MainMenu.ShowAt(WelcomeMore);
    private void VideoFocus_Click(object sender, RoutedEventArgs e)
    {
        VideoFocus.Focus(FocusState.Pointer);
        // Physical clicks are arbitrated after release; UIA/keyboard invocation
        // still focuses the video without inventing a mouse gesture.
        WakeOsc();
    }
    private bool OwnsKeyboardInput()
    {
        if (_dialogOpen || _menuOpen) return true;
        // Expanded ComboBox items live in a popup visual tree; its ancestry
        // need not include the owning ComboBox when an accelerator is invoked.
        for (var node = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is TextBox or PasswordBox or ComboBox or ComboBoxItem or NumberBox or WebView2 or MenuFlyoutPresenter or ContentDialog) return true;
        return false;
    }
    private void OpenShortcut_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        if (OwnsKeyboardInput()) return;
        e.Handled = true; Open_Click(this, new());
    }
    private void OpenUrlShortcut_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        if (OwnsKeyboardInput()) return;
        e.Handled = true; OpenUrl_Click(this, new());
    }
    private void FullscreenShortcut_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        if (OwnsKeyboardInput()) return;
        e.Handled = true; Owner?.ToggleFullscreen(); WakeOsc();
    }
    private async void Page_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_closing) return;
        if (_windowDragging) { EndWindowDrag(e.Key == VirtualKey.Escape); if (e.Key == VirtualKey.Escape) { e.Handled = true; return; } }
        CancelVideoClick();
        if (e.Key == VirtualKey.Escape && HandleSidebarEscape()) { e.Handled = true; return; }
        if (OwnsKeyboardInput()) return;
        // The transparent video-focus Button consumes bare Space before the
        // page accelerator. Route that surface only, before native activation;
        // real controls and text inputs keep their own keyboard behavior.
        if (e.Key == VirtualKey.Space && ReferenceEquals(FocusManager.GetFocusedElement(XamlRoot), VideoFocus) &&
            !new[] { VirtualKey.Control, VirtualKey.Menu, VirtualKey.Shift, VirtualKey.LeftWindows, VirtualKey.RightWindows }
                .Any(key => (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) != 0))
        {
            e.Handled = true;
            if (!e.KeyStatus.WasKeyDown) { WakeOsc(); await TogglePauseAsync("keyboard"); }
            return;
        }
        if (await HandlePlaybackKeyAsync(e)) return;
        if (e.Key == VirtualKey.Escape && IsPictureInPicture)
        { Owner?.ExitPictureInPicture(); e.Handled = true; return; }
        if (e.Key == VirtualKey.Escape && Owner?.IsFullscreen == true)
        { Owner.ToggleFullscreen(); e.Handled = true; WakeOsc(); return; }
        if (e.Key == VirtualKey.Tab && IsMusicMode)
        {
            _keyboardNavigation = true;
            if (!_musicControlsShown) { PlayButton.Focus(FocusState.Keyboard); UpdateMusicControls(); e.Handled = true; }
            return;
        }
        if (e.Key == VirtualKey.Tab)
        {
            _keyboardNavigation = true;
            RecallCursor();
            bool hidden = !_osc.HitTestVisible;
            WakeOsc();
            if (hidden && _surface.HasMedia)
            {
                PlayButton.Focus(FocusState.Keyboard);
                e.Handled = true;
            }
            RefreshOscHolds();
        }
    }
}

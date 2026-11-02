// IINA 1.5.1/build 180 MainWindowController.setupOnScreenController platform adaptation.
// Copyright (c) 2016-2026 lhc and contributors. GPL-3.0; licenses/IINA-GPL-3.0.txt.
using Mani.Playback;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Mani_App;

public sealed partial class MainPage
{
    private OscPosition? _arrangedOscPosition;
    private OscPosition? _pendingOscPosition;
    private bool? _pendingEdgeToEdge, _pendingDockedControls;
    private OscDockingPlan _appliedDocking;
    private bool _timeLabelsAllowed = true;
    private void UpdateTimelineLabels()
    {
        // Keep IINA's slider-first layout at Windows accessibility text sizes.
        // Hide optional labels when the sidebar leaves too little room to seek.
        double labelWidth = Math.Max(38, Math.Max(MainTimeLabel.Text.Length, MainDurationLabel.Text.Length) * 7 * _systemUi.TextScaleFactor);
        var visibility = _timeLabelsAllowed && OscTimeline.ActualWidth >= labelWidth * 2 + 48 ? Visibility.Visible : Visibility.Collapsed;
        MainTimeLabel.Visibility = MainDurationLabel.Visibility = visibility;
    }
    private OscDockingPlan CurrentDocking => IsMusicMode ? new(true, true, 0, MusicBarHeight) : OscDockingLayout.Create(EffectiveOscPosition,
        _settings.EdgeToEdgeVideo, _settings.DockedControls, _surface.HasMedia && !_oscSuppressed,
        Owner?.IsFullscreen == true, VideoArea.ActualHeight, _oscMeasuredHeight);
    private OscBounds CurrentOscBounds => new(OscTranslation.X, OscTranslation.Y, Controls.ActualWidth, Controls.ActualHeight);
    private void PositionOsc(OscBounds bounds)
    {
        // Translate after measurement. A top margin on a Grid child would constrain
        // the next mode to the previous mode's height at the bottom edge.
        Controls.Width = bounds.Width;
        OscTranslation.X = bounds.X;
        OscTranslation.Y = bounds.Y;
    }

    private void ArrangeOscFragments()
    {
        if (_arrangedOscPosition == EffectiveOscPosition) return;
        bool floating = EffectiveOscPosition == OscPosition.Floating;
        _arrangedOscPosition = EffectiveOscPosition;
        // Keep each fragment and its handlers/focus identity. Grid placement is the
        // WinUI equivalent of IINA detaching and reattaching the same fragment views.
        // Retain the container definitions along with the controls.
        OscFragments.RowDefinitions[0].Height = GridLength.Auto;
        OscFragments.RowDefinitions[1].Height = new(floating ? 32 : 0);
        OscFragments.RowDefinitions[2].Height = floating ? GridLength.Auto : new(0);
        OscFragments.ColumnDefinitions[0].Width = floating ? new(1, GridUnitType.Star) : GridLength.Auto;
        OscFragments.ColumnDefinitions[1].Width = floating ? GridLength.Auto : new(1, GridUnitType.Star);
        OscFragments.ColumnDefinitions[2].Width = floating ? new(1, GridUnitType.Star) : GridLength.Auto;
        OscFragments.ColumnDefinitions[3].Width = floating ? new(0) : GridLength.Auto;
        static void Place(FrameworkElement element, int row, int column, int span = 1)
        { Grid.SetRow(element, row); Grid.SetColumn(element, column); Grid.SetColumnSpan(element, span); }
        Place(OscVolume, floating ? 1 : 0, floating ? 0 : 2);
        Place(OscPlayback, floating ? 1 : 0, floating ? 1 : 0);
        Place(OscToolbar, floating ? 1 : 0, floating ? 2 : 3, floating ? 2 : 1);
        Place(OscTimeline, floating ? 2 : 0, floating ? 0 : 1, floating ? 4 : 1);
        OscDragHandle.Visibility = floating && !IsPictureInPicture ? Visibility.Visible : Visibility.Collapsed;
        OscFragments.ColumnSpacing = 0;
        OscPlayback.Spacing = floating ? 8 : 16;
        OscPlayback.Margin = floating ? new(4, 0, 4, 0) : new(0, 0, 8, 0);
        OscVolume.Margin = floating ? new(0) : new(0, 0, 8, 0);
        OscTimeline.Margin = floating ? new(0, 4, 0, 0) : new(0, 0, 8, 0);
        Controls.CornerRadius = new(floating ? 12 : 0);
        Controls.BorderThickness = floating ? new(1) : _settings.OscPosition == OscPosition.Top ? new(0, 0, 0, 1) : new(0, 1, 0, 0);
        PipReturnButton.Visibility = IsPictureInPicture ? Visibility.Visible : Visibility.Collapsed;
        UpdateOscPositionMenu();
    }
    private void UpdateOscPositionMenu()
    {
        FloatingOscOption.IsChecked = _settings.OscPosition == OscPosition.Floating;
        TopOscOption.IsChecked = _settings.OscPosition == OscPosition.Top;
        BottomOscOption.IsChecked = _settings.OscPosition == OscPosition.Bottom;
        ResetOscMenu.IsEnabled = !IsPictureInPicture && _settings.OscPosition == OscPosition.Floating;
        EdgeToEdgeOption.IsChecked = _settings.EdgeToEdgeVideo;
        DockControlsOption.IsChecked = _settings.DockedControls;
        DockControlsOption.Visibility = _settings.EdgeToEdgeVideo ? Visibility.Collapsed : Visibility.Visible;
    }
    private void OscPosition_Click(object sender, RoutedEventArgs e)
    {
        if (_closing || sender is not ToggleMenuFlyoutItem item ||
            !int.TryParse(item.Tag?.ToString(), out int raw) || !Enum.IsDefined((OscPosition)raw)) return;
        var position = (OscPosition)raw;
        if (_settings.OscPosition == position) { UpdateOscPositionMenu(); return; }
        // Finish the flyout lifecycle before moving its anchor and rearranging
        // the accessible controls. Closed applies the requested position.
        _pendingOscPosition = position;
        MainMenu.Hide();
    }
    private void EdgeToEdge_Click(object sender, RoutedEventArgs e)
    {
        if (_closing) return;
        _pendingEdgeToEdge = EdgeToEdgeOption.IsChecked;
        MainMenu.Hide();
    }
    private void DockControls_Click(object sender, RoutedEventArgs e)
    {
        if (_closing) return;
        _pendingDockedControls = DockControlsOption.IsChecked;
        MainMenu.Hide();
    }
    private void ApplyPendingOscLayout()
    {
        if (_closing || (_pendingOscPosition is null && _pendingEdgeToEdge is null && _pendingDockedControls is null)) return;
        // A keyboard-invoked menu can change modes while a pointer still owns a
        // slider/drag handle. Finish that gesture before hiding any fragment.
        _dragOrigin = null;
        OscDragHandle.ReleasePointerCaptures();
        CommitSeek(); Timeline.ReleasePointerCaptures(); Volume.ReleasePointerCaptures();
        _oscPointers.Clear(); _adjustingVolume = false; _oscHover = false;
        _settings = _settings with { OscPosition = _pendingOscPosition ?? _settings.OscPosition,
            EdgeToEdgeVideo = _pendingEdgeToEdge ?? _settings.EdgeToEdgeVideo,
            DockedControls = _pendingDockedControls ?? _settings.DockedControls };
        _pendingOscPosition = null; _pendingEdgeToEdge = _pendingDockedControls = null;
        LayoutOsc();
        ConfigureWindowSizing();
        if (WindowSizing.AspectLocked(_settings)) Owner?.ResizeForVideo(false, false, false, keepSize: true);
        SaveSettings();
        RefreshOscHolds(); WakeOsc();
        Trace("osc-position", new { position = _settings.OscPosition.ToString(), _settings.OscHorizontal,
            _settings.OscBottom, _settings.EdgeToEdgeVideo, _settings.DockedControls });
    }
    private void ApplyOscLayout(OscLayoutPlan plan)
    {
        _appliedDocking = CurrentDocking;
        VideoViewport.Margin = new(_sidebarPlan.VideoLeft, _appliedDocking.Top, _sidebarPlan.VideoRight, _appliedDocking.Bottom);
        _osc.Hold(OscHold.Docked, _appliedDocking.AlwaysShown, Environment.TickCount64, AnimationsEnabled);
        UpdateOscPositionMenu();
        PositionOsc(plan.Bounds);
        Controls.Padding = new(plan.Padding.Left, plan.Padding.Top, plan.Padding.Right, plan.Padding.Bottom);
        static Visibility Show(bool show) => show ? Visibility.Visible : Visibility.Collapsed;
        var focus = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        bool focusDisappears = (!plan.VolumeGroup && Within(focus, OscVolume)) ||
            (!plan.VolumeSlider && Within(focus, Volume)) ||
            (!plan.SeekButtons && (Within(focus, SeekBackButton) || Within(focus, SeekForwardButton))) ||
            (!plan.FullscreenButton && Within(focus, FullscreenButton)) ||
            (!OscLayout.DirectTools(plan) && (Within(focus, PipToolbarButton) || Within(focus, SettingsToolbarButton)));
        OscVolume.Visibility = Show(plan.VolumeGroup);
        Volume.Visibility = Show(plan.VolumeSlider);
        SeekBackButton.Visibility = SeekForwardButton.Visibility = Show(plan.SeekButtons);
        FullscreenButton.Visibility = Show(plan.FullscreenButton);
        PipToolbarButton.Visibility = SettingsToolbarButton.Visibility = Show(!IsPictureInPicture && OscLayout.DirectTools(plan));
        PipToolbarButton.IsEnabled = CanEnterPictureInPicture;
        _timeLabelsAllowed = plan.TimeLabels;
        UpdateTimelineLabels();
        if (focusDisappears && _keyboardNavigation) PlayButton.Focus(FocusState.Keyboard);
        // A docked top bar is already excluded by the viewport; an overlaid one is not.
        StatusOverlay.Margin = new(16, EffectiveOscPosition == OscPosition.Top && !_appliedDocking.Docked ? plan.Bounds.Height + 8 : 16, 16, 16);
        LayoutOsd();
        ConfigureWindowSizing();
    }
}

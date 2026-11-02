// IINA SidebarController/SidebarLayoutPane UI port; Windows input and focus adapter.
// Copyright (c) 2016-2026 lhc and contributors. GPL-3.0; licenses/IINA-GPL-3.0.txt.
using Mani.Playback;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Mani_App;

public sealed partial class MainPage
{
    private readonly LayoutSidebarState _sidebar = new();
    private readonly DispatcherTimer _sidebarTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private bool _sidebarUpdating, _sidebarDragging, _sidebarWidthEditing, _sidebarFocusPending, _pendingLayoutToggle;
    private SidebarPlan _sidebarPlan;
    private bool? _pendingSidebarLeading;
    private string _sidebarEvidence = "";
    private double SavedSidebarWidth => _settings.SidebarSettingsAtLeading ? _settings.LeadingSidebarWidth : _settings.TrailingSidebarWidth;
    private double OscAvailableWidth => Math.Max(1, VideoArea.ActualWidth - _sidebarPlan.Occupied);
    private double ReservedSidebarWidth => !IsPictureInPicture && !IsMusicMode && _sidebar.WantsVisible && !_settings.EdgeToEdgeVideo ? SidebarLayout.ClampWidth(SavedSidebarWidth, VideoArea.ActualWidth) : 0;

    private void InitializeLayoutSidebar()
    {
        _sidebarTimer.Tick += SidebarTick;
        LayoutOscMode.Loaded += (_, _) => UpdateQuickTabLabels();
        LayoutWidthSlider.AddHandler(PointerPressedEvent, new PointerEventHandler((_, e) =>
        { if (e.GetCurrentPoint(LayoutWidthSlider).Properties.IsLeftButtonPressed) _sidebarWidthEditing = true; }), true);
        LayoutWidthSlider.AddHandler(PointerReleasedEvent, new PointerEventHandler((_, _) => EndSidebarWidthEdit()), true);
        LayoutWidthSlider.AddHandler(PointerCaptureLostEvent, new PointerEventHandler((_, _) => EndSidebarWidthEdit()), true);
        LayoutWidthSlider.LostFocus += (_, _) => EndSidebarWidthEdit();
    }
    private void LayoutMenu_Click(object sender, RoutedEventArgs e)
    {
        if (_closing || _m1Busy) return;
        _pendingLayoutToggle = true;
        MainMenu.Hide();
    }
    private void ApplyPendingLayoutSidebar()
    {
        if (!_pendingLayoutToggle || _closing) return;
        _pendingLayoutToggle = false;
        var requested = _requestedQuickPane ?? QuickPane.Layout;
        _requestedQuickPane = null;
        if (_sidebar.Animating) return;
        if (_sidebar.WantsVisible && !QuickSettings.ToggleOff(true, _quickPane, requested))
        {
            SelectQuickPane(requested);
            SidebarClose.Focus(FocusState.Keyboard);
            return;
        }
        SelectQuickPane(requested);
        if (!_sidebar.Toggle(Environment.TickCount64, AnimationsEnabled)) return;
        if (_sidebar.WantsVisible)
        {
            LabPanel.Visibility = InfoPanel.Visibility = Visibility.Collapsed;
            _sidebarFocusPending = true;
        }
        else { CancelDelayEdits(); RestoreSidebarFocus(); }
        UpdateLayoutSidebar();
    }
    private void CloseLayoutSidebar(bool animate = true, [CallerMemberName] string source = "")
    {
        if (!_sidebar.Visible) return;
        CancelDelayEdits();
        Trace("sidebar-close-request", new { source, pane = _quickPane.ToString(), phase = _sidebar.Phase.ToString() });
        _sidebarFocusPending = false;
        RestoreSidebarFocus();
        EndSidebarDrag(); EndSidebarWidthEdit();
        _sidebar.SetVisible(false, Environment.TickCount64, animate && AnimationsEnabled);
        UpdateLayoutSidebar();
    }
    private void SidebarClose_Click(object sender, RoutedEventArgs e) => CloseLayoutSidebar();
    private void RestoreSidebarFocus()
    {
        if (!Within(FocusManager.GetFocusedElement(XamlRoot) as DependencyObject, LayoutSidebar)) return;
        if (_surface.HasMedia) VideoFocus.Focus(FocusState.Keyboard);
        else DispatcherQueue.TryEnqueue(() =>
        { if (!_closing && !_sidebar.WantsVisible && !_pickerOpen && !_dialogOpen && !_surface.HasMedia) WelcomeMore.Focus(FocusState.Keyboard); });
    }
    private bool HandleSidebarEscape()
    {
        if (IsPictureInPicture || IsMusicMode || !_sidebar.WantsVisible || _menuOpen || _dialogOpen || _pickerOpen) return false;
        for (var node = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is ComboBox { IsDropDownOpen: true }) return false;
        CloseLayoutSidebar();
        return true;
    }
    private void SidebarTick(object? sender, object e)
    {
        if (_closing) return;
        _sidebar.Tick(Environment.TickCount64, AnimationsEnabled);
        UpdateLayoutSidebar();
    }
    private void UpdateLayoutSidebar()
    {
        LayoutOsc();
        RefreshLayoutSettings();
        LayoutSidebar.Visibility = _sidebar.Visible && !IsPictureInPicture && !IsMusicMode ? Visibility.Visible : Visibility.Collapsed;
        SidebarControls.IsEnabled = LayoutSidebar.IsHitTestVisible = _sidebar.WantsVisible && !IsPictureInPicture && !IsMusicMode;
        SidebarResizeGrip.Visibility = !IsPictureInPicture && !IsMusicMode && _sidebar.Phase == SidebarPhase.Shown ? Visibility.Visible : Visibility.Collapsed;
        if (_sidebar.Animating) _sidebarTimer.Start(); else _sidebarTimer.Stop();
        if (_sidebarFocusPending && _sidebar.Phase == SidebarPhase.Shown)
        { _sidebarFocusPending = false; SidebarClose.Focus(FocusState.Keyboard); }
        RefreshOscHolds(); WakeOsc();
        string evidence = $"{_sidebar.Phase}/{_settings.SidebarSettingsAtLeading}/{_settings.EdgeToEdgeVideo}/{_sidebarPlan.Width:F1}";
        if (evidence != _sidebarEvidence)
        {
            _sidebarEvidence = evidence;
            Trace("layout-sidebar", new { phase = _sidebar.Phase.ToString(), leading = _settings.SidebarSettingsAtLeading,
                edgeToEdge = _settings.EdgeToEdgeVideo, width = _sidebarPlan.Width, _settings.LeadingSidebarWidth,
                _settings.TrailingSidebarWidth, surfaceWidth = VideoArea.ActualWidth, videoLeft = _sidebarPlan.VideoLeft,
                videoRight = _sidebarPlan.VideoRight, fullscreen = Owner?.IsFullscreen });
        }
    }
    private void ArrangeLayoutSidebar()
    {
        _sidebarPlan = SidebarLayout.Create(VideoArea.ActualWidth, SavedSidebarWidth, IsPictureInPicture || IsMusicMode ? 0 : _sidebar.Expansion,
            _settings.SidebarSettingsAtLeading, _settings.EdgeToEdgeVideo);
        LayoutSidebar.Width = _sidebarPlan.Width;
        // Keep the scroll viewport bounded to the current client height, including
        // a window shrink while keyboard focus remains inside the pane.
        LayoutSidebar.Height = Math.Max(0, VideoArea.ActualHeight);
        LayoutSidebar.HorizontalAlignment = _settings.SidebarSettingsAtLeading ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        SidebarTranslation.X = _sidebarPlan.Translation;
        UpdateQuickTabLabels();
        Grid.SetColumn(SidebarClose, _settings.SidebarSettingsAtLeading ? 1 : 0);
        Grid.SetColumn(SidebarTabsBorder, _settings.SidebarSettingsAtLeading ? 0 : 1);
        SidebarHeader.ColumnDefinitions[0].Width = _settings.SidebarSettingsAtLeading ? new(1, GridUnitType.Star) : GridLength.Auto;
        SidebarHeader.ColumnDefinitions[1].Width = _settings.SidebarSettingsAtLeading ? GridLength.Auto : new(1, GridUnitType.Star);
        SidebarTabsBorder.HorizontalAlignment = _settings.SidebarSettingsAtLeading ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        SidebarCloseIcon.Glyph = _settings.SidebarSettingsAtLeading ? "\uE76B" : "\uE76C";
        SidebarGripTranslation.X = _sidebarPlan.GripX;
        LayoutSidebar.BorderThickness = _settings.SidebarSettingsAtLeading ? new(0, 0, 1, 0) : new(1, 0, 0, 0);
        ConfigureWindowSizing();
    }
    private void RefreshLayoutSettings()
    {
        if (_sidebarUpdating) return;
        _sidebarUpdating = true;
        try
        {
            LayoutEdge.IsOn = _settings.EdgeToEdgeVideo;
            LayoutLock.IsOn = WindowSizing.AspectLocked(_settings);
            LayoutLockRow.Visibility = _settings.EdgeToEdgeVideo ? Visibility.Visible : Visibility.Collapsed;
            LayoutDock.IsOn = _settings.DockedControls;
            LayoutDockRow.Visibility = _settings.EdgeToEdgeVideo ? Visibility.Collapsed : Visibility.Visible;
            LayoutOscMode.SelectedIndex = (int)_settings.OscPosition;
            if (_pendingSidebarLeading is null)
                LayoutSide.SelectedIndex = _settings.SidebarSettingsAtLeading ? 0 : 1;
            FitVideoButton.Visibility = WindowSizing.AspectLocked(_settings) ? Visibility.Collapsed : Visibility.Visible;
            FitVideoButton.IsEnabled = _surface.HasMedia && _videoSize is not null && Owner?.CanSetWindowSize == true;
            double maximum = SidebarLayout.MaximumWidth(VideoArea.ActualWidth);
            LayoutWidthSlider.Minimum = Math.Min(300, maximum);
            LayoutWidthSlider.Maximum = maximum;
            if (!_sidebarWidthEditing) LayoutWidthSlider.Value = _sidebarPlan.Width;
            LayoutWidthValue.Text = Math.Round(_sidebarPlan.Width).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        finally { _sidebarUpdating = false; }
    }
    private void LayoutToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_sidebarUpdating || _closing || !_settingsAvailable) return;
        if (ReferenceEquals(sender, LayoutLock))
        {
            _pendingWindowSetting = ("lock", LayoutLock.IsOn ? 1 : 0);
            ApplyPendingWindowSizing();
        }
        else
        {
            if (ReferenceEquals(sender, LayoutEdge)) _pendingEdgeToEdge = LayoutEdge.IsOn;
            else _pendingDockedControls = LayoutDock.IsOn;
            ApplyPendingOscLayout();
        }
        UpdateLayoutSidebar();
    }
    private void LayoutOsc_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_sidebarUpdating || _closing || !_settingsAvailable || LayoutOscMode.SelectedIndex < 0) return;
        _pendingOscPosition = (OscPosition)LayoutOscMode.SelectedIndex;
        ApplyPendingOscLayout(); UpdateLayoutSidebar();
    }
    private void LayoutSide_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_sidebarUpdating || _closing || !_settingsAvailable || LayoutSide.SelectedIndex < 0) return;
        _pendingSidebarLeading = LayoutSide.SelectedIndex == 0;
        // Complete selection dispatch before moving the same accessible control to the other side.
        DispatcherQueue.TryEnqueue(ApplySidebarPosition);
    }
    private void ApplySidebarPosition()
    {
        if (_closing || _pendingSidebarLeading is not { } leading) return;
        _pendingSidebarLeading = null;
        if (leading == _settings.SidebarSettingsAtLeading) return;
        EndSidebarDrag(); EndSidebarWidthEdit();
        _settings = _settings with { SidebarSettingsAtLeading = leading };
        SaveSettings();
        bool shown = _sidebar.WantsVisible;
        _sidebar.SetVisible(false, Environment.TickCount64, false);
        if (shown) _sidebar.SetVisible(true, Environment.TickCount64, AnimationsEnabled);
        UpdateLayoutSidebar();
    }
    private void SetSidebarWidth(double width, bool save)
    {
        int value = SidebarLayout.StoredWidth(width);
        _settings = _settings.SidebarSettingsAtLeading ? _settings with { LeadingSidebarWidth = value } :
            _settings with { TrailingSidebarWidth = value };
        UpdateLayoutSidebar();
        if (save) SaveSettings();
    }
    private void SidebarWidth_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_sidebarUpdating || _closing || !_settingsAvailable) return;
        SetSidebarWidth(e.NewValue, !_sidebarWidthEditing);
    }
    private void EndSidebarWidthEdit()
    { if (!_sidebarWidthEditing) return; _sidebarWidthEditing = false; SaveSettings(); }
    private void SidebarResize_Pressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(SidebarResizeGrip).Properties.IsLeftButtonPressed || !SidebarResizeGrip.CapturePointer(e.Pointer)) return;
        _sidebarDragging = true; e.Handled = true;
        RefreshOscHolds();
    }
    private void SidebarResize_Moved(object sender, PointerRoutedEventArgs e)
    {
        if (!_sidebarDragging) return;
        SetSidebarWidth(SidebarLayout.DragWidth(e.GetCurrentPoint(VideoArea).Position.X, VideoArea.ActualWidth,
            _settings.SidebarSettingsAtLeading), false);
        e.Handled = true;
    }
    private void EndSidebarDrag()
    {
        if (!_sidebarDragging) return;
        _sidebarDragging = false; SidebarResizeGrip.ReleasePointerCaptures(); SaveSettings(); RefreshOscHolds();
    }
    private void SidebarResize_Released(object sender, PointerRoutedEventArgs e)
    { EndSidebarDrag(); e.Handled = true; }
    private void FitVideo_Click(object sender, RoutedEventArgs e)
    {
        var frame = Owner?.FitWindowToVideo();
        Trace("fit-window-to-video", new { frame });
    }
    private void StopLayoutSidebar()
    {
        _sidebarTimer.Stop(); _sidebarTimer.Tick -= SidebarTick;
        EndSidebarDrag(); EndSidebarWidthEdit();
    }
}

// Windows counterpart of IINA's NSCursor.resizeLeftRight on the inner resize strip.
public sealed class SidebarResizeHandle : UserControl
{
    public SidebarResizeHandle() => ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.SizeWestEast);
}

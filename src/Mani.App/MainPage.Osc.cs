// IINA 1.5.1/build 180 OSCFloatingView and MainWindowController show/hide adaptation.
// Copyright (c) 2016-2026 lhc and contributors. GPL-3.0; licenses/IINA-GPL-3.0.txt.
using Microsoft.UI.Xaml;
using Mani.Playback;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI.ViewManagement;

namespace Mani_App;

public sealed partial class MainPage
{
    private readonly FloatingOscState _osc = new();
    private readonly DispatcherTimer _oscTimer = new() { Interval = TimeSpan.FromMilliseconds(25) };
    private readonly UISettings _systemUi = new();
    private readonly AccessibilitySettings _accessibility = new();
    private readonly HashSet<uint> _oscPointers = [];
    private bool _oscHover, _menuOpen, _keyboardNavigation, _positioningOsc;
    private bool _oscSuppressed;
    private Point? _dragOrigin;
    private (int X, int Y)? _oscPointerScreen;
    private OscBounds _dragBounds;
    private Task _oscMaterialTask = Task.CompletedTask;
    private double _oscMeasuredHeight = 102;
    private (bool Contrast, bool Effects, bool Docked, bool Edge)? _oscMaterialStyle;
    private bool _glassMaterialDirty;
    private string _lastOscEvidence = "";
    private bool AnimationsEnabled => _systemUi.AnimationsEnabled;

    private void InitializeOsc()
    {
        InitializeCursor();
        _oscTimer.Tick += OscTick;
        VideoArea.AddHandler(PointerMovedEvent, new PointerEventHandler(VideoPointerMoved), true);
        VideoArea.AddHandler(PointerEnteredEvent, new PointerEventHandler(VideoPointerMoved), true);
        VideoArea.AddHandler(PointerPressedEvent, new PointerEventHandler(VideoPointerPressed), true);
        Controls.AddHandler(PointerPressedEvent, new PointerEventHandler(OscPointerPressed), true);
        Controls.AddHandler(PointerReleasedEvent, new PointerEventHandler(OscPointerReleased), true);
        Controls.AddHandler(PointerCaptureLostEvent, new PointerEventHandler(OscPointerReleased), true);
        VideoArea.AddHandler(PointerExitedEvent, new PointerEventHandler(VideoPointerExited), true);
        GotFocus += OscFocusChanged;
        LostFocus += OscFocusChanged;
        OsdOverlay.SizeChanged += (_, _) => RequestOscMaterial();
        OscTimeline.SizeChanged += (_, _) => UpdateTimelineLabels();
        // AccessibilitySettings change subscriptions require a UWP CoreWindow on this
        // runtime. Read current settings with the existing UI poll and animation tick.
    }
    private void OscTick(object? sender, object e) { if (!_closing) { RefreshOscHolds(); UpdateOsc(); } }
    private void WakeOsc()
    {
        if (_closing) return;
        _osc.Wake(Environment.TickCount64, AnimationsEnabled);
        UpdateOsc();
    }
    private static bool Within(DependencyObject? node, DependencyObject ancestor)
    {
        for (; node is not null; node = VisualTreeHelper.GetParent(node)) if (node == ancestor) return true;
        return false;
    }
    private void RefreshOscHolds()
    {
        if (_closing) return;
        var now = Environment.TickCount64;
        void Hold(OscHold reason, bool active) => _osc.Hold(reason, active, now, AnimationsEnabled);
        Hold(OscHold.Hover, _oscHover);
        Hold(OscHold.Menu, _menuOpen);
        Hold(OscHold.Capture, _oscPointers.Count > 0 || _seeking || _adjustingVolume);
        Hold(OscHold.Keyboard, (_keyboardNavigation && Within(FocusManager.GetFocusedElement(XamlRoot) as DependencyObject, Controls)) ||
            Within(FocusManager.GetFocusedElement(XamlRoot) as DependencyObject, LayoutSidebar));
        Hold(OscHold.Drag, _dragOrigin is not null || _sidebarDragging || _sidebarWidthEditing || _windowDragging);
        Hold(OscHold.Dialog, _dialogOpen || _pickerOpen);
        Hold(OscHold.Diagnostics, _m1Busy || LabPanel.Visibility == Visibility.Visible || InfoPanel.Visibility == Visibility.Visible);
        Hold(OscHold.Docked, CurrentDocking.AlwaysShown);
    }
    private void UpdateOsc()
    {
        if (_closing) return;
        UpdateMusicTextScale();
        UpdateTimelineLabels();
        UpdateTrackpadSources();
        UpdateCursor();
        UpdateMusicControls();
        _osc.Configure(_settings.EnableControlBarAutoHide, (long)Math.Round(_settings.ControlBarAutoHideTimeout * 1000), Environment.TickCount64, AnimationsEnabled);
        _osc.Tick(Environment.TickCount64, AnimationsEnabled);
        Controls.Opacity = _osc.Opacity;
        Controls.Visibility = _osc.Visible ? Visibility.Visible : Visibility.Collapsed;
        Controls.IsHitTestVisible = _osc.HitTestVisible;
        bool contrast = _accessibility.HighContrast;
        var style = (Contrast: contrast, Effects: _systemUi.AdvancedEffectsEnabled, Docked: _appliedDocking.Docked, Edge: _settings.EdgeToEdgeVideo);
        if (_oscMaterialStyle != style)
        {
            Owner?.UpdateCaptionColors(contrast);
            Brush Glass(byte alpha) => new LinearGradientBrush
            {
                StartPoint = new(0, 0), EndPoint = new(0, 1),
                GradientStops = { new() { Color = Windows.UI.Color.FromArgb(alpha, 48, 48, 54), Offset = 0 },
                    new() { Color = Windows.UI.Color.FromArgb((byte)Math.Min(255, alpha + 20), 28, 28, 34), Offset = 1 } }
            };
            var solid = (Brush)Application.Current.Resources["ApplicationPageBackgroundThemeBrush"];
            Controls.Background = contrast || !style.Effects ? solid : Glass(style.Docked ? (byte)255 : (byte)145);
            LayoutSidebar.Background = contrast || !style.Effects ? solid : Glass(style.Edge ? (byte)180 : (byte)255);
            OsdOverlay.Background = contrast || !style.Effects ? solid : Glass(175);
            var edge = contrast ? (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"] :
                new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(1, 1), GradientStops =
                { new() { Color = Windows.UI.Color.FromArgb(90, 255, 255, 255), Offset = 0 },
                  new() { Color = Windows.UI.Color.FromArgb(28, 255, 255, 255), Offset = 1 } } };
            Controls.BorderBrush = LayoutSidebar.BorderBrush = OsdOverlay.BorderBrush = edge;
            OsdOverlay.BorderThickness = new(1);
            OsdOverlay.CornerRadius = new(12);
            StatusOverlay.Background = contrast ? (Brush)Application.Current.Resources["ApplicationPageBackgroundThemeBrush"] :
                new SolidColorBrush(Windows.UI.Color.FromArgb(208, 32, 32, 40));
            _oscMaterialStyle = style;
            RequestOscMaterial();
        }
        // Changes only: finite transition samples and geometry are useful for local
        // interaction verification without recording pointer coordinates or media paths.
        var bounds = CurrentOscBounds;
        string evidence = FormattableString.Invariant($"{_settings.OscPosition}/{_osc.Active}/{_osc.Holds}/{_osc.Opacity:F3}/{bounds.X:F3}/{bounds.Y:F3}/{bounds.Width:F1}/{bounds.Height:F1}/{_appliedDocking}/{VideoPanel.ActualHeight:F1}");
        if (evidence != _lastOscEvidence)
        {
            _lastOscEvidence = evidence;
            Trace("osc", new { mode = _settings.OscPosition.ToString(), active = _osc.Active, holds = _osc.Holds.ToString(), opacity = _osc.Opacity,
                visible = _osc.Visible, hitTest = _osc.HitTestVisible, x = bounds.X, y = bounds.Y,
                width = Controls.ActualWidth, height = Controls.ActualHeight, animations = AnimationsEnabled,
                docked = _appliedDocking.Docked, alwaysShown = _appliedDocking.AlwaysShown,
                videoTop = _appliedDocking.Top, videoBottom = _appliedDocking.Bottom,
                surfaceWidth = VideoArea.ActualWidth, surfaceHeight = VideoArea.ActualHeight,
                videoWidth = VideoPanel.ActualWidth, videoHeight = VideoPanel.ActualHeight,
                scaleX = VideoPanel.CompositionScaleX, scaleY = VideoPanel.CompositionScaleY });
            RequestOscMaterial();
        }
        if (_osc.NeedsTick)
        {
            // Match IINA's one-shot inactivity timer. Fast ticks only drive a fade;
            // waiting for inactivity must not introduce continuous native property reads.
            _oscTimer.Interval = TimeSpan.FromMilliseconds(_osc.Animating ? 25 : _osc.MillisecondsUntilHide(Environment.TickCount64));
            _oscTimer.Start();
        }
        else _oscTimer.Stop();
    }
    private void RequestOscMaterial()
    {
        if (_closing || !_blurEnabled) return;
        _glassMaterialDirty = true;
        if (_oscMaterialTask.IsCompleted) _oscMaterialTask = UpdateOscMaterialAsync();
    }
    private async Task UpdateOscMaterialAsync()
    {
        try
        {
            do
            {
                _glassMaterialDirty = false;
                await SyncBlurMaskAsync();
            } while (_glassMaterialDirty && !_closing);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!_closing) Trace("osc-material-error", new { error.Message }); }
    }
    private void LayoutOsc()
    {
        if (_closing || _positioningOsc || Controls is null) return;
        _positioningOsc = true;
        try
        {
            ArrangeLayoutSidebar();
            if (IsMusicMode)
            {
                _appliedDocking = new(true, true, 0, MusicBarHeight);
                VideoViewport.Margin = new(0, 0, 0, MusicBarHeight);
                Controls.Visibility = Visibility.Collapsed;
                ConfigureWindowSizing();
                return;
            }
            ArrangeOscFragments();
            var plan = IsPictureInPicture ? PictureInPictureLayout.Controls(OscAvailableWidth, VideoArea.ActualHeight, _oscMeasuredHeight) :
                OscLayout.Create(_settings.OscPosition, OscAvailableWidth, VideoArea.ActualHeight,
                _oscMeasuredHeight, _settings.OscHorizontal, _settings.OscBottom, Owner?.IsFullscreen == true);
            ApplyOscLayout(plan with { Bounds = plan.Bounds with { X = plan.Bounds.X + _sidebarPlan.OccupiedLeft } });
        }
        finally { _positioningOsc = false; }
        RequestOscMaterial();
    }
    private void Controls_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Height > 0) _oscMeasuredHeight = e.NewSize.Height;
        LayoutOsc();
    }
    private void VideoPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_closing) return;
        bool moved = true;
        if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse && MusicPointerNative.GetCursorPos(out var screen))
        {
            moved = _oscPointerScreen != (screen.X, screen.Y);
            _oscPointerScreen = (screen.X, screen.Y);
        }
        ObserveClickBoundary();
        _musicPointerInside = !IsMusicMode || Owner?.MusicPointerInside() == true;
        UpdateMusicControls();
        var p = e.GetCurrentPoint(Controls).Position;
        _oscHover = _osc.HitTestVisible && p.X >= 0 && p.Y >= 0 && p.X <= Controls.ActualWidth && p.Y <= Controls.ActualHeight;
        // WinUI can emit pointer updates while stationary (tooltip/cursor/layout).
        // IINA's mouseMoved inactivity rule must not restart for those updates.
        RefreshOscHolds();
        if (moved) WakeOsc(); else UpdateOsc();
    }
    private void VideoPointerPressed(object sender, PointerRoutedEventArgs e)
    { _keyboardNavigation = false; RefreshOscHolds(); WakeOsc(); }
    private void VideoPointerExited(object sender, PointerRoutedEventArgs e)
    {
        ObserveClickBoundary();
        if (IsMusicMode)
        {
            _musicPointerInside = Owner?.MusicPointerInside() == true;
            UpdateMusicControls();
            return;
        }
        var point = e.GetCurrentPoint(VideoArea).Position;
        if (point.X >= 0 && point.Y >= 0 && point.X < VideoArea.ActualWidth && point.Y < VideoArea.ActualHeight) return;
        _oscPointerScreen = null;
        _musicPointerInside = false; UpdateMusicControls();
        _oscHover = false; RefreshOscHolds(); _osc.Leave(Environment.TickCount64, AnimationsEnabled); UpdateOsc();
    }
    private void Controls_PointerEntered(object sender, PointerRoutedEventArgs e) { _oscHover = true; RefreshOscHolds(); UpdateOsc(); }
    private void Controls_PointerExited(object sender, PointerRoutedEventArgs e) { _oscHover = false; RefreshOscHolds(); UpdateOsc(); }
    private void OscPointerPressed(object sender, PointerRoutedEventArgs e) { _oscPointers.Add(e.Pointer.PointerId); RefreshOscHolds(); UpdateOsc(); }
    private void OscPointerReleased(object sender, PointerRoutedEventArgs e) { _oscPointers.Remove(e.Pointer.PointerId); RefreshOscHolds(); UpdateOsc(); }
    private void OscFocusChanged(object sender, RoutedEventArgs e) => DispatcherQueue.TryEnqueue(() => { if (!_closing) { RefreshOscHolds(); UpdateOsc(); } });
    private void OscMenu_Opened(object sender, object e) { _menuOpen = true; UpdateWindowsMenu(); UpdateActionMenu(); RefreshOscHolds(); UpdateOsc(); }
    private void OscMenu_Closed(object sender, object e)
    { _menuOpen = false; ApplyPendingOscLayout(); ApplyPendingWindowSizing(); ApplyPendingWindowFloating(); ApplyPendingLayoutSidebar(); ApplyPendingPictureInPicture(); ApplyPendingMusicMode(); ApplySharedSettings(); RefreshOscHolds(); UpdateOsc(); RestorePlayerMenuFocus(); }
    private void OscDrag_Pressed(object sender, PointerRoutedEventArgs e)
    {
        if (_settings.OscPosition != OscPosition.Floating || !e.GetCurrentPoint(OscDragHandle).Properties.IsLeftButtonPressed || !OscDragHandle.CapturePointer(e.Pointer)) return;
        _dragOrigin = e.GetCurrentPoint(VideoArea).Position;
        _dragBounds = CurrentOscBounds;
        RefreshOscHolds(); e.Handled = true;
    }
    private void OscDrag_Moved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragOrigin is not { } origin) return;
        var point = e.GetCurrentPoint(VideoArea).Position;
        var bounds = FloatingOscLayout.Drag(_dragBounds with { X = _dragBounds.X - _sidebarPlan.OccupiedLeft }, OscAvailableWidth, VideoArea.ActualHeight,
            _dragBounds.X - _sidebarPlan.OccupiedLeft + point.X - origin.X, _dragBounds.Y + point.Y - origin.Y);
        var position = FloatingOscLayout.Normalize(bounds, OscAvailableWidth, VideoArea.ActualHeight);
        _settings = _settings with { OscHorizontal = position.Horizontal, OscBottom = position.Bottom };
        PositionOsc(bounds with { X = bounds.X + _sidebarPlan.OccupiedLeft });
        RequestOscMaterial(); e.Handled = true;
    }
    private void OscDrag_Released(object sender, PointerRoutedEventArgs e)
    {
        if (_dragOrigin is null) return;
        _dragOrigin = null; OscDragHandle.ReleasePointerCaptures();
        SaveSettings(); RefreshOscHolds(); WakeOsc(); e.Handled = true;
    }
    private void ResetOsc_Click(object sender, RoutedEventArgs e)
    {
        _settings = _settings with { OscHorizontal = .5, OscBottom = .1 };
        LayoutOsc(); SaveSettings(); WakeOsc();
    }
    private void StopOsc()
    {
        StopCursor();
        _oscTimer.Stop(); _oscTimer.Tick -= OscTick;
        _dragOrigin = null; OscDragHandle.ReleasePointerCaptures(); _oscPointers.Clear();
        MainMenu.Hide();
    }
}

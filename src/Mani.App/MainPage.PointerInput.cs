// IINA PlayerWindowController/MainWindowController default pointer routing.
// Copyright (c) 2016-2026 lhc and contributors. GPL-3.0; licenses/IINA-GPL-3.0.txt.
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Mani_App;

public sealed partial class MainPage
{
    private readonly VideoClickState _videoClicks = new();
    private readonly PendingWheelInput _wheelInput = new();
    private readonly WheelEventSequence _wheelEvents = new();
    private readonly DispatcherTimer _videoClickTimer = new();
    private Point? _videoPress;
    private bool _windowDragging, _musicDragCapture;
    private readonly DispatcherTimer _windowDragTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private UIElement? _windowDragCapture;
    private (long Request, long Entry)? _clickMedia;
    private bool PointerInputReady => !_sessionUnavailable && !_closing && !_uiOpening && !_m1Busy && !_pickerOpen && !_dialogOpen && !_menuOpen && _surface.HasMedia;

    private void InitializePointerInput()
    {
        InitializeTrackpad();
        VideoFocus.AddHandler(PointerPressedEvent, new PointerEventHandler(VideoPrimaryPressed), true);
        VideoFocus.AddHandler(PointerMovedEvent, new PointerEventHandler(VideoPrimaryMoved), true);
        VideoFocus.AddHandler(PointerReleasedEvent, new PointerEventHandler(VideoPrimaryReleased), true);
        VideoFocus.AddHandler(PointerCaptureLostEvent, new PointerEventHandler(VideoPrimaryCaptureLost), true);
        VideoFocus.AddHandler(PointerWheelChangedEvent, new PointerEventHandler(PlayerWheelChanged), true);
        MusicBar.AddHandler(PointerPressedEvent, new PointerEventHandler(VideoPrimaryPressed), true);
        MusicBar.AddHandler(PointerMovedEvent, new PointerEventHandler(VideoPrimaryMoved), true);
        MusicBar.AddHandler(PointerReleasedEvent, new PointerEventHandler(VideoPrimaryReleased), true);
        MusicBar.AddHandler(PointerCaptureLostEvent, new PointerEventHandler(VideoPrimaryCaptureLost), true);
        foreach (var slider in new[] { MainTimeline, MusicTimeline, MainVolume, MusicVolume }) slider.PlayerWheel += PlayerWheelChanged;
        MusicVolumeButton.PointerWheelChanged += PlayerWheelChanged;
        MusicVolumeContent.PointerWheelChanged += PlayerWheelChanged;
        _videoClickTimer.Tick += VideoSingleExpired;
        _windowDragTimer.Tick += WindowDragTick;
    }
    private void CancelVideoClick(bool releaseCapture = true)
    {
        _videoClickTimer.Stop(); _videoClicks.Cancel(); _videoPress = null; _clickMedia = null;
        if (releaseCapture && _musicDragCapture) { _musicDragCapture = false; MusicBar.ReleasePointerCaptures(); }
    }
    private void ObserveClickBoundary()
    {
        _videoClicks.ObserveInside(Owner?.MusicPointerInside() == true);
        if (!_videoClicks.Pending) _videoClickTimer.Stop();
    }
    private void VideoPrimaryPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!PointerInputReady || _windowDragging || e.Pointer.PointerDeviceType != PointerDeviceType.Mouse || !e.GetCurrentPoint(VideoArea).Properties.IsLeftButtonPressed) return;
        if (ReferenceEquals(sender, MusicBar))
        {
            if (!IsMusicMode) return;
            for (var node = e.OriginalSource as DependencyObject; node is not null && node != MusicBar; node = VisualTreeHelper.GetParent(node))
                if (node is ButtonBase or Slider) return;
            _musicDragCapture = MusicBar.CapturePointer(e.Pointer);
            if (!_musicDragCapture) return;
        }
        _videoPress = e.GetCurrentPoint(VideoArea).Position;
    }
    private void VideoPrimaryMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_windowDragging) { e.Handled = true; WindowDragTick(null, EventArgs.Empty); return; }
        if (_videoPress is not { } origin) return;
        var point = e.GetCurrentPoint(VideoArea);
        if (!point.Properties.IsLeftButtonPressed) { CancelVideoClick(); return; }
        if (!WindowDragPolicy.BeyondThreshold(point.Position.X - origin.X, point.Position.Y - origin.Y)) return;
        CancelVideoClick(releaseCapture: false);
        if (!PointerInputReady || Owner is not { IsFullscreen: false } owner || _windowDragging) { CancelVideoClick(); return; }
        _windowDragging = true;
        e.Handled = true;
        try
        {
            _windowDragCapture = (UIElement)sender;
            if (!_windowDragCapture.CapturePointer(e.Pointer) || !owner.BeginVideoWindowDrag())
            { EndWindowDrag(); return; }
            RefreshOscHolds(); UpdateOsc();
            Trace("window-drag-start", new { music = IsMusicMode, pip = IsPictureInPicture, request = _snapshot?.RequestId });
            _windowDragTimer.Start();
        }
        catch (Exception error) { EndWindowDrag(); ShowError(error); }
    }
    private void WindowDragTick(object? sender, object e)
    {
        if (!_windowDragging) return;
        try
        {
            if (!PointerInputReady || Owner?.ContinueVideoWindowDrag() != true) EndWindowDrag();
        }
        catch (Exception error) { EndWindowDrag(); ShowError(error); }
    }
    internal void EndWindowDrag(bool cancel = false)
    {
        if (!_windowDragging) return;
        _windowDragging = false;
        _windowDragTimer.Stop();
        Owner?.EndVideoWindowDrag(cancel);
        var capture = _windowDragCapture; _windowDragCapture = null;
        capture?.ReleasePointerCaptures();
        CancelVideoClick();
        if (!_closing) { RefreshOscHolds(); WakeOsc(); Trace("window-drag-end", new { cancel }); }
    }
    private void VideoPrimaryCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_windowDragging) { EndWindowDrag(); return; }
        // Button releases its own capture before bubbling PointerReleased.
        if (e.GetCurrentPoint(VideoFocus).Properties.IsLeftButtonPressed) CancelVideoClick();
    }
    private void VideoPrimaryReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_windowDragging) { EndWindowDrag(); e.Handled = true; return; }
        if (_videoPress is not { } origin || e.GetCurrentPoint(VideoFocus).Properties.IsLeftButtonPressed) return;
        _videoPress = null;
        var point = e.GetCurrentPoint(VideoFocus).Position;
        var areaPoint = e.GetCurrentPoint(VideoArea).Position;
        if (!PointerInputReady || IsMusicMode || point.X < 0 || point.Y < 0 || point.X >= VideoFocus.ActualWidth || point.Y >= VideoFocus.ActualHeight ||
            WindowDragPolicy.BeyondThreshold(areaPoint.X - origin.X, areaPoint.Y - origin.Y)) { CancelVideoClick(); return; }
        if (!IsPictureInPicture && _sidebar.WantsVisible && _settings.EdgeToEdgeVideo)
        { CancelVideoClick(); CloseLayoutSidebar(); return; }
        // The window moves when the second click enters fullscreen. Count a
        // third click at the same screen location in the original click series.
        if (!MusicPointerNative.GetCursorPos(out var screen)) { CancelVideoClick(); return; }
        long interval = PointerInputNative.GetDoubleClickTime();
        var action = _videoClicks.Release(Environment.TickCount64, screen.X, screen.Y, interval,
            PointerInputNative.GetSystemMetrics(36), PointerInputNative.GetSystemMetrics(37));
        _videoClickTimer.Stop();
        if (action == PointerClick.Double)
        {
            Trace("pointer-click", new { action = "fullscreen" });
            Owner?.ToggleFullscreen(); WakeOsc();
        }
        else if (action == PointerClick.PendingSingle)
        {
            _clickMedia = _snapshot is { } s ? (s.RequestId, s.PlaylistEntryId) : null;
            _videoClickTimer.Interval = TimeSpan.FromMilliseconds(interval);
            _videoClickTimer.Start();
        }
    }
    private void VideoSingleExpired(object? sender, object e)
    {
        _videoClickTimer.Stop();
        long now = Environment.TickCount64;
        if (!_videoClicks.Expire(now))
        {
            if (_videoClicks.Pending)
            { _videoClickTimer.Interval = TimeSpan.FromMilliseconds(_videoClicks.MillisecondsUntilExpiration(now)); _videoClickTimer.Start(); }
            return;
        }
        if (!PointerInputReady || IsMusicMode || IsPictureInPicture || _snapshot is not { } s || _clickMedia != (s.RequestId, s.PlaylistEntryId)) return;
        RefreshOscHolds(); _osc.Leave(Environment.TickCount64, AnimationsEnabled); UpdateOsc();
        HideVideoCursor();
        Trace("pointer-click", new { action = "hide-controls", protectedBy = _osc.Holds.ToString() });
    }
    private void PlayerWheelChanged(object? sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(VideoArea);
        // Keep the sequence across media/mode changes, including rejected input.
        if (!_wheelEvents.Accept(point.Timestamp))
        {
            e.Handled = true;
            Trace("pointer-wheel-replay", new { point.Timestamp, point.FrameId });
            return;
        }
        if (!PointerInputReady || _snapshot is not { } snapshot) return;
        var target = ReferenceEquals(sender, MainTimeline) || ReferenceEquals(sender, MusicTimeline) ? WheelTarget.Timeline :
            ReferenceEquals(sender, MainVolume) || ReferenceEquals(sender, MusicVolume) || ReferenceEquals(sender, MusicVolumeButton) || ReferenceEquals(sender, MusicVolumeContent) ? WheelTarget.Volume : WheelTarget.Video;
        if (sender is Control { IsEnabled: false }) return;
        bool horizontal = point.Properties.IsHorizontalMouseWheel;
        if (WheelZoom.Magnification(target, horizontal,
            e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Control), point.Properties.MouseWheelDelta) is { } magnification)
        {
            e.Handled = true;
            CancelVideoClick();
            var frame = CanChangeWindowSize && !_windowDragging ? Owner?.ZoomWindow(magnification) : null;
            Trace("pointer-window-zoom", new { magnification, point.Timestamp, point.FrameId, frame });
            return;
        }
        var change = WheelChange.From(target, horizontal, point.Properties.MouseWheelDelta);
        if (change.Seek != 0 && snapshot.Duration <= 0) return;
        e.Handled = true;
        CancelVideoClick();
        _wheelInput.Add(snapshot.RequestId, snapshot.PlaylistEntryId, change);
        RequestInputDrain();
        if (IsMusicMode && target == WheelTarget.Video && change.Volume != 0) ShowAutomaticMusicVolume(timed: true);
        Trace("pointer-wheel", new { target = target.ToString(), horizontal, delta = point.Properties.MouseWheelDelta, point.Timestamp, point.FrameId, change.Seek, change.Volume, snapshot.RequestId });
    }
    private async Task ApplyWheelInputAsync()
    {
        if (!PointerInputReady || _session is null || _snapshot is not { } snapshot) { _wheelInput.Clear(); return; }
        var change = _wheelInput.Take(snapshot.RequestId, snapshot.PlaylistEntryId);
        int revision = _playbackRevision;
        await ApplyWheelChangeAsync(change, snapshot, () => PointerInputReady && revision == _playbackRevision);
    }
    private async Task ApplyWheelChangeAsync(WheelChange change, Mani.Playback.PlaybackSnapshot snapshot, Func<bool> Current)
    {
        if (_session is null) return;
        if (change.Volume != 0)
        {
            // Read the engine's current value after earlier wheel/slider commands,
            // instead of repeatedly adding to a stale 200 ms UI snapshot.
            var values = await _session.GetPropertiesAsync("volume");
            if (!Current()) return;
            if (double.TryParse(values.GetValueOrDefault("volume"), NumberStyles.Float, CultureInfo.InvariantCulture, out double volume))
            {
                double next = Math.Clamp(volume + change.Volume, 0, _settings.Media.VolumeLimit);
                await _session.SetVolumeAsync(next);
                _uiCache.InvalidateVolume();
                Trace("pointer-wheel-applied", new { action = "volume", delta = change.Volume, target = next, snapshot.RequestId });
            }
        }
        if (change.Seek != 0 && Current())
        {
            await _session.CommandAsync("seek", change.Seek.ToString(CultureInfo.InvariantCulture), "relative");
            if (Current()) _osdObserver.SeekCompleted(snapshot);
            Trace("pointer-wheel-applied", new { action = "seek", delta = change.Seek, snapshot.RequestId });
        }
    }
    private void StopPointerInput()
    { ResetMediaPointerInput(); StopTrackpad(); _videoClickTimer.Tick -= VideoSingleExpired; _windowDragTimer.Tick -= WindowDragTick; }
    private void ResetMediaPointerInput()
    {
        ResetTrackpad();
        _quickFeedback = null;
        _quickValues = new Dictionary<string, string>();
        ClearSubtitleTrackNotice();
        CancelDelayEdits();
        _observedDelays.Clear();
        EndWindowDrag();
        CancelVideoClick();
        _wheelInput.Clear();
    }
}

internal static class PointerInputNative
{
    [DllImport("user32.dll")] internal static extern uint GetDoubleClickTime();
    [DllImport("user32.dll")] internal static extern int GetSystemMetrics(int index);
}

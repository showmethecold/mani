// IINA main-window cursor inactivity, with element-local WinUI cursor lifetime.
// Copyright (c) 2016-2026 lhc and contributors. GPL-3.0; licenses/IINA-GPL-3.0.txt.
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;

namespace Mani_App;

public sealed partial class MainPage
{
    private readonly CursorIdleState _cursor = new();
    private readonly DispatcherTimer _cursorTimer = new();
    private bool _cursorOverVideo, _cursorHidden, _cursorUnavailable;
    private bool _cursorWindowActive = true;
    private MainWindow? _cursorOwner;
    private void InitializeCursor()
    {
        Loaded += CursorLoaded;
        VideoFocus.AddHandler(PointerEnteredEvent, new PointerEventHandler(CursorEntered), true);
        VideoFocus.AddHandler(PointerMovedEvent, new PointerEventHandler(CursorEntered), true);
        VideoFocus.AddHandler(PointerExitedEvent, new PointerEventHandler(CursorExited), true);
        _cursorTimer.Tick += CursorTick;
    }
    private void CursorLoaded(object sender, RoutedEventArgs e)
    {
        if (_cursorOwner is not null || Owner is null) return;
        _cursorOwner = Owner;
        _cursorOwner.Activated += CursorWindowActivated;
    }
    private void CursorWindowActivated(object sender, WindowActivatedEventArgs e)
    {
        _cursorWindowActive = e.WindowActivationState != WindowActivationState.Deactivated;
        if (!_cursorWindowActive) CancelDelayEdits();
        _cursor.Wake(Environment.TickCount64);
        UpdateCursor();
    }
    private void CursorEntered(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType != PointerDeviceType.Mouse) return;
        _cursorOverVideo = true;
        if (MusicPointerNative.GetCursorPos(out var point)) _cursor.Move(point.X, point.Y, Environment.TickCount64);
        UpdateCursor();
    }
    private void CursorExited(object sender, PointerRoutedEventArgs e)
    { _cursorOverVideo = false; UpdateCursor(); }
    private void CursorTick(object? sender, object e) => UpdateCursor();
    private void UpdateCursor()
    {
        if (_closing || _cursorUnavailable || !IsLoaded) return;
        long now = Environment.TickCount64;
        _cursor.Configure((long)Math.Round(_settings.ControlBarAutoHideTimeout * 1000), now);
        bool eligible = _cursorWindowActive && _surface.HasMedia && !_uiOpening && !_oscSuppressed && !IsMusicMode && !IsPictureInPicture &&
            _cursorOverVideo && _videoPress is null && Owner is { } owner && WindowDragNative.GetForegroundWindow() == WinRT.Interop.WindowNative.GetWindowHandle(owner) &&
            (_osc.Holds & ~OscHold.Docked) == OscHold.None;
        _cursor.Eligible(eligible, now);
        _cursor.Tick(now);
        bool hidden = _cursor.Hidden;
        try { VideoFocus.HidePointer(hidden); }
        catch (Exception error)
        {
            _cursorUnavailable = true; _cursor.Reset(); _cursorTimer.Stop();
            VideoFocus.RestorePointer();
            Trace("cursor-error", new { error.Message });
            return;
        }
        if (_cursorHidden != hidden)
        {
            _cursorHidden = hidden;
            Trace("cursor-visibility", new { hidden = _cursorHidden, eligible, delay = _settings.ControlBarAutoHideTimeout });
        }
        if (_cursor.Waiting) { _cursorTimer.Interval = TimeSpan.FromMilliseconds(_cursor.Remaining(now)); _cursorTimer.Start(); }
        else _cursorTimer.Stop();
    }
    private void HideVideoCursor() { UpdateCursor(); _cursor.HideNow(); UpdateCursor(); }
    private void RecallCursor() { _cursor.Wake(Environment.TickCount64); UpdateCursor(); }
    private void StopCursor()
    {
        Loaded -= CursorLoaded;
        if (_cursorOwner is not null) _cursorOwner.Activated -= CursorWindowActivated;
        _cursorOwner = null;
        _cursorTimer.Stop(); _cursorTimer.Tick -= CursorTick;
        _cursor.Reset(); VideoFocus.ReleaseCursors();
    }
}

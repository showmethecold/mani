// Windows boundary for IINA NSWindow.performDrag; retain caption, HWND and renderer.
// Copyright (c) 2016-2026 lhc and contributors. GPL-3.0; licenses/IINA-GPL-3.0.txt.
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace Mani_App;

public sealed partial class MainWindow
{
    private WindowFrame? _videoDragStart, _videoDragCancel;
    private SizingNative.Point _videoDragPointer;
    private bool _videoDragMaximized;

    internal bool BeginVideoWindowDrag()
    {
        if (_closing || _changingPresenter || IsFullscreen || SizingNative.IsIconic(WindowHandle) ||
            !WindowDragNative.LeftDown || !MusicPointerNative.GetCursorPos(out var point)) return false;
        var before = ReadFrame();
        _videoDragMaximized = SizingNative.IsZoomed(WindowHandle);
        _videoDragCancel = _videoDragMaximized ? _windowedFrame : before;
        if (_videoDragMaximized && AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.Restore();
            var restored = ReadFrame();
            // Keep the grabbed part of the video under the pointer after restore.
            var next = restored with { X = point.X - restored.Width * Math.Clamp((point.X - before.X) / before.Width, 0, 1),
                Y = point.Y - restored.Height * Math.Clamp((point.Y - before.Y) / before.Height, 0, 1) };
            MovePictureFrame(WindowSizing.Constrain(next, SizingMetrics().Work));
        }
        _videoDragStart = ReadFrame();
        _videoDragPointer = point;
        return true;
    }
    internal bool ContinueVideoWindowDrag()
    {
        if (_closing || IsFullscreen || !WindowDragNative.LeftDown ||
            WindowDragNative.GetForegroundWindow() != WindowHandle || _videoDragStart is not { } start ||
            !MusicPointerNative.GetCursorPos(out var point)) return false;
        var next = WindowDragPolicy.Position(start.X, start.Y, _videoDragPointer.X, _videoDragPointer.Y, point.X, point.Y);
        if (AppWindow.Position.X != next.X || AppWindow.Position.Y != next.Y) AppWindow.Move(new PointInt32(next.X, next.Y));
        return true;
    }
    internal void EndVideoWindowDrag(bool cancel)
    {
        if (_videoDragStart is null) return;
        _videoDragStart = null;
        if (_closing) return;
        if (cancel && _videoDragCancel is { } original)
        {
            MovePictureFrame(original);
            if (_videoDragMaximized && AppWindow.Presenter is OverlappedPresenter presenter) presenter.Maximize();
        }
        _videoDragCancel = null;
        RememberWindowFrame();
    }
}

internal static class WindowDragNative
{
    internal static bool LeftDown => (GetAsyncKeyState(1) & 0x8000) != 0;
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
}

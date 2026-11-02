// IINA MainWindowController sizing/fullscreen rules, adapted to Windows HWNDs.
// Copyright (c) 2016-2026 lhc and contributors. GPL-3.0; licenses/IINA-GPL-3.0.txt.
using System.ComponentModel;
using System.Runtime.InteropServices;
using Mani.Playback;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace Mani_App;

public sealed partial class MainWindow
{
    private VideoSize? _sizingVideo;
    private PlayerSettings _sizingSettings = new();
    private double _dockedHeight, _sidebarWidth;
    private WindowFrame? _windowedFrame, _pendingFrame;
    private uint _windowedStyle, _windowedExStyle;
    private bool _changingPresenter, _applyingFrame, _windowedMaximized;
    private AppWindowPresenter? _windowedPresenter;
    private SizingNative.SubclassProc? _sizingCallback;
    private nint WindowHandle => WinRT.Interop.WindowNative.GetWindowHandle(this);
    // Presenter state can lag WM_SIZE. Never cache the minimized icon rectangle
    // or a maximized frame as the normal window's non-client measurements.
    private bool IsRestored => !IsMusicMode && !IsPictureInPicture && !IsFullscreen && !SizingNative.IsIconic(WindowHandle) && !SizingNative.IsZoomed(WindowHandle) &&
        AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Restored };
    internal bool CanSetWindowSize => IsRestored && !_closing;

    private void InitializeWindowSizing()
    {
        _windowedStyle = unchecked((uint)SizingNative.GetWindowLongPtrW(WindowHandle, -16).ToInt64());
        _windowedExStyle = unchecked((uint)SizingNative.GetWindowLongPtrW(WindowHandle, -20).ToInt64());
        _sizingCallback = SizingWindowProcedure;
        if (!SizingNative.SetWindowSubclass(WindowHandle, _sizingCallback, 0x4d33, 0)) throw new Win32Exception();
        RememberWindowFrame();
        AppWindow.Changed += (_, _) =>
        {
            if (_closing || _changingPresenter || _applyingFrame || !IsRestored) return;
            ApplyPendingFrame();
            RememberWindowFrame();
        };
    }
    internal void ConfigureWindowSizing(VideoSize? video, PlayerSettings settings, double dockedHeight, double sidebarWidth = 0)
    { _sizingVideo = video; _sizingSettings = settings; _dockedHeight = dockedHeight; _sidebarWidth = sidebarWidth; }

    private WindowFrame ReadFrame()
    {
        SizingNative.GetWindowRect(WindowHandle, out var rect);
        return rect.Frame;
    }
    private void RememberWindowFrame()
    {
        if (!IsRestored) return;
        var frame = ReadFrame();
        SizingNative.GetClientRect(WindowHandle, out var client);
        var chrome = WindowChrome();
        // HWND frame and client notifications can arrive at different points in a
        // minimize/restore transition. Only save a consistent normal frame.
        if (Math.Abs(frame.Width - client.Frame.Width - chrome.Width) > 2 ||
            Math.Abs(frame.Height - client.Frame.Height - chrome.Height) > 2) return;
        _windowedFrame = frame;
    }
    private VideoSize WindowChrome()
    {
        var rect = new SizingNative.Rect { Right = 100, Bottom = 100 };
        if (!SizingNative.AdjustWindowRectExForDpi(ref rect, _windowedStyle, false, _windowedExStyle,
            Math.Max(96, SizingNative.GetDpiForWindow(WindowHandle)))) throw new Win32Exception();
        return new(rect.Frame.Width - 100, rect.Frame.Height - 100);
    }
    private (WindowFrame Work, VideoSize Chrome, double Scale) SizingMetrics()
    {
        var monitor = new SizingNative.MonitorInfo { Size = Marshal.SizeOf<SizingNative.MonitorInfo>() };
        if (!SizingNative.GetMonitorInfoW(SizingNative.MonitorFromWindow(WindowHandle, 2), ref monitor)) throw new Win32Exception();
        double scale = Math.Max(96, SizingNative.GetDpiForWindow(WindowHandle)) / 96.0;
        var chrome = WindowChrome();
        return (monitor.Work.Frame, new(chrome.Width + _sidebarWidth * scale, chrome.Height + _dockedHeight * scale), scale);
    }
    internal WindowFrame? FitWindowToVideo()
    {
        if (!CanSetWindowSize || _sizingVideo is not { } video || (IsPictureInPicture || WindowSizing.AspectLocked(_sizingSettings))) return null;
        var metrics = SizingMetrics();
        var frame = WindowSizing.FitVideo(ReadFrame(), video, metrics.Chrome, _sidebarWidth * metrics.Scale);
        _pendingFrame = frame;
        ApplyPendingFrame();
        return frame;
    }
    internal WindowFrame? ResizeForVideo(bool initial, bool started, bool opened, bool keepSize = false, ResizeWindowOption? explicitSize = null)
    {
        if (_sizingVideo is not { } video || _closing) return null;
        if (IsMusicMode) return null;
        if (IsPictureInPicture) { UpdatePictureInPictureAspect(); return null; }
        if (explicitSize is null && !WindowSizing.AspectLocked(_sizingSettings) && !opened) return null;
        if (explicitSize is not null && !CanSetWindowSize) return null;
        RememberWindowFrame();
        var metrics = SizingMetrics();
        var frame = WindowSizing.Calculate(video, _pendingFrame ?? _windowedFrame ?? ReadFrame(), metrics.Work,
            metrics.Chrome, metrics.Scale, _sizingSettings, initial, started, opened, keepSize, explicitSize);
        _pendingFrame = frame;
        if (IsRestored) ApplyPendingFrame();
        return frame;
    }
    internal WindowFrame? StepWindowSize(int direction)
    {
        if (!CanSetWindowSize || _sizingVideo is not { } video) return null;
        var metrics = SizingMetrics();
        var frame = WindowSizing.Step(video, ReadFrame(), metrics.Work, metrics.Chrome, metrics.Scale,
            WindowSizing.AspectLocked(_sizingSettings), direction);
        _pendingFrame = frame;
        ApplyPendingFrame();
        return frame;
    }
    private void ApplyPendingFrame()
    {
        if (_pendingFrame is not { } frame || !IsRestored) return;
        _pendingFrame = null;
        var metrics = SizingMetrics();
        frame = WindowSizing.Constrain(frame, metrics.Work);
        _applyingFrame = true;
        try
        {
            AppWindow.MoveAndResize(new RectInt32((int)Math.Round(frame.X), (int)Math.Round(frame.Y),
                Math.Max(1, (int)Math.Round(frame.Width)), Math.Max(1, (int)Math.Round(frame.Height))));
        }
        finally { _applyingFrame = false; }
        RememberWindowFrame();
    }
    internal WindowFrame? BeginWindowZoom() => CanSetWindowSize ? ReadFrame() : null;
    internal WindowFrame? ZoomWindow(double magnification, WindowFrame? anchor = null)
    {
        if (!CanSetWindowSize || _sizingVideo is null) return null;
        var metrics = SizingMetrics();
        var frame = WindowSizing.Zoom(ReadFrame(), metrics.Work, metrics.Chrome, metrics.Scale, magnification, anchor);
        if (frame is null) return null;
        _pendingFrame = frame;
        ApplyPendingFrame();
        return frame;
    }
    private void ToggleSizingFullscreen()
    {
        bool enter = !IsFullscreen;
        _changingPresenter = true;
        try
        {
            if (enter)
            {
                RememberWindowFrame();
                _windowedPresenter = AppWindow.Presenter;
                _windowedMaximized = _windowedPresenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
                AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
            }
            else if (_windowedPresenter is { } presenter) AppWindow.SetPresenter(presenter);
            else AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
            if (!enter && _windowedMaximized && AppWindow.Presenter is OverlappedPresenter restored)
                restored.Maximize();
        }
        finally { _changingPresenter = false; }
        if (!enter) { ApplyPendingFrame(); RememberWindowFrame(); }
    }
    private nint SizingWindowProcedure(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
        // No exception may cross a native window-procedure boundary.
        try
        {
            if (message == 0x82) SizingNative.RemoveWindowSubclass(hwnd, _sizingCallback!, id); // WM_NCDESTROY
            if (!_closing && !_changingPresenter && IsMusicMode && message == 0x214)
            {
                var metrics = SizingMetrics();
                var rect = Marshal.PtrToStructure<SizingNative.Rect>(lParam);
                var resized = MusicModeLayout.Size(rect.Frame, _musicVideo, _musicArtwork, metrics.Work, WindowChrome(), metrics.Scale, (int)wParam, MusicBarHeight);
                Marshal.StructureToPtr(SizingNative.Rect.From(resized), lParam, false);
                return 1;
            }
            if (!_closing && !_changingPresenter && !IsFullscreen && message == 0x214 && _sizingVideo is { } video && (IsPictureInPicture || WindowSizing.AspectLocked(_sizingSettings)))
            {
                RememberWindowFrame();
                var metrics = SizingMetrics();
                var rect = Marshal.PtrToStructure<SizingNative.Rect>(lParam);
                var resized = WindowSizing.Drag(rect.Frame, (int)wParam, video, metrics.Chrome, metrics.Work, metrics.Scale);
                Marshal.StructureToPtr(SizingNative.Rect.From(resized), lParam, false);
                return 1;
            }
            if (!_closing && !IsFullscreen && message == 0x24) // WM_GETMINMAXINFO
            {
                var info = Marshal.PtrToStructure<SizingNative.MinMaxInfo>(lParam);
                var metrics = SizingMetrics();
                if (IsMusicMode)
                {
                    var chrome = WindowChrome();
                    var minimumMusic = MusicModeLayout.Size(new(0, 0, 0, 0), _musicVideo, _musicArtwork, metrics.Work, chrome, metrics.Scale, barHeight: MusicBarHeight);
                    info.MinTrack = new((int)Math.Ceiling(minimumMusic.Width), (int)Math.Ceiling(minimumMusic.Height));
                    Marshal.StructureToPtr(info, lParam, false);
                    return 0;
                }
                var minimum = _sizingVideo is { } size && (IsPictureInPicture || WindowSizing.AspectLocked(_sizingSettings))
                    ? WindowSizing.Bound(new(size.Aspect, 1), metrics.Work, metrics.Chrome, metrics.Scale)
                    : new VideoSize(285 * metrics.Scale, 120 * metrics.Scale);
                // The sidebar itself shrinks in a narrow window; it must not raise
                // the native minimum width every time its edge is dragged.
                info.MinTrack = new((int)Math.Ceiling(minimum.Width + metrics.Chrome.Width - _sidebarWidth * metrics.Scale), (int)Math.Ceiling(minimum.Height + metrics.Chrome.Height));
                Marshal.StructureToPtr(info, lParam, false);
                return 0;
            }
        }
        catch (Exception error)
        {
            // Preserve native processing if the monitor disappears during sizing.
            System.Diagnostics.Debug.WriteLine(error);
        }
        return SizingNative.DefSubclassProc(hwnd, message, wParam, lParam);
    }
}

internal static class SizingNative
{
    internal delegate nint SubclassProc(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data);
    [StructLayout(LayoutKind.Sequential)] internal struct Rect
    {
        public int Left, Top, Right, Bottom;
        public readonly WindowFrame Frame => new(Left, Top, Right - Left, Bottom - Top);
        public static Rect From(WindowFrame frame) => new() { Left = (int)Math.Round(frame.X), Top = (int)Math.Round(frame.Y),
            Right = (int)Math.Round(frame.Right), Bottom = (int)Math.Round(frame.Bottom) };
    }
    [StructLayout(LayoutKind.Sequential)] internal struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] internal struct Point(int x, int y) { public int X = x, Y = y; }
    [StructLayout(LayoutKind.Sequential)] internal struct MinMaxInfo { public Point Reserved, MaxSize, MaxPosition, MinTrack, MaxTrack; }
    [DllImport("comctl32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetWindowSubclass(nint hwnd, SubclassProc proc, nuint id, nuint data);
    [DllImport("comctl32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool RemoveWindowSubclass(nint hwnd, SubclassProc proc, nuint id);
    [DllImport("comctl32.dll")] internal static extern nint DefSubclassProc(nint hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetClientRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsZoomed(nint hwnd);
    [DllImport("user32.dll")] internal static extern nint GetWindowLongPtrW(nint hwnd, int index);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool AdjustWindowRectExForDpi(ref Rect rect, uint style, [MarshalAs(UnmanagedType.Bool)] bool menu, uint exStyle, uint dpi);
    [DllImport("user32.dll")] internal static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);
}

// IINA switchToMiniPlayer/switchBackFromMiniPlayer and MiniPlayerWindow geometry.
// Copyright (c) 2016-2026 lhc and contributors. GPL-3.0; licenses/IINA-GPL-3.0.txt.
// Retain the native caption, HWND and video host; replace only the presenter/layout.
using Microsoft.UI.Windowing;

namespace Mani_App;

public sealed partial class MainWindow
{
    private bool _musicMode, _changingMusicMode, _musicReturnMaximized, _musicInitialized;
    private AppWindowPresenter? _musicReturnPresenter;
    private WindowFrame? _musicReturnFrame, _lastMusicFrame;
    private VideoSize _musicVideo = new(1, 1);
    private bool _musicArtwork = true;
    internal bool IsMusicMode => _musicMode;
    private double MusicBarHeight => ((MainPage)RootFrame.Content).MusicBarHeight;
    internal bool MusicPointerInside()
    {
        // PointerExited can carry the last point inside a child during a flyout
        // transition. Use current client coordinates for the tracking-area boundary.
        return MusicPointerNative.GetCursorPos(out var point) && MusicPointerNative.ScreenToClient(WindowHandle, ref point) &&
            SizingNative.GetClientRect(WindowHandle, out var rect) && point.X >= 0 && point.Y >= 0 && point.X < rect.Right && point.Y < rect.Bottom;
    }

    internal void EnterMusicMode(VideoSize video, bool artwork)
    {
        if (_closing || _changingMusicMode || _musicMode || IsPictureInPicture) return;
        RememberWindowFrame();
        _musicReturnPresenter = AppWindow.Presenter;
        _musicReturnFrame = _windowedFrame ?? ReadFrame();
        _musicReturnMaximized = AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized } || IsFullscreen && _windowedMaximized;
        _changingMusicMode = _changingPresenter = true;
        _musicVideo = video; _musicArtwork = artwork; _musicMode = true;
        try
        {
            var presenter = OverlappedPresenter.Create();
            presenter.IsMaximizable = false;
            AppWindow.SetPresenter(presenter);
            presenter.Restore();
            ((MainPage)RootFrame.Content).MusicModeChanged();
            if (!_musicInitialized && Floating.Automatic) Floating.SetManual(true);
            _musicInitialized = true;
            ApplyWindowFloating();
            var metrics = SizingMetrics();
            MovePictureFrame(_lastMusicFrame is { } saved
                ? MusicModeLayout.Size(saved, video, artwork, metrics.Work, WindowChrome(), metrics.Scale, barHeight: MusicBarHeight)
                : MusicModeLayout.Initial(_musicReturnFrame.Value, video, artwork, metrics.Work, WindowChrome(), metrics.Scale, MusicBarHeight));
        }
        catch
        {
            _musicMode = false;
            RestoreMusicPresenter();
            ((MainPage)RootFrame.Content).MusicModeChanged();
            throw;
        }
        finally { _changingMusicMode = _changingPresenter = false; }
    }
    internal void ExitMusicMode()
    {
        if (!_musicMode || _changingMusicMode) return;
        if (!SizingNative.IsIconic(WindowHandle)) _lastMusicFrame = ReadFrame();
        _changingMusicMode = _changingPresenter = true;
        try
        {
            _musicMode = false;
            RestoreMusicPresenter();
            ((MainPage)RootFrame.Content).MusicModeChanged();
            ApplyWindowFloating();
        }
        finally { _changingMusicMode = _changingPresenter = false; }
        RememberWindowFrame();
    }
    private void RestoreMusicPresenter()
    {
        void Placement(OverlappedPresenter presenter)
        {
            presenter.Restore();
            if (_musicReturnFrame is { } frame) MovePictureFrame(WindowSizing.Constrain(frame, SizingMetrics().Work));
            if (_musicReturnMaximized) presenter.Maximize();
        }
        if (_musicReturnPresenter?.Kind == AppWindowPresenterKind.FullScreen && _windowedPresenter is OverlappedPresenter windowed)
        { AppWindow.SetPresenter(windowed); Placement(windowed); }
        AppWindow.SetPresenter(_musicReturnPresenter!);
        if (_musicReturnPresenter is OverlappedPresenter normal) Placement(normal);
    }
    internal void UpdateMusicGeometry(VideoSize video, bool artwork)
    {
        _musicVideo = video; _musicArtwork = artwork;
        if (!_musicMode || _changingMusicMode || SizingNative.IsIconic(WindowHandle)) return;
        var metrics = SizingMetrics();
        var frame = ReadFrame();
        var next = MusicModeLayout.Size(frame, video, artwork, metrics.Work, WindowChrome(), metrics.Scale, barHeight: MusicBarHeight);
        if (Math.Abs(next.Width - frame.Width) >= 1 || Math.Abs(next.Height - frame.Height) >= 1) MovePictureFrame(next);
    }
}

internal static class MusicPointerNative
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    internal static extern bool GetCursorPos(out SizingNative.Point point);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    internal static extern bool ScreenToClient(nint hwnd, ref SizingNative.Point point);
}

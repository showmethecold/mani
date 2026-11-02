// IINA MainWindowController enterPIP/prepareForPIPClosure/doneExitingPIP port.
// Copyright (c) 2016-2026 lhc and contributors. GPL-3.0; licenses/IINA-GPL-3.0.txt.
// The Windows presenter changes without replacing the HWND, session or video host.
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace Mani_App;

public sealed partial class MainWindow
{
    private bool _pictureInPicture, _changingPictureInPicture;
    private AppWindowPresenter? _pipReturnPresenter;
    private WindowFrame? _pipReturnFrame;
    private bool _pipReturnMaximized;
    internal bool IsPictureInPicture => _pictureInPicture;

    internal void EnterPictureInPicture()
    {
        if (_closing || _changingPictureInPicture || _pictureInPicture || _sizingVideo is not { } video) return;
        RememberWindowFrame();
        _pipReturnPresenter = AppWindow.Presenter;
        _pipReturnFrame = _windowedFrame ?? ReadFrame();
        _pipReturnMaximized = AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized } ||
            IsFullscreen && _windowedMaximized;
        _changingPictureInPicture = _changingPresenter = true;
        _pictureInPicture = true;
        try
        {
            var presenter = OverlappedPresenter.Create();
            presenter.IsAlwaysOnTop = true;
            presenter.IsMinimizable = presenter.IsMaximizable = false;
            presenter.IsResizable = true;
            AppWindow.SetPresenter(presenter);
            // A new presenter can inherit the HWND's maximized placement.
            presenter.Restore();
            ((MainPage)RootFrame.Content).PictureInPictureChanged();
            var metrics = SizingMetrics();
            MovePictureFrame(PictureInPictureLayout.Initial(video, metrics.Work, WindowChrome(), metrics.Scale));
        }
        catch
        {
            _pictureInPicture = false;
            RestorePicturePresenter();
            RestorePictureFrame();
            ((MainPage)RootFrame.Content).PictureInPictureChanged();
            throw;
        }
        finally { _changingPictureInPicture = _changingPresenter = false; }
    }

    internal void ExitPictureInPicture()
    {
        if (_closing || _changingPictureInPicture || !_pictureInPicture || _pipReturnPresenter is null) return;
        _changingPictureInPicture = _changingPresenter = true;
        try
        {
            RestorePicturePresenter();
            _pictureInPicture = false;
            RestorePictureFrame();
            ((MainPage)RootFrame.Content).PictureInPictureChanged();
            ApplyWindowFloating();
            Activate();
        }
        finally { _changingPictureInPicture = _changingPresenter = false; }
        RememberWindowFrame();
    }

    private void RestorePictureFrame()
    {
        if (_pipReturnPresenter is not OverlappedPresenter presenter) return;
        RestorePicturePlacement(presenter);
    }

    private void RestorePicturePresenter()
    {
        // Fullscreen also uses the HWND's normal placement on its next exit.
        // Re-establish that placement before restoring the fullscreen presenter.
        if (_pipReturnPresenter?.Kind == AppWindowPresenterKind.FullScreen && _windowedPresenter is OverlappedPresenter windowed)
        {
            AppWindow.SetPresenter(windowed);
            RestorePicturePlacement(windowed);
        }
        AppWindow.SetPresenter(_pipReturnPresenter!);
    }

    private void RestorePicturePlacement(OverlappedPresenter presenter)
    {
        presenter.Restore();
        if (_pipReturnFrame is { } frame) MovePictureFrame(WindowSizing.Constrain(frame, SizingMetrics().Work));
        if (_pipReturnMaximized) presenter.Maximize();
    }

    private void MovePictureFrame(WindowFrame frame) => AppWindow.MoveAndResize(new RectInt32(
        (int)Math.Round(frame.X), (int)Math.Round(frame.Y), (int)Math.Round(frame.Width), (int)Math.Round(frame.Height)));

    internal void UpdatePictureInPictureAspect()
    {
        if (!_pictureInPicture || _changingPictureInPicture || _sizingVideo is not { } video) return;
        var metrics = SizingMetrics();
        MovePictureFrame(WindowSizing.Constrain(WindowSizing.Drag(ReadFrame(), 2, video, WindowChrome(), metrics.Work, metrics.Scale), metrics.Work));
    }
}

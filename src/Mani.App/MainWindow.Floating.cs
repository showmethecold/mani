// Windows adapter for IINA NSWindow.level and fullscreen on-top restoration.
// Copyright (c) 2016-2026 lhc and contributors. GPL-3.0; licenses/IINA-GPL-3.0.txt.
using Microsoft.UI.Windowing;

namespace Mani_App;

public sealed partial class MainWindow
{
    private readonly WindowFloatingState _mainFloating = new(), _musicFloating = new();
    internal WindowFloatingState Floating => IsMusicMode ? _musicFloating : _mainFloating;
    internal bool? NativeFloating => AppWindow.Presenter is OverlappedPresenter presenter ? presenter.IsAlwaysOnTop : null;
    internal void ObserveWindowFloating(FloatingPlayback playback, long request, long entry)
    {
        if (_closing) return;
        Floating.Observe(playback, request, entry);
        ApplyWindowFloating();
    }
    internal void SetWindowFloating(bool value)
    {
        if (_closing) return;
        Floating.SetManual(value);
        ApplyWindowFloating();
    }
    internal void SetAutomaticFloating(bool value)
    {
        if (_closing) return;
        _mainFloating.SetAutomatic(value);
        _musicFloating.SetAutomatic(value);
        ApplyWindowFloating();
    }
    private void ApplyWindowFloating()
    {
        if (!IsPictureInPicture && AppWindow.Presenter is OverlappedPresenter presenter && presenter.IsAlwaysOnTop != Floating.Applied)
            presenter.IsAlwaysOnTop = Floating.Applied;
        (RootFrame.Content as MainPage)?.RefreshWindowFloating();
    }
}

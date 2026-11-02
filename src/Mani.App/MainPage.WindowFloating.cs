// IINA window menu/alwaysFloatOnTop preference port using accepted playback state.
// Copyright (c) 2016-2026 lhc and contributors. GPL-3.0; licenses/IINA-GPL-3.0.txt.
using Mani.Playback;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Mani_App;

public sealed partial class MainPage
{
    private bool? _pendingFloating;
    private string _lastFloatingEvidence = "";
    private void ObserveWindowFloating(PlaybackSnapshot? snapshot)
    {
        FloatingPlayback playback = _surface.Surface switch
        {
            PlayerSurface.Loading => FloatingPlayback.Loading,
            PlayerSurface.Media => snapshot!.Paused ? FloatingPlayback.Paused : FloatingPlayback.Playing,
            _ => FloatingPlayback.Empty
        };
        Owner?.ObserveWindowFloating(playback, snapshot?.RequestId ?? 0, snapshot?.PlaylistEntryId ?? 0);
    }
    internal void RefreshWindowFloating()
    {
        if (Owner is not { } window) return;
        var state = window.Floating;
        AlwaysOnTopOption.IsChecked = state.Requested;
        AlwaysOnTopOption.IsEnabled = !_closing && !_m1Busy && state.CanSetManual && !IsPictureInPicture;
        string evidence = $"{state.Requested}/{state.Applied}/{state.Automatic}/{state.Fullscreen}/{state.Playback}/{window.NativeFloating}";
        if (evidence == _lastFloatingEvidence) return;
        _lastFloatingEvidence = evidence;
        Trace("window-floating", new { requested = state.Requested, applied = state.Applied,
            automatic = state.Automatic, fullscreen = state.Fullscreen, playback = state.Playback.ToString(), native = window.NativeFloating });
    }
    private void WindowFloating_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleMenuFlyoutItem item || _closing) return;
        if (ReferenceEquals(item, AlwaysOnTopOption)) _pendingFloating = item.IsChecked;
        MainMenu.Hide();
    }
    private void ApplyPendingWindowFloating()
    {
        if (_closing) return;
        if (_pendingFloating is { } floating)
        {
            _pendingFloating = null;
            Owner?.SetWindowFloating(floating);
        }
        RefreshWindowFloating();
    }
}

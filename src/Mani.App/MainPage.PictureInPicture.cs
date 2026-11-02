// IINA MainWindowMenuActions.menuTogglePIP and MainWindowController PIP controls.
// Copyright (c) 2016-2026 lhc and contributors. GPL-3.0; licenses/IINA-GPL-3.0.txt.
using Mani.Playback;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Mani_App;

public sealed partial class MainPage
{
    private bool _pendingPictureInPicture;
    private bool IsPictureInPicture => Owner?.IsPictureInPicture == true;
    private bool CanEnterPictureInPicture => !IsMusicMode && !_closing && !_m1Busy && _surface.HasMedia && _videoSize is not null;
    private OscPosition EffectiveOscPosition => IsPictureInPicture ? OscPosition.Floating : _settings.OscPosition;

    private void PictureInPicture_Click(object sender, RoutedEventArgs e)
    {
        if (_closing || _m1Busy || IsMusicMode) return;
        if (_menuOpen) { _pendingPictureInPicture = true; MainMenu.Hide(); }
        else TogglePictureInPicture();
    }
    private void ApplyPendingPictureInPicture()
    {
        if (!_pendingPictureInPicture || _closing) return;
        _pendingPictureInPicture = false;
        TogglePictureInPicture();
    }
    private void TogglePictureInPicture()
    {
        try
        {
            if (IsPictureInPicture) Owner?.ExitPictureInPicture();
            else if (_surface.HasMedia && _videoSize is not null && !_m1Busy) Owner?.EnterPictureInPicture();
        }
        catch (Exception error) { ShowError(error); }
    }
    private void RefreshPictureInPictureMenu()
    {
        PictureInPictureMenu.Text = UiText.Get(IsPictureInPicture ? "ReturnToMainWindow" : "PictureInPicture");
        PictureInPictureMenu.IsEnabled = !IsMusicMode && !_closing && !_m1Busy && (IsPictureInPicture || CanEnterPictureInPicture);
        PipToolbarButton.IsEnabled = CanEnterPictureInPicture;
        ControlBarMenu.IsEnabled = !IsPictureInPicture && !IsMusicMode;
    }
    internal void PictureInPictureChanged()
    {
        CancelDelayEdits();
        EndWindowDrag();
        _dragOrigin = null; OscDragHandle.ReleasePointerCaptures();
        CommitSeek(); Timeline.ReleasePointerCaptures(); Volume.ReleasePointerCaptures();
        _oscPointers.Clear(); _adjustingVolume = _oscHover = false;
        if (IsPictureInPicture)
        {
            EndSidebarDrag(); EndSidebarWidthEdit(); _sidebarFocusPending = false;
            LabPanel.Visibility = InfoPanel.Visibility = Visibility.Collapsed;
        }
        _arrangedOscPosition = null;
        UpdateLayoutSidebar();
        LayoutOsc();
        UpdateActionMenu();
        VideoFocus.Focus(_keyboardNavigation ? FocusState.Keyboard : FocusState.Pointer);
        WakeOsc();
        Trace("picture-in-picture", new { active = IsPictureInPicture, fullscreen = Owner?.IsFullscreen,
            paused = _snapshot?.Paused, request = _snapshot?.RequestId, entry = _snapshot?.PlaylistEntryId,
            position = _snapshot?.Position, sidebar = _sidebar.WantsVisible, mode = _settings.OscPosition.ToString() });
    }
}

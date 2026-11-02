// IINA PlayerCore music metadata/track changes and MiniPlayerWindowController UI.
// Copyright (c) 2016-2026 lhc and contributors. GPL-3.0; licenses/IINA-GPL-3.0.txt.
using Mani.Playback;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;

namespace Mani_App;

public sealed partial class MainPage
{
    private readonly MusicModeState _musicState = new();
    private MusicMediaInfo? _musicInfo;
    private bool _pendingMusicMode, _musicArtworkVisible = true, _musicPreferencesLoaded, _musicPointerInside, _musicVolumeOpen, _musicControlsShown;
    private long _nextMusicRead;
    private (long Request, long Entry)? _musicMedia;
    private string _musicTrackSignature = "", _musicEvidence = "";
    private Storyboard? _musicFade;
    internal double MusicBarHeight { get; private set; } = MusicModeLayout.BarHeight;
    private bool IsMusicMode => Owner?.IsMusicMode == true;
    // Stable visual trees avoid WinUI dropping sibling UIA providers after reparenting.
    // Both presentations use the same handlers, UI cache and playback session.
    private Button PlayButton => IsMusicMode ? MusicPlayButton : MainPlayButton;
    private FontIcon PlayIcon => IsMusicMode ? MusicPlayIcon : MainPlayIcon;
    private Button SeekBackButton => IsMusicMode ? MusicSeekBackButton : MainSeekBackButton;
    private Button SeekForwardButton => IsMusicMode ? MusicSeekForwardButton : MainSeekForwardButton;
    private Button MuteButton => IsMusicMode ? MusicMuteButton : MainMuteButton;
    private FontIcon MuteIcon => IsMusicMode ? MusicMuteIcon : MainMuteIcon;
    private Slider Volume => IsMusicMode ? MusicVolume : MainVolume;
    private Slider Timeline => IsMusicMode ? MusicTimeline : MainTimeline;
    private TextBlock TimeLabel => IsMusicMode ? MusicTimeLabel : MainTimeLabel;
    private TextBlock DurationLabel => IsMusicMode ? MusicDurationLabel : MainDurationLabel;
    private void MusicMore_Click(object sender, RoutedEventArgs e) => MainMenu.ShowAt(MusicMoreButton);


    private void MusicMode_Click(object sender, RoutedEventArgs e)
    {
        if (_closing || _m1Busy || IsPictureInPicture) return;
        if (_menuOpen) { _pendingMusicMode = true; MainMenu.Hide(); }
        else SetMusicMode(!IsMusicMode, automatically: false);
    }
    private void MusicShortcut_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        e.Handled = ToggleMusicFromKeyboard();
    }
    private bool ToggleMusicFromKeyboard()
    {
        if (_closing || _pickerOpen || OwnsKeyboardInput() || IsPictureInPicture || !_surface.HasMedia) return false;
        _keyboardNavigation = true;
        SetMusicMode(!IsMusicMode, automatically: false);
        return true;
    }
    private void ApplyPendingMusicMode()
    {
        if (!_pendingMusicMode || _closing) return;
        _pendingMusicMode = false;
        SetMusicMode(!IsMusicMode, automatically: false);
    }
    internal void ReturnFromMusicMode() => SetMusicMode(false, automatically: false);
    private void SetMusicMode(bool active, bool automatically)
    {
        if (_closing || Owner is null || active == IsMusicMode || active && (!_surface.HasMedia || _musicInfo is null || IsPictureInPicture)) return;
        try
        {
            CommitSeek();
            foreach (var slider in new[] { MainTimeline, MusicTimeline, MainVolume, MusicVolume }) slider.ReleasePointerCaptures();
            if (!_musicPreferencesLoaded) { _musicPreferencesLoaded = true; _musicArtworkVisible = _settings.MusicModeShowAlbumArt; }
            if (active) Owner.EnterMusicMode(_musicInfo!.DisplaySize, _musicArtworkVisible);
            else Owner.ExitMusicMode();
            _musicState.Switch(IsMusicMode, automatically);
            // Presenter restoration can move native focus after MusicModeChanged.
            // Restore the client focus after that transition so the next shortcut works.
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_closing || _pickerOpen || _dialogOpen || _menuOpen) return;
                (IsMusicMode ? MusicReturnButton : VideoFocus).Focus(_keyboardNavigation ? FocusState.Keyboard : FocusState.Pointer);
            });
            Trace("music-mode", new { active = IsMusicMode, automatically, manualOverride = _musicState.ManualOverride,
                request = _snapshot?.RequestId, entry = _snapshot?.PlaylistEntryId, position = _snapshot?.Position, paused = _snapshot?.Paused });
        }
        catch (Exception error) { ShowError(error); }
    }
    private async Task ObserveMusicAsync(PlaybackSnapshot snapshot, int revision)
    {
        if (!_surface.HasMedia || _session is null || _m1Busy) return;
        var media = (snapshot.RequestId, snapshot.PlaylistEntryId);
        long now = Environment.TickCount64;
        if (_musicMedia == media && now < _nextMusicRead) return;
        _nextMusicRead = now + 1000;
        var properties = await _session.GetPropertiesAsync("path", "track-list", "chapters", "chapter", "metadata/by-key/album",
            "metadata/by-key/artist", "chapter-metadata/by-key/performer", "dwidth", "dheight", "vid", "video-params/rotate", "video-rotate",
            "sub-visibility", "sid", "secondary-sub-visibility", "secondary-sid");
        bool Current() => !_closing && !_uiOpening && revision == _playbackRevision && properties.GetValueOrDefault("path") == snapshot.Path;
        if (!Current()) return;
        string? chapterTitle = null;
        if (int.TryParse(properties.GetValueOrDefault("chapters"), out int chapters) && chapters > 0 &&
            int.TryParse(properties.GetValueOrDefault("chapter"), out int chapter) && chapter >= 0)
        {
            string key = $"chapter-list/{chapter}/title";
            var chapterProperties = await _session.GetPropertiesAsync(key);
            if (!Current()) return;
            chapterTitle = chapterProperties.GetValueOrDefault(key);
        }
        var info = MusicMediaInfo.Read(snapshot.Path, snapshot.Title, properties, chapterTitle);
        string signature = $"{snapshot.RequestId}/{snapshot.PlaylistEntryId}/{info.Kind}/{info.Tracks}";
        _musicMedia = media;
        _musicInfo = info;
        if (_musicTrackSignature != signature)
        {
            _musicTrackSignature = signature;
            if (_musicState.TrackListChanged(info.Kind, _settings.AutoSwitchToMusicMode, Owner?.IsFullscreen == true || IsPictureInPicture) is { } next)
                SetMusicMode(next, automatically: true);
        }
        if (IsMusicMode) Owner?.UpdateMusicGeometry(info.DisplaySize, _musicArtworkVisible);
        UpdateMusicView();
        RefreshMusicMenu();
        string evidence = $"{signature}/{info.Title}/{info.ArtistAlbum}/{info.DisplaySize}/{info.DefaultArtwork}";
        if (_musicEvidence != evidence)
        {
            _musicEvidence = evidence;
            Trace("music-media", new { request = snapshot.RequestId, kind = info.Kind.ToString(), info.DisplaySize, info.DefaultArtwork });
        }
    }
    private void RefreshMusicMenu()
    {
        MusicModeMenu.Text = UiText.Get(IsMusicMode ? "ExitMusicMode" : "MusicMode");
        MusicModeMenu.IsEnabled = !_closing && !_m1Busy && !IsPictureInPicture && _surface.HasMedia && _musicInfo is not null;
        MusicArtworkOption.Visibility = IsMusicMode ? Visibility.Visible : Visibility.Collapsed;
        MusicArtworkOption.IsChecked = _musicArtworkVisible;
        AutomationProperties.SetName(MusicArtworkButton, UiText.Get(_musicArtworkVisible ? "HideMusicArtwork" : "ShowMusicArtwork"));
        ToolTipService.SetToolTip(MusicArtworkButton, UiText.Get(_musicArtworkVisible ? "HideMusicArtwork" : "ShowMusicArtwork"));
    }
    private void MusicArtwork_Click(object sender, RoutedEventArgs e)
    {
        if (!IsMusicMode || _closing) return;
        _musicArtworkVisible = !_musicArtworkVisible;
        _settings = _settings with { MusicModeShowAlbumArt = _musicArtworkVisible };
        SaveSettings(); MainMenu.Hide();
        Owner?.UpdateMusicGeometry(_musicInfo?.DisplaySize ?? new(1, 1), _musicArtworkVisible);
        UpdateMusicView(); RefreshMusicMenu();
        Trace("music-artwork", new { visible = _musicArtworkVisible, _musicInfo?.DefaultArtwork });
    }
    private void MusicVolume_Opened(object sender, object e) { _musicVolumeOpen = true; UpdateMusicControls(); }
    private void MusicVolume_Closed(object sender, object e) { _musicVolumeOpen = false; _automaticMusicVolume = false; _musicVolumeHideTimer.Stop(); UpdateMusicControls(); }

    internal void MusicModeChanged()
    {
        HideAutomaticMusicVolume();
        CancelDelayEdits();
        EndWindowDrag();
        CommitSeek(); Timeline.ReleasePointerCaptures(); Volume.ReleasePointerCaptures();
        _dragOrigin = null; OscDragHandle.ReleasePointerCaptures();
        EndSidebarDrag(); EndSidebarWidthEdit(); _sidebarFocusPending = false;
        _oscPointers.Clear(); _adjustingVolume = _oscHover = false;
        MusicVolumeFlyout.Hide(); ResetOsd();
        LabPanel.Visibility = InfoPanel.Visibility = Visibility.Collapsed;
        _uiCache.Invalidate(); _displayedTime = null;
        Volume.IsEnabled = _session is not null;
        _arrangedOscPosition = null;
        MusicBar.Visibility = IsMusicMode ? Visibility.Visible : Visibility.Collapsed;
        _osc.SetActive(_surface.HasMedia && !_oscSuppressed && !IsMusicMode, Environment.TickCount64, AnimationsEnabled);
        UpdateLayoutSidebar(); LayoutOsc(); UpdateActionMenu();
        ObserveWindowFloating(_snapshot);
        UpdateMusicView();
        if (IsMusicMode) MusicReturnButton.Focus(_keyboardNavigation ? FocusState.Keyboard : FocusState.Pointer);
        else VideoFocus.Focus(_keyboardNavigation ? FocusState.Keyboard : FocusState.Pointer);
        WakeOsc();
    }
    private void UpdateMusicView()
    {
        UpdateMusicTextScale();
        DefaultMusicArtwork.Visibility = IsMusicMode && _musicArtworkVisible && _surface.HasMedia && _musicInfo?.DefaultArtwork == true ? Visibility.Visible : Visibility.Collapsed;
        if (!IsMusicMode) { VideoViewport.Visibility = Visibility.Visible; return; }
        VideoViewport.Visibility = _musicArtworkVisible ? Visibility.Visible : Visibility.Collapsed;
        VideoViewport.Margin = new(0, 0, 0, MusicBarHeight);
        string title = _surface.HasMedia ? _musicInfo?.Title ?? _surface.Title : UiText.Get("Loading");
        Owner?.SetMediaTitle(title);
        SetText(MusicTitle, title); ToolTipService.SetToolTip(MusicBar, title);
        SetText(MusicArtistAlbum, _musicInfo?.ArtistAlbum ?? "");
        MusicArtistAlbum.Visibility = string.IsNullOrEmpty(MusicArtistAlbum.Text) ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetName(MusicVolumeButton, string.Format(System.Globalization.CultureInfo.InvariantCulture, UiText.Get("MusicVolumeFormat"), _snapshot?.Volume ?? _settings.VolumePercent));
        UpdateMusicControls();
    }
    private void UpdateMusicTextScale()
    {
        var rows = MusicModeLayout.ControlRows(_systemUi.TextScaleFactor);
        double height = rows.Header + rows.Timeline;
        if (height == MusicBarHeight) return;
        MusicBarHeight = height; MusicBar.Height = height;
        MusicHeaderRow.Height = new(rows.Header); MusicTimelineRow.Height = new(rows.Timeline);
        UpdateQuickTabLabels();
        if (IsMusicMode)
        {
            VideoViewport.Margin = new(0, 0, 0, height);
            Owner?.UpdateMusicGeometry(_musicInfo?.DisplaySize ?? new(1, 1), _musicArtworkVisible);
            LayoutOsc();
        }
        Trace("music-text-scale", new { scale = _systemUi.TextScaleFactor, height });
    }
    private void UpdateMusicControls()
    {
        bool show = IsMusicMode && (_musicPointerInside || _menuOpen || _musicVolumeOpen || _pickerOpen || _dialogOpen ||
            _seeking || _adjustingVolume || _keyboardNavigation && Within(FocusManager.GetFocusedElement(XamlRoot) as DependencyObject, MusicBar));
        if (_musicControlsShown == show) return;
        _musicControlsShown = show;
        _musicFade?.Stop();
        MusicButtons.IsHitTestVisible = show;
        // Hidden buttons must not receive Tab; the visible timeline recalls them.
        MusicButtons.Visibility = Visibility.Visible;
        foreach (var button in MusicButtons.Children.OfType<Control>()) button.IsTabStop = show;
        _musicFade = new Storyboard();
        void Fade(UIElement target, double to)
        {
            var animation = new DoubleAnimation { From = target.Opacity, To = to, Duration = new(TimeSpan.FromSeconds(AnimationsEnabled ? .2 : 0)) };
            Storyboard.SetTarget(animation, target); Storyboard.SetTargetProperty(animation, "Opacity"); _musicFade.Children.Add(animation);
        }
        Fade(MusicButtons, show ? 1 : 0); Fade(MusicLabels, show ? 0 : 1); _musicFade.Begin();
        Trace("music-controls", new { shown = show, _musicPointerInside, _musicVolumeOpen });
    }
}

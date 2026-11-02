// IINA handleVideoSizeChange/onVideoReconfig and preference observation port.
// Copyright (c) 2016-2026 lhc and contributors. GPL-3.0; licenses/IINA-GPL-3.0.txt.
using Mani.Playback;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Mani_App;

public sealed partial class MainPage
{
    private VideoSize? _videoSize;
    private long _nextSizeRead;
    private (long Request, long Entry)? _sizeMedia;
    private bool _initialWindowSize = true;
    private (string Key, int Value)? _pendingWindowSetting;
    private ResizeWindowOption? _pendingWindowSize;
    private int? _pendingWindowStep;
    private bool CanChangeWindowSize => _surface.HasMedia && !_uiOpening && !_closing &&
        _videoSize is not null && _snapshot is { } current &&
        _sizeMedia == (current.RequestId, current.PlaylistEntryId) && Owner?.CanSetWindowSize == true;

    private void ConfigureWindowSizing()
    {
        double dock = !IsMusicMode && !IsPictureInPicture && !_settings.EdgeToEdgeVideo && _settings.DockedControls && _settings.OscPosition != OscPosition.Floating ? _oscMeasuredHeight : 0;
        Owner?.ConfigureWindowSizing(_surface.HasMedia ? _videoSize : null, _settings, dock, ReservedSidebarWidth);
    }
    private async Task ObserveWindowSizeAsync(PlaybackSnapshot snapshot, int revision)
    {
        if (!_surface.HasMedia || _session is null || _m1Busy) return;
        if (_sizeMedia == (snapshot.RequestId, snapshot.PlaylistEntryId) && Environment.TickCount64 < _nextSizeRead) return;
        _nextSizeRead = Environment.TickCount64 + 1000;
        var properties = await _session.GetPropertiesAsync("dwidth", "dheight", "video-params/rotate", "video-rotate", "vid", "path");
        if (_closing || _uiOpening || revision != _playbackRevision ||
            !string.Equals(properties.GetValueOrDefault("path"), snapshot.Path, StringComparison.Ordinal)) return;
        if (VideoSize.FromProperties(properties) is not { } video) return;
        var media = (snapshot.RequestId, snapshot.PlaylistEntryId);
        bool started = _sizeMedia != media;
        bool opened = started && (_sizeMedia is null || _sizeMedia.Value.Request != snapshot.RequestId);
        if (!started && _videoSize == video) return;
        _videoSize = video; _sizeMedia = media;
        RefreshPictureInPictureMenu();
        ConfigureWindowSizing();
        var frame = Owner?.ResizeForVideo(_initialWindowSize, started, opened);
        _initialWindowSize = false;
        Trace("window-video-size", new { snapshot.RequestId, snapshot.PlaylistEntryId, video, started, opened,
            locked = WindowSizing.AspectLocked(_settings), fullscreen = Owner?.IsFullscreen, frame, properties });
    }
    private void UpdateWindowSizeMenu()
    {
        WindowSizeMenu.IsEnabled = CanChangeWindowSize;
        LockWindowAspect.IsChecked = WindowSizing.AspectLocked(_settings);
        LockWindowAspect.IsEnabled = _settings.EdgeToEdgeVideo;
    }
    private void WindowSize_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && int.TryParse(item.Tag?.ToString(), out int value) && Enum.IsDefined((ResizeWindowOption)value))
            _pendingWindowSize = (ResizeWindowOption)value;
        MainMenu.Hide();
    }
    private void WindowStep_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && int.TryParse(item.Tag?.ToString(), out int direction))
            _pendingWindowStep = direction;
        MainMenu.Hide();
    }
    private void ApplyWindowSize(ResizeWindowOption size)
    {
        if (!CanChangeWindowSize) return;
        var frame = Owner?.ResizeForVideo(false, false, false, explicitSize: size);
        Trace("window-size-command", new { size = size.ToString(), frame });
    }
    private void ApplyWindowStep(int direction)
    {
        if (!CanChangeWindowSize) return;
        var frame = Owner?.StepWindowSize(direction);
        Trace("window-size-step", new { direction, frame });
    }
    private void WindowSetting_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleMenuFlyoutItem item) return;
        _pendingWindowSetting = ("lock", item.IsChecked ? 1 : 0);
        MainMenu.Hide();
    }
    private void ApplyPendingWindowSizing()
    {
        if (_closing) return;
        if (_pendingWindowSetting is { } setting)
        {
            _pendingWindowSetting = null;
            _settings = setting.Key switch
            {
                "lock" => _settings with { UnlockWindowAspectRatio = setting.Value == 0 },
                "physical" => _settings with { UsePhysicalResolution = setting.Value == 1 },
                "timing" => _settings with { ResizeWindowTiming = (ResizeWindowTiming)setting.Value },
                _ => _settings with { ResizeWindowOption = (ResizeWindowOption)setting.Value }
            };
            ConfigureWindowSizing(); SaveSettings();
            if (setting.Key == "lock" && WindowSizing.AspectLocked(_settings))
                Owner?.ResizeForVideo(false, false, false, keepSize: true);
            Trace("window-sizing-setting", new { setting.Key, setting.Value });
        }
        if (_pendingWindowSize is { } size)
        {
            _pendingWindowSize = null;
            ApplyWindowSize(size);
        }
        if (_pendingWindowStep is { } direction)
        {
            _pendingWindowStep = null;
            ApplyWindowStep(direction);
        }
        UpdateWindowSizeMenu();
    }
}

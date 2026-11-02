// IINA default input / PlayerWindowController command routing port.
// Copyright (c) 2016-2026 lhc and contributors. GPL-3.0; licenses/IINA-GPL-3.0.txt.
using System.Globalization;
using Mani.Playback;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI.Core;

namespace Mani_App;

public sealed partial class MainPage
{
    private async Task<bool> HandlePlaybackKeyAsync(KeyRoutedEventArgs e)
    {
        bool Down(VirtualKey key) => (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) != 0;
        var modifiers = (Down(VirtualKey.Control) ? PlaybackModifiers.Control : 0) |
            (Down(VirtualKey.Menu) ? PlaybackModifiers.Alt : 0) |
            (Down(VirtualKey.Shift) ? PlaybackModifiers.Shift : 0) |
            (Down(VirtualKey.LeftWindows) || Down(VirtualKey.RightWindows) ? PlaybackModifiers.Windows : 0);
        bool navigationControl = false;
        bool pageNavigationControl = false;
        // Inspect page content only. WinUI also puts a ScrollViewer above the
        // window's content root; that host does not own player page keys.
        for (var node = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject; node is not null && node != this; node = VisualTreeHelper.GetParent(node))
        {
            if (node != VideoFocus && node is ButtonBase or Slider or ToggleSwitch or Selector)
                navigationControl = true;
            // Page keys belong to sliders/lists and scrollable content, but a
            // transport button has no page action. Keep its player chord usable.
            if (node is Slider or Selector or ScrollViewer) pageNavigationControl = true;
        }
        var binding = PlaybackKeyMap.Resolve((int)e.Key, modifiers, PointerInputReady,
            OwnsKeyboardInput(), navigationControl, IsMusicMode || IsPictureInPicture, pageNavigationControl);
        if (binding is not { } command) return false;
        if (command.Action == PlaybackKeyAction.Quit)
        {
            if (_pickerOpen || _m1Busy) return false;
            e.Handled = true;
            Trace("playback-key", new { key = (int)e.Key, modifiers = modifiers.ToString(),
                action = command.Action.ToString(), command.Value, repeat = e.KeyStatus.WasKeyDown, RequestId = _snapshot?.RequestId ?? 0 });
            await App.Instance.QuitAsync();
            return true;
        }
        if (_snapshot is not { } snapshot) return false;
        if (command.Action == PlaybackKeyAction.RevealFile && CurrentLocalFile is null) return false;
        if (command.Action is PlaybackKeyAction.WindowSize or PlaybackKeyAction.WindowStep && !CanChangeWindowSize) return false;
        if (command.Action == PlaybackKeyAction.PictureInPicture && (IsMusicMode || !IsPictureInPicture && _videoSize is null)) return false;
        if (command.Action == PlaybackKeyAction.OnTop && (IsPictureInPicture || Owner?.Floating.CanSetManual != true)) return false;
        // Mark synchronously before the first await: the focused WinUI Button
        // must not also activate or move focus for this same key event.
        e.Handled = true;
        WakeOsc();
        Trace("playback-key", new { key = (int)e.Key, modifiers = modifiers.ToString(),
            action = command.Action.ToString(), command.Value, repeat = e.KeyStatus.WasKeyDown, snapshot.RequestId });
        switch (command.Action)
        {
            case PlaybackKeyAction.Playlist:
                await NavigatePlaylistAsync(command.Value == "1"); break;
            case PlaybackKeyAction.Chapter:
                await ChangeChapterAsync(int.Parse(command.Value, CultureInfo.InvariantCulture)); break;
            case PlaybackKeyAction.Edition:
                await CycleEditionAsync(); break;
            case PlaybackKeyAction.RevealFile:
                await RevealCurrentFileAsync(); break;
            case PlaybackKeyAction.Seek:
            case PlaybackKeyAction.Volume:
                if (command.Action == PlaybackKeyAction.Seek && snapshot.Duration <= 0) break;
                double amount = double.Parse(command.Value, CultureInfo.InvariantCulture);
                // Share the existing media-bound relative-input accumulator.
                // Its accepted read avoids lost volume steps during key repeat.
                _wheelInput.Add(snapshot.RequestId, snapshot.PlaylistEntryId,
                    command.Action == PlaybackKeyAction.Seek ? new(amount, 0) : new(0, amount));
                RequestInputDrain();
                break;
            case PlaybackKeyAction.Frame:
                await Execute(s => s.StepFrameAsync(command.Value == "back")); break;
            case PlaybackKeyAction.SpeedMultiply:
                await Execute(s => s.CommandAsync("multiply", "speed", command.Value)); break;
            case PlaybackKeyAction.SpeedReset:
                await Execute(s => s.SetSpeedAsync(1)); break;
            case PlaybackKeyAction.Pause:
                await TogglePauseAsync("keyboard"); break;
            case PlaybackKeyAction.Mute:
                await ToggleMuteAsync(); break;
            case PlaybackKeyAction.Fullscreen:
                Owner?.ToggleFullscreen(); break;
            case PlaybackKeyAction.PictureInPicture:
                _keyboardNavigation = true;
                TogglePictureInPicture(); break;
            case PlaybackKeyAction.OnTop:
                if (Owner is { } window) window.SetWindowFloating(!window.Floating.Requested); break;
            case PlaybackKeyAction.EnterFullscreen:
                if (Owner?.IsFullscreen == false) Owner.ToggleFullscreen(); break;
            case PlaybackKeyAction.Stop:
                await StopPlaybackAsync(); break;
            case PlaybackKeyAction.AbLoop:
                await Execute(s => s.CycleAbLoopAsync()); break;
            case PlaybackKeyAction.FileLoop:
                await ToggleFileLoopAsync(); break;
            case PlaybackKeyAction.Screenshot:
                await CaptureScreenshotAsync(); break;
            case PlaybackKeyAction.WindowSize:
                ApplyWindowSize(Enum.Parse<ResizeWindowOption>(command.Value)); break;
            case PlaybackKeyAction.WindowStep:
                ApplyWindowStep(int.Parse(command.Value, CultureInfo.InvariantCulture)); break;
            case PlaybackKeyAction.Property:
                await ApplyKeyboardPropertyAsync(command.Value, snapshot); break;
            case PlaybackKeyAction.SubtitleSeek:
                await Execute(async session =>
                {
                    var before = session.GetPlaybackRestartCountAsync();
                    var write = session.CommandAsync("sub-seek", command.Value);
                    await Task.WhenAll(before, write);
                    var actual = await session.ReadAsync();
                    long restarts = await session.GetPlaybackRestartCountAsync();
                    if (_closing || _uiOpening || _snapshot is not { } current ||
                        current.RequestId != snapshot.RequestId || current.FileLoadedCount != snapshot.FileLoadedCount ||
                        actual.RequestId != snapshot.RequestId || actual.FileLoadedCount != snapshot.FileLoadedCount) return;
                    // The engine decides subtitle boundaries and may do nothing.
                    // Do not invent a seek message from ordinary clock movement.
                    if (actual.Seeking || restarts > await before) _osdObserver.SeekCompleted(snapshot);
                    Trace("keyboard-sub-seek", new { command.Value, actual.RequestId, actual.Position,
                        actual.Seeking, restarted = restarts > await before });
                }); break;
            case PlaybackKeyAction.ShowProgress:
                ShowQuickFeedback(OsdMessage.From(OsdKind.ShowTime, snapshot), snapshot); break;
            case PlaybackKeyAction.Panel:
                _requestedQuickPane = Enum.Parse<QuickPane>(command.Value);
                _pendingLayoutToggle = true;
                ApplyPendingLayoutSidebar(); break;
        }
        return true;
    }

    private async Task ApplyKeyboardPropertyAsync(string command, PlaybackSnapshot media)
    {
        // Fixed IINA input.conf commands only; relative updates stay in mpv so
        // held keys never derive their next value from a stale sidebar snapshot.
        string[] arguments = command.Split(' ');
        // Keep IINA's original cycle aliases in the command. Observe the
        // canonical IDs used by its track-change handlers and our media model.
        string property = arguments[1] switch
        {
            "video" => "vid", "audio" => "aid", "sub" => "sid", _ => arguments[1]
        };
        int revision = ++_quickRevision;
        int playbackRevision = _playbackRevision;
        if (property == "sid") { _subtitleManualRevision++; _matchingCancellation?.Cancel(); }
        _quickFeedback = null;
        _quickWrites++;
        try
        {
            await Execute(async session =>
            {
                if (property is "vid" or "aid" or "sid")
                {
                    MediaInfo tracks;
                    try { tracks = await session.GetMediaInfoAsync(); }
                    catch (InvalidOperationException) when (_closing || _uiOpening || playbackRevision != _playbackRevision) { return; }
                    if (_closing || _uiOpening || playbackRevision != _playbackRevision ||
                        !QuickSettings.Matches(tracks, media)) return;
                    if (!QuickSettings.CanCycleTrack(tracks, property))
                    {
                        Trace("keyboard-track-unavailable", new { command, property, tracks.RequestId, tracks.PlaylistEntryId });
                        return;
                    }
                }
                var before = await session.GetPropertiesAsync("path", property);
                // Revalidate on the owning thread after the read completes.
                if (property is "vid" or "aid" or "sid") await session.TrackCommandForMediaAsync(media.RequestId, media.PlaylistEntryId, arguments);
                else await session.CommandForMediaAsync(media.RequestId, media.PlaylistEntryId, arguments);
                if (_closing || _uiOpening || revision != _quickRevision) return;
                var valuesTask = session.GetPropertiesAsync("path", property);
                var infoTask = session.GetMediaInfoAsync();
                await Task.WhenAll(valuesTask, infoTask);
                var values = await valuesTask;
                var actual = await infoTask;
                if (_closing || _uiOpening || revision != _quickRevision || _snapshot is not { } current ||
                    !QuickSettings.Matches(actual, media) || !QuickSettings.Matches(actual, current) ||
                    before.GetValueOrDefault("path") != media.Path || values.GetValueOrDefault("path") != media.Path ||
                    !before.TryGetValue(property, out var previous)) return;
                var feedback = new QuickOsdFeedback(media.RequestId, media.PlaylistEntryId, media.Path, property, previous);
                if (property is "sub-pos" or "sub-scale")
                {
                    ObserveMediaChannelSettings(values);
                }
                if (feedback.Complete(actual, values, UiText.Get("OsdTrackNone"), UiText.Get("TrackNoTitle"), UiText.Get("TrackDefault")) is { } message)
                    ShowQuickFeedback(message, current);
                Trace("keyboard-property-applied", new { command, property, before = previous,
                    value = values.GetValueOrDefault(property), actual.RequestId, actual.PlaylistEntryId });
            });
        }
        finally { _quickWrites--; _nextQuickRead = 0; }
    }
}

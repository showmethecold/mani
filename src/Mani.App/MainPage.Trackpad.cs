// IINA trackpad routing with a Windows Composition input boundary.
// Copyright (c) 2016-2026 lhc and contributors. GPL-3.0; licenses/IINA-GPL-3.0.txt.
using Mani_App.Interop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Mani_App;

public sealed partial class MainPage
{
    private readonly Dictionary<FrameworkElement, TrackpadInput> _trackpadSources = [];
    private readonly TrackpadGesture _trackpadGesture = new();
    private readonly TrackpadCommandQueue _trackpadCommands = new();
    private TrackpadInput? _activeTrackpad;
    private long _trackpadId;
    private TrackpadMedia _trackpadMedia;
    private (long Id, TrackpadMedia Media)? _trackpadResume;
    private (bool Music, bool Pip, bool Fullscreen) _trackpadMode;
    private WindowFrame? _trackpadZoomAnchor;
    private readonly DispatcherTimer _musicVolumeHideTimer = new();
    private bool _automaticMusicVolume;
    private TrackpadMedia CurrentTrackpadMedia => new(_snapshot?.RequestId ?? -1, _snapshot?.PlaylistEntryId ?? -1, _playbackRevision);

    private void InitializeTrackpad()
    {
        _musicVolumeHideTimer.Tick += (_, _) => HideAutomaticMusicVolume();
        foreach (var (element, target) in new (FrameworkElement, WheelTarget)[] {
            (VideoFocus, WheelTarget.Video), (MainTimeline, WheelTarget.Timeline), (MainVolume, WheelTarget.Volume),
            (MusicTimeline, WheelTarget.Timeline), (MusicVolume, WheelTarget.Volume), (MusicVolumeButton, WheelTarget.Volume) })
        {
            element.Loaded += (_, _) =>
            {
                if (_closing || _trackpadSources.ContainsKey(element)) return;
                try
                {
                    _trackpadSources[element] = new TrackpadInput(element, target, ReceiveTrackpad);
                    UpdateTrackpadSources();
                    Trace("trackpad-attached", new { element.Name, target = target.ToString() });
                }
                catch (Exception error) { Trace("trackpad-unavailable", new { element.Name, error.Message }); }
            };
            element.Unloaded += (_, _) =>
            {
                if (_trackpadSources.Remove(element, out var input)) input.Dispose();
            };
            element.AddHandler(PointerPressedEvent, new PointerEventHandler((_, args) =>
            {
                // Sliders retain their native direct-touch editing. Precision
                // touchpad redirection is automatic on each registered source.
                if (element == VideoFocus && _trackpadSources.TryGetValue(element, out var input))
                {
                    try { input.RedirectTouch(args, element); }
                    catch (System.Runtime.InteropServices.COMException error)
                    { Trace("trackpad-touch-rejected", new { error.HResult }); }
                }
            }), true);
        }
    }
    private void UpdateTrackpadSources()
    {
        if (_activeTrackpad is not null && (_trackpadMedia != CurrentTrackpadMedia ||
            _trackpadMode != (IsMusicMode, IsPictureInPicture, Owner?.IsFullscreen == true)))
            _activeTrackpad.Cancel();
        foreach (var (element, input) in _trackpadSources.ToArray())
            input.SetEnabled(PointerInputReady && element.IsLoaded && element.Visibility == Visibility.Visible &&
                (element is not Control control || control.IsEnabled));
    }
    private void ReceiveTrackpad(TrackpadInput input, TrackpadSample sample)
    {
        if (_closing) return;
        if (sample.Event == TrackpadEvent.Begin)
        {
            if (!PointerInputReady || _snapshot is null) { input.Cancel(); return; }
            if (_activeTrackpad is { } previous)
            {
                // Interacting can interrupt this same native tracker's inertia.
                // End only the old command stream: Cancel() would reset the
                // tracker that has already begun the replacement interaction.
                if (previous == input) ReceiveTrackpad(previous, sample with { Event = TrackpadEvent.Cancel });
                else previous.Cancel();
            }
            _activeTrackpad = input; _trackpadId++; _trackpadMedia = CurrentTrackpadMedia;
            _trackpadMode = (IsMusicMode, IsPictureInPicture, Owner?.IsFullscreen == true);
            _trackpadZoomAnchor = null;
            _trackpadGesture.Begin(input.Target, sample.X, sample.Y, sample.Scale);
            CancelVideoClick();
        }
        if (_activeTrackpad != input) return;
        if (_trackpadMedia != CurrentTrackpadMedia || !PointerInputReady)
        {
            if (sample.Event is not (TrackpadEvent.Cancel or TrackpadEvent.End)) { input.Cancel(); return; }
        }
        bool wasInertia = _trackpadGesture.Phase == TrackpadPhase.Inertia;
        var change = sample.Event switch
        {
            TrackpadEvent.Values => _trackpadGesture.Update(sample.X, sample.Y, sample.Scale),
            TrackpadEvent.Inertia => _trackpadGesture.BeginInertia(),
            TrackpadEvent.End or TrackpadEvent.Cancel => _trackpadGesture.End(),
            _ => default
        };
        void Add(TrackpadCommandKind kind, double amount = 0) =>
            _trackpadCommands.Add(new(_trackpadId, _trackpadMedia, kind, amount));
        if (change.BeginZoom)
        {
            _trackpadCommands.DiscardMotion(_trackpadId);
            _trackpadZoomAnchor = Owner?.BeginWindowZoom();
        }
        if (change.BeginSeek && _snapshot is { Duration: > 0 }) Add(TrackpadCommandKind.PauseForSeek);
        if (change.Seek != 0 && _snapshot is { Duration: > 0 }) Add(TrackpadCommandKind.Seek, change.Seek);
        if (change.Volume != 0) Add(TrackpadCommandKind.Volume, change.Volume);
        if (IsMusicMode && input.Target == WheelTarget.Video)
        {
            // WinUI Flyout closes asynchronously. Keep the same presentation
            // across adjacent direct/inertia callbacks, then use IINA's timeout
            // from the last momentum sample instead of racing Hide with ShowAt.
            if (sample.Event == TrackpadEvent.Cancel || sample.Event == TrackpadEvent.End && !wasInertia) HideAutomaticMusicVolume();
            if (sample.Event == TrackpadEvent.Inertia && _automaticMusicVolume) ShowAutomaticMusicVolume(timed: true);
            if (change.Volume != 0) ShowAutomaticMusicVolume(_trackpadGesture.Phase == TrackpadPhase.Inertia);
        }
        if (change.EndSeek) Add(TrackpadCommandKind.Resume);
        WindowFrame? frame = null;
        if (change.Zoom != 0 && CanChangeWindowSize && !_windowDragging)
            frame = Owner?.ZoomWindow(change.Zoom, _trackpadZoomAnchor);
        Trace("trackpad-input", new { gesture = _trackpadId, target = input.Target.ToString(), phase = sample.Event.ToString(),
            axis = _trackpadGesture.Axis.ToString(), change, frame });
        if (sample.Event is TrackpadEvent.End or TrackpadEvent.Cancel)
        {
            if (sample.Event == TrackpadEvent.Cancel) _trackpadCommands.DiscardMotion(_trackpadId);
            _activeTrackpad = null; _trackpadZoomAnchor = null;
        }
    }
    private async Task ApplyTrackpadInputAsync()
    {
        if (_session is null || _snapshot is not { } snapshot) { _trackpadCommands.Clear(); _trackpadResume = null; return; }
        foreach (var command in _trackpadCommands.Take())
        {
            bool Current() => !_closing && !_uiOpening && _surface.HasMedia && command.Media == CurrentTrackpadMedia;
            if (command.Kind == TrackpadCommandKind.Resume)
            {
                bool resume = _trackpadResume == (command.Gesture, command.Media);
                if (resume) _trackpadResume = null;
                if (resume && Current())
                {
                    await _session.SetPausedAsync(false);
                    Trace("trackpad-resume", new { command.Gesture });
                }
                continue;
            }
            if (!Current() || !PointerInputReady) continue;
            if (command.Kind == TrackpadCommandKind.PauseForSeek)
            {
                var values = await _session.GetPropertiesAsync("pause");
                if (!Current() || !PointerInputReady) continue;
                if (values.GetValueOrDefault("pause") == "no")
                {
                    await _session.SetPausedAsync(true);
                    if (Current()) _trackpadResume = (command.Gesture, command.Media);
                    Trace("trackpad-pause", new { command.Gesture });
                }
            }
            else
            {
                var change = command.Kind == TrackpadCommandKind.Seek ? new WheelChange(command.Amount, 0) : new WheelChange(0, command.Amount);
                await ApplyWheelChangeAsync(change, snapshot, () => Current() && PointerInputReady);
            }
        }
    }
    private void ResetTrackpad()
    {
        _activeTrackpad?.Cancel(); _activeTrackpad = null; _trackpadGesture.End();
        _trackpadCommands.Clear(); _trackpadResume = null; _trackpadZoomAnchor = null;
    }
    private void StopTrackpad()
    {
        HideAutomaticMusicVolume();
        ResetTrackpad();
        foreach (var input in _trackpadSources.Values) input.Dispose();
        _trackpadSources.Clear();
    }
    private void ShowAutomaticMusicVolume(bool timed)
    {
        if (!IsMusicMode || _closing) return;
        _musicVolumeHideTimer.Stop();
        if (!_musicVolumeOpen)
        {
            _automaticMusicVolume = true;
            MusicVolumeFlyout.ShowAt(MusicVolumeButton, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions
            { ShowMode = Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowMode.Transient });
        }
        if (timed && _automaticMusicVolume)
        {
            _musicVolumeHideTimer.Interval = TimeSpan.FromSeconds(Math.Max(.001, _settings.OsdAutoHideTimeout));
            _musicVolumeHideTimer.Start();
        }
    }
    private void HideAutomaticMusicVolume()
    {
        _musicVolumeHideTimer.Stop();
        if (!_automaticMusicVolume) return;
        _automaticMusicVolume = false; MusicVolumeFlyout.Hide();
    }
}

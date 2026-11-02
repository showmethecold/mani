// IINA 1.5.1/build 180 OSDView/OSDMessage and displayOSD Windows adaptation.
// Copyright (c) 2016-2026 lhc and contributors. GPL-3.0; licenses/IINA-GPL-3.0.txt.
using System.Globalization;
using Mani.Playback;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;

namespace Mani_App;

public sealed partial class MainPage
{
    private readonly OsdPlaybackObserver _osdObserver = new();
    private readonly OsdState _osd = new();
    private readonly DispatcherTimer _osdTimer = new();
    private string _lastOsdEvidence = "";
    private void InitializeOsd() => _osdTimer.Tick += OsdTick;
    private void ResetOsd()
    {
        _osdObserver.Reset(); _osd.Clear(); _osdTimer.Stop();
        OsdOverlay.Visibility = Visibility.Collapsed;
        RequestOscMaterial();
    }
    private void ObserveOsd(PlaybackSnapshot snapshot)
    {
        if (_closing || IsMusicMode) return;
        var update = _osdObserver.Observe(snapshot, Environment.TickCount64);
        if (update.Clear) _osd.Clear();
        bool accepted = update.Message is { } message && ShowOsdMessage(message, snapshot);
        _osd.RefreshTime(snapshot);
        UpdateOsdView();
        if (accepted) AnnounceOsd();
    }
    private bool ShowOsdMessage(OsdMessage message, PlaybackSnapshot snapshot)
    {
        if (_closing || IsMusicMode || !OsdPreferences.Allows(message.Kind, _settings)) return false;
        _osd.Show(message, Environment.TickCount64, (long)Math.Round(_settings.OsdAutoHideTimeout * 1000));
        Trace("osd-message", new { kind = message.Kind.ToString(), message.Value, message.Progress,
            snapshot.RequestId, snapshot.FileLoadedCount, snapshot.Position, snapshot.Duration, snapshot.Paused,
            snapshot.Volume, snapshot.Muted, snapshot.Speed,
            snapshot.AbLoopA, snapshot.AbLoopB, snapshot.FileLoop, snapshot.PlaylistLoop });
        return true;
    }
    private void ShowQuickFeedback(OsdMessage message, PlaybackSnapshot snapshot)
    {
        if (!_osdObserver.CanShowFeedback(snapshot, Environment.TickCount64) || !ShowOsdMessage(message, snapshot)) return;
        UpdateOsdView();
        AnnounceOsd();
    }
    private void AnnounceOsd()
    {
        if (OsdOverlay.Visibility == Visibility.Visible)
            (FrameworkElementAutomationPeer.FromElement(OsdTitle) ?? FrameworkElementAutomationPeer.CreatePeerForElement(OsdTitle))?
                .RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }
    private void OsdTick(object? sender, object e) { if (!_closing) UpdateOsdView(); }
    private void UpdateOsdView()
    {
        if (_closing) return;
        long now = Environment.TickCount64;
        _osd.Tick(now, AnimationsEnabled);
        if (!_settings.EnableOsd) _osd.Clear();
        bool show = !IsMusicMode && _osd.Visible && _notice.Length == 0 && _snapshot is not { PausedForCache: true } && !_m1Busy && !_oscSuppressed;
        OsdTitle.FontSize = _settings.OsdTextSize;
        OsdDetail.FontSize = OsdPreferences.SecondarySize(_settings.OsdTextSize);
        OsdOverlay.Opacity = _osd.Opacity;
        OsdOverlay.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (_osd.Message is { } message)
        {
            string time = $"{Time(message.Position)} / {Time(message.Duration)}";
            string title = message.Kind switch
            {
                OsdKind.File => message.Title,
                OsdKind.Navigation => UiText.Get("Edition") + ": " + (message.Title.Length > 0 ? message.Title : string.Format(UiText.Get("EditionFormat"), message.Value)),
                OsdKind.Seek => time,
                OsdKind.ShowTime => $"{OsdMessage.PreciseTime(message.Position)} / {OsdMessage.PreciseTime(message.Duration)}",
                OsdKind.Volume => string.Format(CultureInfo.InvariantCulture, UiText.Get("OsdVolume"), message.Value),
                OsdKind.Speed => string.Format(CultureInfo.InvariantCulture, UiText.Get("OsdSpeed"), message.Value),
                OsdKind.SubPosition or OsdKind.SubScale => string.Format(CultureInfo.InvariantCulture, UiText.Get("Osd" + message.Kind), message.Value),
                OsdKind.VideoTrack or OsdKind.AudioTrack or OsdKind.SubtitleTrack =>
                    string.Format(CultureInfo.InvariantCulture, UiText.Get("Osd" + message.Kind), message.Title),
                OsdKind.AudioDelay or OsdKind.SubDelay or OsdKind.SecondarySubDelay =>
                    string.Format(CultureInfo.InvariantCulture,
                        UiText.Get("Osd" + message.Kind + (message.Value == 0 ? "None" : message.Value > 0 ? "Later" : "Earlier")),
                        QuickOsdFeedback.FormatDelay(message.Value)),
                _ => UiText.Get("Osd" + message.Kind)
            };
            SetText(OsdTitle, title);
            bool secondary = message.Kind is OsdKind.Pause or OsdKind.Resume or OsdKind.AbLoopA or OsdKind.AbLoopB;
            SetText(OsdDetail, secondary ? time : "");
            OsdDetail.Visibility = secondary ? Visibility.Visible : Visibility.Collapsed;
            AutomationProperties.SetName(OsdTitle, secondary ? $"{title}, {time}" : title);
            OsdProgress.Visibility = message.Progress is not null ? Visibility.Visible : Visibility.Collapsed;
            OsdProgress.Value = message.Progress ?? 0;
            AutomationProperties.SetName(OsdProgress, title);
        }
        LayoutOsd();
        string evidence = $"{_osd.Message?.Kind}/{show}/{_osd.Opacity:F3}";
        if (evidence != _lastOsdEvidence)
        {
            _lastOsdEvidence = evidence;
            RequestOscMaterial();
            Trace("osd-visibility", new { kind = _osd.Message?.Kind.ToString(), visible = show, opacity = _osd.Opacity,
                animations = AnimationsEnabled, hitTest = OsdOverlay.IsHitTestVisible });
        }
        if (_osd.Visible)
        { _osdTimer.Interval = TimeSpan.FromMilliseconds(_osd.NextTick(now)); _osdTimer.Start(); }
        else _osdTimer.Stop();
    }
    private void LayoutOsd()
    {
        if (OsdOverlay is null) return;
        double top = EffectiveOscPosition == OscPosition.Top && !_appliedDocking.Docked ? _oscMeasuredHeight + 8 : 8;
        // IINA anchors OSD after the leading sidebar. Docked sidebars are
        // already outside VideoViewport; only subtract their overlay portion.
        double left = Math.Max(0, _sidebarPlan.OccupiedLeft - _sidebarPlan.VideoLeft);
        double right = Math.Max(0, _sidebarPlan.OccupiedRight - _sidebarPlan.VideoRight);
        OsdOverlay.Margin = new(left + 8, top, right + 8, 8);
        OsdOverlay.MaxWidth = Math.Max(1, VideoViewport.ActualWidth - left - right - 16);
        OsdOverlay.MaxHeight = Math.Max(1, VideoViewport.ActualHeight - top - 8);
    }
    private async Task SeekWithOsdAsync(PlaybackSession session, double position)
    {
        var media = _snapshot;
        await session.SeekAsync(position);
        // The command reply does not imply that the requested position is on screen.
        // The next accepted, settled snapshot supplies both time and progress.
        if (!_closing && media is not null) _osdObserver.SeekCompleted(media);
    }
    private void StopOsd()
    { ResetOsd(); _osdTimer.Tick -= OsdTick; }
}

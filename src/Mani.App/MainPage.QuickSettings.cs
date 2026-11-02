// IINA QuickSettingViewController/Sidebar*Pane port with WinUI controls.
// Copyright (c) 2016-2026 lhc and contributors. GPL-3.0; licenses/IINA-GPL-3.0.txt.
using System.Globalization;
using Mani.Playback;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;

namespace Mani_App;

public sealed partial class MainPage
{
    private QuickPane _quickPane = QuickPane.Video;
    private QuickPane? _requestedQuickPane;
    private MediaInfo? _quickInfo;
    private bool _quickUpdating;
    private int _quickRevision, _quickWrites;
    private long _nextQuickRead;
    private string _quickEvidence = "";
    private IReadOnlyDictionary<string, string> _quickValues = new Dictionary<string, string>();
    private QuickOsdFeedback? _quickFeedback;

    private void QuickSettingsMenu_Click(object sender, RoutedEventArgs e)
    {
        _requestedQuickPane = sender is MenuFlyoutItem { Tag: string name } && Enum.TryParse<QuickPane>(name, out var pane) ? pane : _quickPane;
        _pendingLayoutToggle = true;
        MainMenu.Hide();
    }
    private void QuickToolbar_Click(object sender, RoutedEventArgs e)
    {
        _requestedQuickPane = _quickPane;
        _pendingLayoutToggle = true;
        ApplyPendingLayoutSidebar();
    }
    private void SelectQuickPane(QuickPane pane)
    {
        if (pane != _quickPane) CancelDelayEdits();
        _quickUpdating = true;
        try
        {
            _quickPane = pane;
            QuickPage.SelectedIndex = (int)pane;
            UpdateQuickTabLabels();
            LayoutPane.Visibility = pane == QuickPane.Layout ? Visibility.Visible : Visibility.Collapsed;
            QuickVideoPane.Visibility = pane == QuickPane.Video ? Visibility.Visible : Visibility.Collapsed;
            QuickAudioPane.Visibility = pane == QuickPane.Audio ? Visibility.Visible : Visibility.Collapsed;
            QuickSubtitlesPane.Visibility = pane == QuickPane.Subtitles ? Visibility.Visible : Visibility.Collapsed;
            QuickScroll.Visibility = pane is QuickPane.Playlist or QuickPane.Chapters ? Visibility.Collapsed : Visibility.Visible;
            _playlistPane.Visibility = pane == QuickPane.Playlist ? Visibility.Visible : Visibility.Collapsed;
            _chaptersPane.Visibility = pane == QuickPane.Chapters ? Visibility.Visible : Visibility.Collapsed;
            QuickScroll.ChangeView(null, 0, null, true);
            _nextQuickRead = 0;
        }
        finally { _quickUpdating = false; }
        Trace("quick-pane", new { pane = pane.ToString() });
    }
    private void QuickPage_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_quickUpdating || QuickPage.SelectedIndex < 0) return;
        SelectQuickPane((QuickPane)QuickPage.SelectedIndex);
    }
    private void UpdateQuickTabLabels()
    {
        // IINA SideBar.updateTabActiveStatus: icons remain visible; selected title is shown.
        // On very narrow windows or large system text retain icon tooltips/Automation names.
        var labels = new[] { TabLayoutLabel, TabVideoLabel, TabAudioLabel, TabSubtitlesLabel };
        bool room = _sidebarPlan.Width >= 300 && _systemUi.TextScaleFactor <= 1.5;
        for (int i = 0; i < labels.Length; i++)
            labels[i].Visibility = room && i == (int)_quickPane ? Visibility.Visible : Visibility.Collapsed;
        // ListBox supports StackPanel; virtualizing WrapGrid requires ListViewBase.
        // Keep all three choices usable by stacking them for narrow / enlarged text layouts.
        if (LayoutOscMode.ItemsPanelRoot is StackPanel choices)
            choices.Orientation = _sidebarPlan.Width >= 300 && _systemUi.TextScaleFactor <= 1.5 ? Orientation.Horizontal : Orientation.Vertical;
    }
    private bool QuickCurrent => !_closing && !_uiOpening && !_m1Busy && _surface.HasMedia &&
        _quickInfo is { } info && _snapshot is { } snapshot && QuickSettings.Matches(info, snapshot);

    private async Task ObserveQuickSettingsAsync(PlaybackSnapshot snapshot, int revision)
    {
        if (!QuickCurrent) { _quickFeedback = null; ClearSubtitleTrackNotice(); CancelDelayEdits(); SetQuickEnabled(false); }
        if (IsMusicMode || IsPictureInPicture) _quickFeedback = null;
        // Complete a pending operation even if its sidebar has just closed.
        // Otherwise preserve the existing visible-sidebar-only read cadence.
        if (_session is null || !_surface.HasMedia || IsMusicMode || IsPictureInPicture ||
            ((!_sidebar.WantsVisible || _quickPane == QuickPane.Layout) && _quickFeedback is null) ||
            _quickWrites != 0 || Environment.TickCount64 < _nextQuickRead) return;
        _nextQuickRead = Environment.TickCount64 + 500;
        int quickRevision = _quickRevision;
        try
        {
            var info = await PollMediaInfoAsync();
            var values = await _session.GetPropertiesAsync("path", "speed", "audio-delay", "sub-delay", "secondary-sub-delay", "sub-visibility", "secondary-sub-visibility", "sub-pos", "secondary-sub-pos", "sub-scale", "secondary-sub-scale", "sub-ass-override", "secondary-sub-ass-override");
            if (_closing || _uiOpening || revision != _playbackRevision || quickRevision != _quickRevision ||
                !QuickSettings.Matches(info, snapshot) || values.GetValueOrDefault("path") != snapshot.Path) return;
            _quickUpdating = true;
            try
            {
                if (_quickInfo is null || _quickInfo.RequestId != info.RequestId || _quickInfo.PlaylistEntryId != info.PlaylistEntryId)
                    foreach (var box in new[] { QuickVideoTrack, QuickAudioTrack, QuickPrimaryTrack, QuickSecondaryTrack }) box.IsDropDownOpen = false;
                _quickInfo = info;
                _quickValues = values;
                UpdateQuickTracks(QuickVideoTrack, info, info.VideoTrack);
                UpdateQuickTracks(QuickAudioTrack, info, info.AudioTrack);
                UpdateQuickTracks(QuickPrimaryTrack, info, info.SubtitleTrack);
                UpdateQuickTracks(QuickSecondaryTrack, info, info.SecondarySubtitleTrack);
                foreach (var box in new[] { QuickSpeed, QuickAudioDelay, QuickPrimaryDelay, QuickSecondaryDelay })
                    if (!DelayEditing((string)box.Tag) && !Within(FocusManager.GetFocusedElement(XamlRoot) as DependencyObject, box) &&
                        double.TryParse(values.GetValueOrDefault((string)box.Tag), CultureInfo.InvariantCulture, out double value) && double.IsFinite(value))
                        box.Value = value;
                if (!ReferenceEquals(FocusManager.GetFocusedElement(XamlRoot), QuickSpeedSlider) &&
                    double.TryParse(values.GetValueOrDefault("speed"), CultureInfo.InvariantCulture, out double speed))
                    QuickSpeedSlider.Value = QuickSettings.StepFromSpeed(speed);
                ObserveDelaySliders(values);
                QuickPrimaryVisible.IsOn = values.GetValueOrDefault("sub-visibility") == "yes";
                QuickSecondaryVisible.IsOn = values.GetValueOrDefault("secondary-sub-visibility") == "yes";
                SetQuickEnabled(true);
                ObserveMediaChannelSettings(values);
            }
            finally { _quickUpdating = false; }
            var feedback = _quickFeedback;
            _quickFeedback = null;
            if (feedback?.Complete(info, values, UiText.Get("OsdTrackNone"), UiText.Get("TrackNoTitle"), UiText.Get("TrackDefault")) is { } message)
                ShowQuickFeedback(message, snapshot);
            string evidence = $"{info.RequestId}/{info.PlaylistEntryId}/{info.VideoTrack}/{info.AudioTrack}/{info.SubtitleTrack}/{info.SecondarySubtitleTrack}/" +
                string.Join("/", values.OrderBy(p => p.Key).Select(p => p.Value));
            if (evidence != _quickEvidence)
            {
                _quickEvidence = evidence;
                Trace("quick-observed", new { info.RequestId, info.PlaylistEntryId, info.VideoTrack, info.AudioTrack, info.SubtitleTrack, info.SecondarySubtitleTrack,
                    tracks = info.Tracks.Select(t => new { t.Id, t.Type }), values });
            }
        }
        catch (MediaUnavailableException) { _quickInfo = null; SetQuickEnabled(false); _nextQuickRead = 0; }
        catch (OperationCanceledException) { }
        catch (InvalidOperationException) when (_uiOpening || revision != _playbackRevision || !_surface.HasMedia) { }
    }
    private void SetQuickEnabled(bool enabled)
    {
        void SetControls(DependencyObject node)
        {
            if (node is Control control) { control.IsEnabled = enabled; return; }
            for (int i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(node); i++)
                SetControls(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(node, i));
        }
        foreach (var pane in new[] { QuickVideoPane, QuickAudioPane, QuickSubtitlesPane })
            SetControls(pane);
        RefreshMediaControls(); // Restore per-control scope/capability rules after the broad enable pass.
    }
    private void UpdateQuickTracks(ComboBox box, MediaInfo info, string selected)
    {
        if (box.IsDropDownOpen) return; // Never replace the keyboard user's open list.
        var tracks = QuickSettings.Tracks(info, (string)box.Tag, UiText.Get("TrackNone"), UiText.Get("TrackNoTitle"), UiText.Get("TrackDefault"));
        if (box.ItemsSource is not QuickTrack[] old || !old.SequenceEqual(tracks)) box.ItemsSource = tracks;
        box.SelectedIndex = QuickSettings.SelectedIndex(tracks, selected);
    }
    private async Task SetQuickPropertyAsync(string property, string value)
    {
        if (!QuickCurrent || _quickUpdating || _session is null) return;
        _quickFeedback = QuickOsdFeedback.Begin(property, _quickInfo!, _quickValues);
        _quickRevision++;
        _quickWrites++;
        try
        {
            // Enqueue immediately on the existing player worker; never carry a command across a UI await/media change.
            var media = _quickInfo!;
            await Execute(s => s.CommandForMediaAsync(media.RequestId, media.PlaylistEntryId, "set", property, value));
            Trace("quick-command", new { property, value });
        }
        finally { _quickWrites--; _nextQuickRead = 0; }
    }
    private async void QuickTrack_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { SelectedItem: QuickTrack track } box || !QuickCurrent || _quickUpdating || _quickInfo is null) return;
        string property = (string)box.Tag;
        // IDs are not row numbers. Recheck membership in the last accepted media model.
        if (track.Id != 0 && !_quickInfo.Tracks.Any(t => t.Id == track.Id && t.Type == QuickSettings.TrackType(property))) return;
        var before = _quickInfo;
        if (property is "sid" or "secondary-sid") { _subtitleManualRevision++; _matchingCancellation?.Cancel(); }
        _quickFeedback = QuickOsdFeedback.Begin(property, before, _quickValues);
        int revision = ++_quickRevision;
        _quickWrites++;
        try
        {
            // IINA PlayerCore.setTrack writes the ID then reads the actual selection.
            // mpv can accept the command but retain the old track on a conflict.
            await Execute(async session =>
            {
                string value = track.Id == 0 ? "no" : track.Id.ToString(CultureInfo.InvariantCulture);
                await session.SelectTrackAsync(before, property, track.Id);
                Trace("quick-command", new { property, value });
                var actual = await session.GetMediaInfoAsync();
                if (_closing || _uiOpening || revision != _quickRevision || _snapshot is not { } snapshot ||
                    !QuickSettings.Matches(actual, snapshot) || actual.RequestId != before.RequestId ||
                    actual.PlaylistEntryId != before.PlaylistEntryId) return;
                if (property is not ("sid" or "secondary-sid")) return;
                _quickInfo = actual;
                ClearSubtitleTrackNotice();
                if (!QuickSettings.SubtitleConflict(actual, property, track.Id)) return;
                _quickUpdating = true;
                try
                {
                    box.IsDropDownOpen = false;
                    UpdateQuickTracks(box, actual, property == "sid" ? actual.SubtitleTrack : actual.SecondarySubtitleTrack);
                }
                finally { _quickUpdating = false; }
                ShowSubtitleTrackNotice(property, track.Id);
            });
        }
        finally { _quickWrites--; _nextQuickRead = 0; }
    }
    private void ClearSubtitleTrackNotice()
    {
        QuickPrimaryConflict.Visibility = QuickSecondaryConflict.Visibility = Visibility.Collapsed;
        QuickPrimaryConflict.Text = QuickSecondaryConflict.Text = "";
    }
    private void ShowSubtitleTrackNotice(string property, long track)
    {
        var notice = property == "sid" ? QuickPrimaryConflict : QuickSecondaryConflict;
        notice.Text = string.Format(CultureInfo.InvariantCulture,
            UiText.Get(property == "sid" ? "SubtitleUsedBySecondary" : "SubtitleUsedByPrimary"), track);
        notice.Visibility = Visibility.Visible;
        (FrameworkElementAutomationPeer.FromElement(notice) ?? FrameworkElementAutomationPeer.CreatePeerForElement(notice))?
            .RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (!_closing && _sidebar.WantsVisible && _quickPane == QuickPane.Subtitles && notice.Visibility == Visibility.Visible)
            {
                // The newly wrapped message needs its final height before the
                // ScrollViewer can reveal the last line in a short window.
                notice.UpdateLayout();
                notice.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = 1 });
            }
        });
        Trace("subtitle-track-conflict", new { property, track, message = notice.Text });
    }
    private async void QuickNumber_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_quickUpdating || !QuickCurrent || !double.IsFinite(args.NewValue)) return;
        double value = (string)sender.Tag == "speed" ? QuickSettings.ConstrainSpeed(args.NewValue) : args.NewValue;
        await SetQuickPropertyAsync((string)sender.Tag, value.ToString(CultureInfo.InvariantCulture));
    }
    private async void QuickSpeed_Changed(object sender, RangeBaseValueChangedEventArgs args)
    {
        if (_quickUpdating || !QuickCurrent) return;
        await SetQuickPropertyAsync("speed", QuickSettings.SpeedFromStep(args.NewValue).ToString(CultureInfo.InvariantCulture));
    }
    private async void QuickVisibility_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleSwitch toggle) await SetQuickPropertyAsync((string)toggle.Tag, toggle.IsOn ? "yes" : "no");
    }
    private async void QuickReset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string property }) await SetQuickPropertyAsync(property, property == "speed" ? "1" : "0");
    }
}

// IINA PlaySlider / ThumbnailPeekView: media-bound time/chapter and thumbnail peek.
// Copyright (c) 2016-2026 lhc and contributors. GPL-3.0; licenses/IINA-GPL-3.0.txt.
using Mani.Playback;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Mani_App;

public sealed partial class MainPage
{
    private readonly ToolTip _timelinePreview = new() { Placement = PlacementMode.Top };
    private readonly Image _previewImage = new() { MaxWidth = 180, MaxHeight = 120, Visibility = Visibility.Collapsed };
    private readonly TextBlock _previewText = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 220 };
    private ThumbnailPreviewCache? _thumbnailCache;
    private CancellationTokenSource? _thumbnailCancellation;
    private readonly HashSet<Task> _thumbnailTasks = [];
    private long _previewRequest;
    private int _previewIndex = -1;
    private void UpdateChapterMarkers()
    {
        var fractions = _settings.ShowChapterPositions && _snapshot is { Loading: false } media &&
            _navigationInfo is { } info && QuickSettings.Matches(info, media)
            ? ChapterMarkerLayout.Fractions(media.Duration, info.Chapters) : [];
        bool changed = MainTimeline.SetChapterFractions(fractions);
        MusicTimeline.SetChapterFractions(fractions);
        if (changed) Trace("chapter-markers", new { count = fractions.Length, fractions, request = _snapshot?.RequestId, entry = _snapshot?.PlaylistEntryId });
    }

    private void InitializeTimelinePreview()
    {
        var content = new StackPanel { Spacing = 4 }; content.Children.Add(_previewImage); content.Children.Add(_previewText);
        _timelinePreview.Content = content; AutomationProperties.SetAutomationId(_timelinePreview, "TimelinePreview");
        foreach (var timeline in new[] { MainTimeline, MusicTimeline })
        {
            timeline.PointerMoved += TimelinePreview_Moved;
            timeline.PointerExited += (_, _) => CancelTimelinePreview();
        }
        string cache = Path.Combine(App.DataDirectory, "thumbnails");
        _thumbnailCache = new(cache, App.Instance.CaptureThumbnailAsync);
    }
    private void CancelTimelinePreview()
    {
        _thumbnailCancellation?.Cancel(); _thumbnailCancellation?.Dispose(); _thumbnailCancellation = null;
        _previewIndex = -1; _timelinePreview.IsOpen = false; _previewImage.Source = null; _previewImage.Visibility = Visibility.Collapsed;
    }
    private async void TimelinePreview_Moved(object sender, PointerRoutedEventArgs e)
    {
        var task = UpdateTimelinePreviewAsync(sender, e); _thumbnailTasks.Add(task);
        try { await task; }
        catch (Exception error) { Trace("thumbnail-unavailable", new { code = error.HResult }); CancelTimelinePreview(); }
        finally { _thumbnailTasks.Remove(task); }
    }
    private async Task UpdateTimelinePreviewAsync(object sender, PointerRoutedEventArgs e)
    {
        if (_closing || _uiOpening || _menuOpen || _dialogOpen || _snapshot is not { Duration: > 0, Loading: false } media || sender is not WheelSlider slider) return;
        double fraction = slider.TimelineFraction(e.GetCurrentPoint(slider).Position);
        double seconds = fraction * media.Duration;
        var chapter = _navigationInfo is { } info && QuickSettings.Matches(info, media) ? info.Chapters.LastOrDefault(c => c.StartTime <= seconds) : null;
        _previewText.Text = Time(seconds) + (chapter is null ? "" : "\n" + ChapterTitle(chapter));
        _timelinePreview.XamlRoot = XamlRoot;
        _timelinePreview.PlacementTarget = slider;
        ToolTipService.SetToolTip(slider, _timelinePreview);
        _timelinePreview.IsOpen = true;
        if (!_settings.EnableThumbnailPreview)
        {
            _thumbnailCancellation?.Cancel(); _previewImage.Source = null; _previewImage.Visibility = Visibility.Collapsed;
            _previewIndex = -1; return;
        }
        int index = ThumbnailPreviewCache.SampleIndex(seconds, media.Duration);
        if (_previewIndex == index && _previewRequest == media.RequestId) return;
        _thumbnailCancellation?.Cancel(); _thumbnailCancellation?.Dispose();
        var cancellation = _thumbnailCancellation = new(); var token = cancellation.Token;
        _previewIndex = index; _previewRequest = media.RequestId;
        _previewImage.Source = null; _previewImage.Visibility = Visibility.Collapsed;
        try
        {
            long cacheGeneration = _thumbnailCache!.Generation;
            await Task.Delay(100, token);
            var path = await _thumbnailCache!.GetAsync(media.Path, seconds, media.Duration, token);
            if (path is null || cacheGeneration != _thumbnailCache.Generation || !_settings.EnableThumbnailPreview || token.IsCancellationRequested || _closing || _snapshot?.RequestId != media.RequestId || _snapshot.PlaylistEntryId != media.PlaylistEntryId) return;
            _previewImage.Source = new BitmapImage(new Uri(path)); _previewImage.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        { Trace("thumbnail-unavailable", new { code = error.HResult }); }
    }
}

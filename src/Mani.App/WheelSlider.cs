// Windows adaptation of IINA PlaySlider/VolumeSlider scrollWheel forwarding.
// The existing player command owns scrolling; suppress Slider's native second action.
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
namespace Mani_App;

public sealed class WheelSlider : Slider
{
    private Grid? _trackHost;
    private FrameworkElement? _thumb;
    private Microsoft.UI.Xaml.Shapes.Rectangle? _track;
    private Canvas? _chapterLayer;
    private Microsoft.UI.Xaml.Shapes.Path? _chapterMarks;
    private double[] _chapterFractions = [];

    protected override void OnApplyTemplate()
    {
        if (_track is not null) _track.SizeChanged -= Track_SizeChanged;
        if (_thumb is not null) _thumb.SizeChanged -= Track_SizeChanged;
        if (_trackHost is not null && _chapterLayer is not null) _trackHost.Children.Remove(_chapterLayer);
        base.OnApplyTemplate();
        _trackHost = GetTemplateChild("HorizontalTemplate") as Grid;
        _track = GetTemplateChild("HorizontalTrackRect") as Microsoft.UI.Xaml.Shapes.Rectangle;
        _thumb = GetTemplateChild("HorizontalThumb") as FrameworkElement;
        _chapterLayer = null;
        _chapterMarks = null;
        if (_trackHost is null || _track is null || _thumb is null) return;
        // Adapt IINA's one-point chapter cutouts to a theme-aware WinUI layer.
        // Native thumb, automation peer, keyboard and pointer handling stay intact.
        _chapterLayer = new Canvas { IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAccessibilityView(_chapterLayer, AccessibilityView.Raw);
        Grid.SetRow(_chapterLayer, 1); Grid.SetColumnSpan(_chapterLayer, 3);
        _trackHost.Children.Insert(_trackHost.Children.IndexOf(_thumb), _chapterLayer);
        _track.SizeChanged += Track_SizeChanged;
        _thumb.SizeChanged += Track_SizeChanged;
        RenderChapters();
    }

    internal bool SetChapterFractions(double[] fractions)
    {
        if (_chapterFractions.AsSpan().SequenceEqual(fractions)) return false;
        _chapterFractions = fractions;
        RenderChapters();
        return true;
    }

    internal double TimelineFraction(Point position)
    {
        if (_track is null || _thumb is null) return ChapterMarkerLayout.Fraction(position.X, ActualWidth, 12);
        var point = TransformToVisual(_track).TransformPoint(position);
        return ChapterMarkerLayout.Fraction(point.X, _track.ActualWidth, _thumb.ActualWidth);
    }

    private void Track_SizeChanged(object sender, SizeChangedEventArgs e) => RenderChapters();

    private void RenderChapters()
    {
        if (_chapterLayer is null || _track is null || _thumb is null) return;
        _chapterLayer.Height = _track.ActualHeight;
        if (_chapterFractions.Length == 0)
        {
            if (_chapterMarks is not null) _chapterMarks.Data = null;
            return;
        }
        if (_chapterMarks is null)
        {
            _chapterMarks = (Microsoft.UI.Xaml.Shapes.Path)Microsoft.UI.Xaml.Markup.XamlReader.Load(
                "<Path xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" Fill=\"{ThemeResource ApplicationPageBackgroundThemeBrush}\" IsHitTestVisible=\"False\"/>");
            AutomationProperties.SetAccessibilityView(_chapterMarks, AccessibilityView.Raw);
            _chapterLayer.Children.Add(_chapterMarks);
        }
        var geometry = new GeometryGroup { FillRule = FillRule.Nonzero };
        foreach (double fraction in _chapterFractions)
        {
            geometry.Children.Add(new RectangleGeometry { Rect = new Rect(
                ChapterMarkerLayout.Center(fraction, _track.ActualWidth, _thumb.ActualWidth) - .5, 0, 1, _track.ActualHeight) });
        }
        _chapterMarks.Data = geometry;
    }

    internal event EventHandler<PointerRoutedEventArgs>? PlayerWheel;
    protected override void OnPointerWheelChanged(PointerRoutedEventArgs e)
    {
        e.Handled = true;
        PlayerWheel?.Invoke(this, e);
    }
}

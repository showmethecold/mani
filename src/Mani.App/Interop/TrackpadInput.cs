// Windows boundary for IINA's trackpad phase and magnification rules.
using System.Numerics;
using Microsoft.UI.Composition.Interactions;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;

namespace Mani_App.Interop;

internal enum TrackpadEvent { Begin, Values, Inertia, End, Cancel }
internal readonly record struct TrackpadSample(TrackpadEvent Event, float X, float Y, float Scale);

internal sealed class TrackpadInput : IInteractionTrackerOwner, IDisposable
{
    private readonly DispatcherQueue _dispatcher;
    private readonly Action<TrackpadInput, TrackpadSample> _receive;
    private readonly InteractionTracker _tracker;
    private readonly VisualInteractionSource _source;
    private bool _enabled, _disposed, _active;
    private Vector3 _position;
    private float _scale = 1;
    public WheelTarget Target { get; }

    public TrackpadInput(UIElement element, WheelTarget target, Action<TrackpadInput, TrackpadSample> receive)
    {
        Target = target; _dispatcher = element.DispatcherQueue; _receive = receive;
        var visual = ElementCompositionPreview.GetElementVisual(element);
        _tracker = InteractionTracker.CreateWithOwner(visual.Compositor, this);
        VisualInteractionSource? source = null;
        try
        {
        _tracker.MinPosition = new(-1000000); _tracker.MaxPosition = new(1000000);
        _tracker.MinScale = .01f; _tracker.MaxScale = 100;
        _source = source = VisualInteractionSource.Create(visual);
        _source.ManipulationRedirectionMode = VisualInteractionSourceRedirectionMode.CapableTouchpadOnly;
        _source.PositionXChainingMode = _source.PositionYChainingMode = _source.ScaleChainingMode = InteractionChainingMode.Never;
        _source.IsPositionXRailsEnabled = _source.IsPositionYRailsEnabled = true;
        _tracker.InteractionSources.Add(_source);
        }
        catch
        {
            _disposed = true;
            source?.Dispose(); _tracker.Dispose();
            throw;
        }
    }
    public void SetEnabled(bool enabled)
    {
        if (_disposed || _enabled == enabled) return;
        _enabled = enabled;
        _source.PositionXSourceMode = _source.PositionYSourceMode = enabled ? InteractionSourceMode.EnabledWithInertia : InteractionSourceMode.Disabled;
        _source.ScaleSourceMode = enabled && Target == WheelTarget.Video ? InteractionSourceMode.EnabledWithoutInertia : InteractionSourceMode.Disabled;
        if (!enabled) Cancel();
    }
    public void RedirectTouch(PointerRoutedEventArgs args, UIElement element)
    {
        if (!_enabled || _disposed || args.Pointer.PointerDeviceType != PointerDeviceType.Touch) return;
        _source.TryRedirectForManipulation(args.GetCurrentPoint(element));
        args.Handled = true;
    }
    public void Cancel()
    {
        if (_disposed) return;
        if (_active) { _active = false; _receive(this, new(TrackpadEvent.Cancel, _position.X, _position.Y, _scale)); }
        // Cancels native inertia as well as resetting the next gesture baseline.
        _tracker.TryUpdatePosition(Vector3.Zero); _tracker.TryUpdateScale(1, Vector3.Zero);
    }
    private void Dispatch(Action action)
    {
        if (_disposed) return;
        _dispatcher.TryEnqueue(() => { if (!_disposed) action(); });
    }
    public void InteractingStateEntered(InteractionTracker sender, InteractionTrackerInteractingStateEnteredArgs args) => Dispatch(() =>
    {
        if (!_enabled) return;
        _active = true; _receive(this, new(TrackpadEvent.Begin, _position.X, _position.Y, _scale));
    });
    public void ValuesChanged(InteractionTracker sender, InteractionTrackerValuesChangedArgs args)
    {
        var position = args.Position; float scale = args.Scale; int request = args.RequestId;
        Dispatch(() =>
        {
            _position = position; _scale = scale;
            // Programmatic reset/velocity callbacks must never become user input.
            if (_enabled && _active && request == 0) _receive(this, new(TrackpadEvent.Values, position.X, position.Y, scale));
        });
    }
    public void InertiaStateEntered(InteractionTracker sender, InteractionTrackerInertiaStateEnteredArgs args) => Dispatch(() =>
    { if (_enabled && _active && args.RequestId == 0) _receive(this, new(TrackpadEvent.Inertia, _position.X, _position.Y, _scale)); });
    public void IdleStateEntered(InteractionTracker sender, InteractionTrackerIdleStateEnteredArgs args) => Dispatch(() =>
    {
        if (!_active || args.RequestId != 0) return;
        _active = false; _receive(this, new(TrackpadEvent.End, _position.X, _position.Y, _scale));
        _tracker.TryUpdatePosition(Vector3.Zero); _tracker.TryUpdateScale(1, Vector3.Zero);
    });
    public void CustomAnimationStateEntered(InteractionTracker sender, InteractionTrackerCustomAnimationStateEnteredArgs args) { }
    public void RequestIgnored(InteractionTracker sender, InteractionTrackerRequestIgnoredArgs args) { }
    public void Dispose()
    {
        if (_disposed) return;
        Cancel(); _disposed = true;
        _tracker.InteractionSources.RemoveAll(); _source.Dispose(); _tracker.Dispose();
    }
}

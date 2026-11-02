// Ported from IINA 1.5.1/build 180 PlayerWindowController.scrollWheel,
// MainWindowController.scrollWheel/handleMagnifyGesture and AppData maps.
// Copyright (c) 2016-2026 lhc and contributors. GPL-3.0; licenses/IINA-GPL-3.0.txt.
namespace Mani_App;

internal enum TrackpadPhase { Idle, Direct, Inertia }
internal enum TrackpadAxis { None, Horizontal, Vertical, Zoom }
internal readonly record struct TrackpadChange(double Seek = 0, double Volume = 0,
    double Zoom = 0, bool BeginSeek = false, bool EndSeek = false, bool BeginZoom = false);

internal sealed class TrackpadGesture
{
    private double _x, _y, _scale;
    private WheelTarget _target;
    private bool _seeking;
    public TrackpadPhase Phase { get; private set; }
    public TrackpadAxis Axis { get; private set; }
    public void Begin(WheelTarget target, double x, double y, double scale)
    {
        _target = target; _x = x; _y = y; _scale = scale;
        _seeking = false; Axis = TrackpadAxis.None; Phase = TrackpadPhase.Direct;
    }
    public TrackpadChange Update(double x, double y, double scale)
    {
        if (Phase == TrackpadPhase.Idle || !double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(scale) || scale <= 0) return default;
        double dx = x - _x, dy = y - _y, ds = scale - _scale;
        _x = x; _y = y; _scale = scale;
        // AppKit separates magnification from scrolling. A Windows pinch may
        // also report translation; once recognized, it owns this interaction.
        if (_target == WheelTarget.Video && (Axis == TrackpadAxis.Zoom || Math.Abs(ds) > .000001))
        {
            bool begin = Axis != TrackpadAxis.Zoom, endSeek = _seeking;
            Axis = TrackpadAxis.Zoom; _seeking = false;
            return new(Zoom: Phase == TrackpadPhase.Direct ? ds : 0, EndSeek: endSeek, BeginZoom: begin);
        }
        if (Phase == TrackpadPhase.Inertia || Axis == TrackpadAxis.None)
            Axis = dx != 0 ? TrackpadAxis.Horizontal : dy != 0 ? TrackpadAxis.Vertical : Axis;
        if (Axis == TrackpadAxis.None) return default;
        // Composition positions describe scroll offsets: positive X is forward;
        // positive Y is downward. AppKit's natural-direction normalization is
        // therefore already represented by X and the negation of Y.
        double delta = Axis == TrackpadAxis.Horizontal ? dx : -dy;
        bool seek = _target == WheelTarget.Timeline || _target == WheelTarget.Video && Axis == TrackpadAxis.Horizontal;
        bool volume = _target == WheelTarget.Volume || _target == WheelTarget.Video && Axis == TrackpadAxis.Vertical;
        bool beginSeek = seek && delta != 0 && Phase == TrackpadPhase.Direct && !_seeking;
        if (beginSeek) _seeking = true;
        // IINA's momentum NSEvents have an empty phase and use the mouse maps.
        return new(Seek: seek ? delta * (Phase == TrackpadPhase.Inertia ? 2 : .25) : 0,
            Volume: volume ? delta * (Phase == TrackpadPhase.Inertia ? 1 : .75) : 0, BeginSeek: beginSeek);
    }
    public TrackpadChange BeginInertia()
    {
        if (Phase != TrackpadPhase.Direct) return default; // Ignore orphan momentum.
        Phase = TrackpadPhase.Inertia;
        bool end = _seeking; _seeking = false;
        return new(EndSeek: end);
    }
    public TrackpadChange End()
    {
        bool end = _seeking; _seeking = false;
        Phase = TrackpadPhase.Idle; Axis = TrackpadAxis.None;
        return new(EndSeek: end);
    }
}

internal readonly record struct TrackpadMedia(long Request, long Entry, int Revision);
internal enum TrackpadCommandKind { PauseForSeek, Seek, Volume, Resume }
internal readonly record struct TrackpadCommand(long Gesture, TrackpadMedia Media, TrackpadCommandKind Kind, double Amount = 0);
internal sealed class TrackpadCommandQueue
{
    private readonly List<TrackpadCommand> _commands = [];
    public void Add(TrackpadCommand command)
    {
        if (command.Kind is TrackpadCommandKind.Seek or TrackpadCommandKind.Volume && _commands.Count > 0 &&
            _commands[^1] is var last && last.Gesture == command.Gesture && last.Media == command.Media && last.Kind == command.Kind)
            _commands[^1] = last with { Amount = last.Amount + command.Amount };
        else _commands.Add(command);
    }
    public TrackpadCommand[] Take() { var result = _commands.ToArray(); _commands.Clear(); return result; }
    public void Clear() => _commands.Clear();
    public void DiscardMotion(long gesture) => _commands.RemoveAll(c => c.Gesture == gesture && c.Kind is TrackpadCommandKind.Seek or TrackpadCommandKind.Volume);
}

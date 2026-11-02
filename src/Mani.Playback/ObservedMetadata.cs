// Property observation pattern adapted from IINA 1.5.1/build 180,
// iina/MPVController.swift (observeProperties/readEvents/handlePropertyChange).
// Copyright (c) 2016 lhc. GPL-3.0; see licenses/IINA-GPL-3.0.txt.
// Windows/C# scope and notification limitations: docs/m1/metadata-polling.md.
namespace Mani.Playback;

// Confined to PlaybackSession's native worker. Never caches a borrowed native pointer.
internal sealed class ObservedMetadata
{
    internal static IReadOnlyList<string> Names { get; } = Array.AsReadOnly(new[] {
        "path", "media-title", "current-vo", "hwdec-current", "video-format",
        "video-params/primaries", "video-params/gamma", "current-ao", "audio-device", "audio-buffer",
        "pause", "idle-active", "paused-for-cache", "seeking", "eof-reached", "speed", "volume", "mute",
        "loop-file", "loop-playlist", "ab-loop-a", "ab-loop-b"
    });
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private long? _refreshedAt;
    internal const long ReconcileMilliseconds = 1000;

    internal void BeginRead(long now)
    {
        // client.h does not guarantee notifications for every property transition.
        // Reconcile once a second, also after a quiet diagnostic interval or clock reset.
        if (_refreshedAt is not long last || now < last || now - last >= ReconcileMilliseconds)
        {
            Invalidate();
            _refreshedAt = now;
        }
    }
    internal void Invalidate() => _values.Clear();
    internal string? Peek(string name) => _values.GetValueOrDefault(name);
    internal string Read(string name, Func<string, string> query)
    {
        if (!Names.Contains(name)) throw new ArgumentException("Not an observed metadata property.", nameof(name));
        if (!_values.TryGetValue(name, out var value)) _values[name] = value = query(name);
        return value;
    }
    internal void Handle(ulong subscription, MpvNative.PropertyEvent property)
        => Handle(subscription, MpvData.CopyProperty(property));
    internal void Handle(ulong subscription, MpvProperty property)
    {
        if (subscription == 0 || subscription > (ulong)Names.Count) return;
        string name = Names[(int)subscription - 1];
        if (property.Name != name) return;
        if (property.Format == 0) // NONE: data is invalid; never dereference it.
            _values[name] = "";
        else if (property.Format == 1 && property.Value is string value)
            _values[name] = value;
        else
            _values.Remove(name); // Malformed/unexpected payload: next poll queries directly.
    }
}

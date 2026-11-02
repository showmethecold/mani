using System.Collections.Concurrent;
using System.Threading.Channels;
using Mani.Playback;

namespace Mani.UnitTests;

internal sealed class FakeMpvClient : IMpvClient
{
    internal sealed record Command(ulong Id, string[] Arguments);
    internal readonly ConcurrentDictionary<string, object> Properties = new();
    internal readonly ConcurrentQueue<MpvEvent> Events = new();
    internal readonly ConcurrentQueue<ulong> Aborted = new();
    internal readonly Channel<Command> Commands = Channel.CreateUnbounded<Command>();
    internal bool Disposed;
    internal Exception? SendFailure;
    internal Exception? SetFailure;
    internal readonly ConcurrentDictionary<int, byte> Threads = new();
    private void Check()
    {
        if (Disposed) throw new ObjectDisposedException(nameof(FakeMpvClient));
        Threads.TryAdd(Environment.CurrentManagedThreadId, 0);
    }
    public void Initialize(PlaybackOptions options) { Check(); Properties["idle-active"] = true; }
    public MpvEvent WaitEvent() { Check(); return Events.TryDequeue(out var e) ? e : new(0); }
    public void Send(ulong request, string[] arguments)
    {
        Check();
        if (SendFailure is { } error) { SendFailure = null; throw error; }
        Commands.Writer.TryWrite(new(request, arguments));
    }
    public void Abort(ulong request) { Check(); Aborted.Enqueue(request); }
    public void Set(string name, string value) { Check(); if (SetFailure is { } error) { SetFailure = null; throw error; } Properties[name] = value; }
    public string Get(string name) { Check(); return Properties.GetValueOrDefault(name) is bool flag ? flag ? "yes" : "no" : Properties.GetValueOrDefault(name)?.ToString() ?? ""; }
    public bool Flag(string name) { Check(); return Properties.GetValueOrDefault(name) is true or "yes"; }
    public double Number(string name) { Check(); return double.TryParse(Get(name), out var value) ? value : 0; }
    public long Integer(string name) { Check(); return long.TryParse(Get(name), out var value) ? value : 0; }
    public object? Node(string name) { Check(); return Properties.GetValueOrDefault(name); }
    public void Dispose() { Check(); Disposed = true; }
    internal Task<Command> Next() => Commands.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    internal void Reply(Command command, int error = 0) => Events.Enqueue(new(5, error, command.Id, Message: error < 0 ? "Command failed" : ""));
    internal void Playlist(long entry) => Properties["playlist"] = new object?[] { new Dictionary<string, object?> { ["id"] = entry } };
    internal void Loaded(long entry, string source)
    {
        Properties["path"] = source;
        Properties["media-title"] = source;
        Properties["idle-active"] = false;
        Properties["duration"] = 60d;
        Events.Enqueue(new(6, EntryId: entry));
        Events.Enqueue(new(8));
    }
    internal Task<PlaybackSession> Create() => PlaybackSession.CreateAsync(new("", "", "", Headless: true), this);
}

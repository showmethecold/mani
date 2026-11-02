namespace Mani.Playback;

// All calls, including Dispose, are confined to the session's one worker.
// This seam permits deterministic lifecycle tests without loading a native DLL.
internal interface IMpvClient : IDisposable
{
    void Initialize(PlaybackOptions options);
    MpvEvent WaitEvent();
    void Send(ulong request, string[] arguments);
    void Abort(ulong request);
    void Set(string name, string value);
    string Get(string name);
    bool Flag(string name);
    double Number(string name);
    long Integer(string name);
    object? Node(string name);
}

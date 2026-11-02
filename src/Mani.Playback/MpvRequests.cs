namespace Mani.Playback;

// Worker-confined correlation of native replies. Cancellation never calls native code
// from a token callback; the worker observes it before its next dispatch/pump.
internal sealed class MpvRequests(IMpvClient client, Action<string, object>? trace = null)
{
    private sealed class Pending(CancellationToken token, Action<MpvEvent>? reply)
    {
        internal readonly TaskCompletionSource<object?> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly CancellationToken Token = token;
        internal readonly Action<MpvEvent>? Reply = reply;
        internal bool Aborted;
    }
    private readonly Dictionary<ulong, Pending> _pending = [];
    private ulong _next;

    internal Task<object?> Send(string[] arguments, CancellationToken token = default, Action<MpvEvent>? reply = null)
    {
        if (token.IsCancellationRequested) return Task.FromCanceled<object?>(token);
        if (_pending.Count >= 1024) throw new InvalidOperationException("Too many pending playback commands.");
        var item = new Pending(token, reply);
        ulong id = checked(++_next);
        _pending.Add(id, item);
        trace?.Invoke("command-submitted", new { requestId = id, command = arguments.FirstOrDefault() });
        try { client.Send(id, arguments); }
        catch (Exception error)
        {
            _pending.Remove(id);
            item.Completion.TrySetException(error);
            trace?.Invoke("command-rejected", new { requestId = id, error = error.GetType().Name });
            reply?.Invoke(new(5, -1, id, Message: error.Message));
        }
        return item.Completion.Task;
    }
    internal void Complete(MpvEvent value)
    {
        if (!_pending.Remove(value.Reply, out var item)) return;
        trace?.Invoke("command-replied", new { requestId = value.Reply, code = value.Error, cancelled = item.Token.IsCancellationRequested });
        // Native completion can race token cancellation. Whichever the worker observes
        // first wins; a cancelled caller never consumes a late success result.
        if (item.Token.IsCancellationRequested) item.Completion.TrySetCanceled(item.Token);
        else if (value.Error < 0) item.Completion.TrySetException(new InvalidOperationException(value.Message));
        else item.Completion.TrySetResult(value.Result);
        item.Reply?.Invoke(value);
    }
    internal void CancelPending()
    {
        foreach (var (id, item) in _pending)
        {
            if (item.Aborted || !item.Token.IsCancellationRequested) continue;
            item.Aborted = true;
            client.Abort(id);
            trace?.Invoke("command-cancelled", new { requestId = id });
            item.Completion.TrySetCanceled(item.Token);
        }
    }
    internal void Close(Exception error)
    {
        foreach (var (id, item) in _pending)
        {
            try { if (!item.Aborted) client.Abort(id); }
            catch (Exception abortError) { trace?.Invoke("command-abort-failed", new { requestId = id, error = abortError.GetType().Name }); }
            finally { item.Completion.TrySetException(error); }
            trace?.Invoke("command-closed", new { requestId = id });
        }
        _pending.Clear();
    }
}

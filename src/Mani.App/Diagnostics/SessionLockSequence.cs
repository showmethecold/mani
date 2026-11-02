namespace Mani_App.Diagnostics;

// Windows-only diagnostic evidence. No pause/resume policy is applied here.
internal sealed record SessionLockEvent(int Sequence, int Code, int SessionId, long ElapsedMs, DateTimeOffset AtUtc);
internal sealed class SessionLockSequence(int sessionId)
{
    public int SessionId { get; } = sessionId;
    public bool Locked { get; private set; }
    public string? Failure { get; private set; }
    public List<SessionLockEvent> Events { get; } = [];
    public void Receive(int code, int incomingSession, long elapsedMs)
    {
        if (incomingSession != SessionId) return;
        if (code == 15) return; // WTS_SESSION_DESKTOP_READY is not a lock transition.
        if (code is not (7 or 8)) { Fail("Unexpected Windows session change: " + code); return; }
        if ((code == 7) == Locked) Fail("Duplicate or out-of-order lock/unlock notification.");
        if (elapsedMs < 0 || (Events.Count > 0 && elapsedMs < Events[^1].ElapsedMs)) Fail("Session notification clock moved backwards.");
        Events.Add(new(Events.Count + 1, code, incomingSession, elapsedMs, DateTimeOffset.UtcNow));
        Locked = code == 7;
    }
    public void Fail(string reason) => Failure ??= reason;
    public void RequireHealthy()
    {
        if (Failure is not null) throw new InvalidOperationException(Failure);
    }
    public bool CycleCompleted(int priorEvents)
    {
        RequireHealthy();
        if (priorEvents is not (0 or 2) || Events.Count < priorEvents || Events.Count > priorEvents + 2)
            throw new InvalidOperationException("Unexpected lock/unlock cycle count.");
        if (Events.Count != priorEvents + 2) return false;
        if (Locked || Events[priorEvents].Code != 7 || Events[priorEvents + 1].Code != 8 ||
            Events[priorEvents + 1].ElapsedMs - Events[priorEvents].ElapsedMs < 5000)
            throw new InvalidOperationException("Keep Windows locked for at least five seconds (ten seconds recommended).");
        return true;
    }
}

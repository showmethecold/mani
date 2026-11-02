using Mani.Playback;

namespace Mani_App.Diagnostics;

internal sealed record SleepWakeEvent(int Sequence, int Code, long ElapsedMs, DateTimeOffset AtUtc);
// One diagnostic cycle; production policy is independent of this evidence collector.
internal sealed class SleepWakeSequence(Func<long> elapsed)
{
    public List<SleepWakeEvent> Events { get; } = [];
    public string? Failure { get; private set; }
    public SleepPauseResult? PauseResult { get; private set; }
    public long? PauseCompletedMs { get; private set; }
    public long? ConfirmedMs { get; private set; }
    public bool Armed { get; set; }
    public bool Complete => Failure is null && Events.Count == 3;
    public void Fail(string error) => Failure ??= error;
    public void Receive(int code)
    {
        if (code is not (4 or 18 or 7)) return;
        long ms = elapsed();
        int expected = Events.Count switch { 0 => 4, 1 => 18, 2 => 7, _ => -1 };
        if (!Armed || code != expected || ms < 0 || (Events.Count > 0 && ms < Events[^1].ElapsedMs))
            Fail("Unexpected sleep/wake order or timing. Use Sleep only when prompted.");
        Events.Add(new(Events.Count + 1, code, ms, DateTimeOffset.UtcNow));
        if (code == 18 && Events.Count == 2 && ms - Events[0].ElapsedMs < 5000)
            Fail("Keep the computer asleep for at least five seconds (ten recommended).");
    }
    public void Paused(SleepPauseResult result)
    {
        PauseResult = result; PauseCompletedMs = elapsed();
        if (!result.Applied || !result.Paused) Fail("Sleep notification did not pause an active file.");
    }
    public bool Confirm()
    {
        RequireHealthy();
        if (!Complete || PauseCompletedMs is null) return false;
        ConfirmedMs = elapsed(); return true;
    }
    public void RequireHealthy()
    {
        if (Failure is not null) throw new InvalidOperationException(Failure);
    }
}


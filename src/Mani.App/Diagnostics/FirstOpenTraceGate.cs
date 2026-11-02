using System.Diagnostics;
using System.Text.Json;

namespace Mani_App.Diagnostics;

// Windows diagnostic handshake only. No replacement for IINA/libmpv playback logic.
internal sealed record FirstOpenTraceGate(string scope, string automationId, TimingController collector,
    TimingController player, TimingController recorder, TimingController guard,
    long startBeforeQpc, long startAfterQpc, long deadlineQpc, long frequency)
{
    public void Validate(MediaTimingRequest request, TimingController host, long nowQpc, long readyQpc, bool checkProcesses = true)
    {
        if (!request.IsFirstOpenTrace || scope != MediaTimingRequest.FirstOpenTraceScope ||
            automationId != request.automationId || collector != request.traceController || player != host ||
            frequency != Stopwatch.Frequency || readyQpc <= 0 || startBeforeQpc < readyQpc ||
            startAfterQpc < startBeforeQpc || nowQpc < startAfterQpc || nowQpc >= deadlineQpc ||
            deadlineQpc <= startAfterQpc || deadlineQpc - startBeforeQpc != 60 * frequency ||
            recorder is null || guard is null || collector is null || player is null ||
            new[] { collector.pid, player.pid, recorder.pid, guard.pid }.Distinct().Count() != 4)
            throw new InvalidDataException("Trace gate identity, clock or recording interval differs.");
        if (checkProcesses)
        {
            collector.RequireAlive(); recorder.RequireAlive(); guard.RequireAlive(); player.RequireAlive();
        }
    }
}

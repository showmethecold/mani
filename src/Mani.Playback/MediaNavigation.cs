// IINA PlayerCore.playChapter and default chapter/edition commands.
// Copyright (c) 2016-2026 lhc and contributors. GPL-3.0; licenses/IINA-GPL-3.0.txt.
using System.Globalization;
namespace Mani.Playback;

public sealed partial class PlaybackSession
{
    private readonly SemaphoreSlim _resumeEdits = new(1, 1);
    private long? _resumeBlockedGeneration;
    public async Task SaveResumeAsync(long request, long entry)
    {
        await _resumeEdits.WaitAsync().ConfigureAwait(false);
        try
        {
            await Invoke(() =>
            {
                RequireMedia(request, entry);
                if (_resumeBlockedGeneration == request) throw new OperationCanceledException("Edition is changing.");
                return SendCommand(["write-watch-later-config"]);
            }).Unwrap().ConfigureAwait(false);
        }
        finally { _resumeEdits.Release(); }
    }
    private void RequireMedia(long request, long entry)
    {
        Pump();
        if (_loading || request != _generation || entry != _engineEntry || _engineGeneration != _generation)
            throw new OperationCanceledException("Media changed before navigation.");
    }
    public Task SetMediaPropertyAsync(long request, long entry, string property, string value)
    {
        if (property != "edition") throw new ArgumentException("Unsupported navigation property.", nameof(property));
        return ChangeEditionAsync(request, entry, ["set", property, value]);
    }
    public Task ChangeMediaPropertyAsync(long request, long entry, string property, string value)
    {
        if (property == "edition" && value == "cycle") return ChangeEditionAsync(request, entry, ["cycle", "edition"]);
        if (property != "chapter" || value is not ("1" or "-1")) throw new ArgumentException("Unsupported navigation command.");
        return Invoke(() => { RequireMedia(request, entry); return SendCommand(["add", "chapter", value]); }).Unwrap();
    }
    private async Task ChangeEditionAsync(long request, long entry, string[] command)
    {
        // A periodic checkpoint contains the old edition. mpv rereads that file
        // on edition reload, otherwise silently undoing the explicit selection.
        // IINA's synchronous commands preserve order. Async libmpv requires a
        // reply between commands and exclusion of concurrent checkpoint writes.
        await _resumeEdits.WaitAsync().ConfigureAwait(false);
        try
        {
            await Invoke(() => { RequireMedia(request, entry); return SendCommand(["delete-watch-later-config"]); }).Unwrap().ConfigureAwait(false);
            await Invoke(() =>
            {
                RequireMedia(request, entry);
                _resumeBlockedGeneration = request;
                try { return SendCommand(command, reply: result => { if (result.Error < 0) _resumeBlockedGeneration = null; }); }
                catch { _resumeBlockedGeneration = null; throw; }
            }).Unwrap().ConfigureAwait(false);
        }
        finally { _resumeEdits.Release(); }
    }
    public async Task PlayChapterAsync(long request, long entry, double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0) throw new ArgumentOutOfRangeException(nameof(seconds));
        await Invoke(() => { RequireMedia(request, entry); return SendCommand(["seek", seconds.ToString(CultureInfo.InvariantCulture), "absolute"]); }).Unwrap().ConfigureAwait(false);
        await Invoke(() => { RequireMedia(request, entry); SetNative("pause", "no"); return true; }).ConfigureAwait(false);
    }
}

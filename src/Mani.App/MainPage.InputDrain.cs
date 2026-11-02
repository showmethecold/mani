namespace Mani_App;

public sealed partial class MainPage
{
    private Task _inputTask = Task.CompletedTask;
    private bool _inputRequested;

    // IINA sends slider/key actions directly to the engine. Keep Mani's latest-value
    // coalescing, but dispatch independently of the display's 200 ms polling clock.
    private void RequestInputDrain()
    {
        if (_closing || _sessionUnavailable || _session is null) return;
        _inputRequested = true;
        if (_inputTask.IsCompleted) _inputTask = DrainInputAsync();
    }

    private async Task DrainInputAsync()
    {
        // Ensure assignment of _inputTask precedes event reentrancy.
        await Task.Yield();
        _playbackRevision++;
        try
        {
            while (_inputRequested && !_closing && !_sessionUnavailable && _session is { } session)
            {
                _inputRequested = false;
                if (_pendingSeek is double seek)
                {
                    _pendingSeek = null;
                    await SeekWithOsdAsync(session, seek);
                }
                if (_pendingVolume is double volume)
                {
                    _pendingVolume = null;
                    await session.SetVolumeAsync(volume);
                    Trace("volume", new { volume });
                }
                await ApplyWheelInputAsync();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { ShowError(error); }
        finally
        {
            _playbackRevision++;
            if (_inputRequested && !_closing) DispatcherQueue.TryEnqueue(RequestInputDrain);
        }
    }
}

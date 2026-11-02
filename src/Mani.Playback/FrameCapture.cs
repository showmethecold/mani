using System.Diagnostics;
using System.Globalization;

namespace Mani.Playback;

public static class FrameCapture
{
    // An independent client: neither seeking nor setting properties on the main player.
    // CPU pixels are used only for this explicitly requested still image, never for playback.
    public static async Task<string> CaptureAsync(PlaybackOptions options, string mediaPath, double seconds,
        string destination, CancellationToken cancellationToken = default, int thumbnailWidth = 0)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!double.IsFinite(seconds) || seconds < 0) throw new ArgumentOutOfRangeException(nameof(seconds));
        await using var capture = await PlaybackSession.CreateAsync(options with { Headless = true, KeepOpen = true, LoadAutoProfiles = false }).ConfigureAwait(false);
        await ConfigureAsync(capture, thumbnailWidth).ConfigureAwait(false);
        return await CaptureAsync(capture, mediaPath, seconds, destination, cancellationToken, reload: true).ConfigureAwait(false);
    }

    internal static async Task ConfigureAsync(PlaybackSession capture, int thumbnailWidth)
    {
        await capture.SetAsync("screenshot-sw", "yes").ConfigureAwait(false);
        await capture.SetAsync("screenshot-high-bit-depth", thumbnailWidth > 0 ? "no" : "yes").ConfigureAwait(false);
        if (thumbnailWidth <= 0) return;
        await capture.SetAsync("vd-lavc-threads", "1").ConfigureAwait(false);
        await capture.SetAsync("audio", "no").ConfigureAwait(false);
        await capture.SetAsync("vf", $"scale=w={thumbnailWidth}:h={thumbnailWidth}:force_original_aspect_ratio=decrease").ConfigureAwait(false);
    }

    internal static async Task<string> CaptureAsync(PlaybackSession capture, string mediaPath, double seconds,
        string destination, CancellationToken cancellationToken, bool reload, Action? loaded = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!double.IsFinite(seconds) || seconds < 0) throw new ArgumentOutOfRangeException(nameof(seconds));
        destination = Path.GetFullPath(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".png";
        try
        {
            if (reload)
                await capture.LoadAsync(mediaPath, paused: true, cancellationToken)
                    .WaitAsync(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
            var timer = Stopwatch.StartNew();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var state = await capture.ReadAsync().ConfigureAwait(false);
                if (!state.Loading && state.Path.Length > 0 && state.VideoFormat.Length > 0) break;
                if (state.LoadFailure.Length > 0) throw new InvalidOperationException(state.LoadFailure);
                if (timer.Elapsed.TotalSeconds > 15) throw new TimeoutException("Frame capture load timed out.");
                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            }
            loaded?.Invoke();
            // Once sent, drain native seek/screenshot replies before another
            // request can reuse the decoder or remove its temporary output.
            cancellationToken.ThrowIfCancellationRequested();
            await capture.SeekAsync(seconds).WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            timer.Restart();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var values = await capture.GetPropertiesAsync("seeking", "time-pos").ConfigureAwait(false);
                if (values["seeking"] == "no" && double.TryParse(values["time-pos"], CultureInfo.InvariantCulture, out var position)
                    && Math.Abs(position - seconds) < 0.5) break;
                if (timer.Elapsed.TotalSeconds > 15) throw new TimeoutException("Frame capture seek timed out.");
                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            await capture.CommandAsync("screenshot-to-file", temporary, "video").WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(temporary) || new FileInfo(temporary).Length == 0) throw new IOException("No frame was produced.");
            File.Move(temporary, destination); // Never overwrite a user's existing image.
            return destination;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

// IINA 1.5.1/build 180 FFmpegController (100 samples, serial cancellation),
// ThumbnailCache (version/size/mtime invalidation, oldest-first budget).
// Copyright (c) 2016-2026 lhc and contributors. GPL-3.0; licenses/IINA-GPL-3.0.txt.
// Reuses the pinned libmpv independent FrameCapture decoder on Windows.
using System.Security.Cryptography;
using System.Text;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Mani.Playback;

public sealed class ThumbnailPreviewCache(string directory, Func<string, double, string, CancellationToken, Task<string>> capture,
    long budgetBytes = 500L * 1024 * 1024)
{
    private static readonly SemaphoreSlim Decoder = new(1, 1);
    private sealed class CacheEpoch { public long Value; }
    private static readonly ConcurrentDictionary<string, CacheEpoch> Epochs = new(StringComparer.OrdinalIgnoreCase);
    private readonly CacheEpoch _epoch = Epochs.GetOrAdd(Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)), _ => new());
    private static readonly Regex OwnedName = new(@"\A[0-9a-f]{64}-(?:[0-9]|[1-9][0-9])\.png\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    public long Generation => Volatile.Read(ref _epoch.Value);
    private IEnumerable<FileInfo> OwnedFiles() => Directory.Exists(directory)
        ? new DirectoryInfo(directory).EnumerateFiles("*.png").Where(file => OwnedName.IsMatch(file.Name) && (file.Attributes & FileAttributes.ReparsePoint) == 0) : [];
    public Task<long> SizeAsync(CancellationToken token = default) => Task.Run(() =>
    {
        long total = 0;
        foreach (var file in OwnedFiles())
        {
            token.ThrowIfCancellationRequested();
            try { total += file.Length; }
            catch (FileNotFoundException) { } // Concurrent cache cleanup is harmless.
        }
        return total;
    }, token);
    public async Task ClearAsync(CancellationToken token = default)
    {
        // Clear invalidates already queued/running requests in every window using
        // this directory. The decoder gate prevents a late writer recreating them.
        Interlocked.Increment(ref _epoch.Value);
        await Decoder.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await Task.Run(() => { foreach (var file in OwnedFiles()) { token.ThrowIfCancellationRequested(); file.Delete(); } }, token).ConfigureAwait(false);
        }
        finally { Decoder.Release(); }
    }
    public static int SampleIndex(double seconds, double duration) => !double.IsFinite(seconds) || !double.IsFinite(duration) || duration <= 0
        ? 0 : Math.Clamp((int)Math.Floor(Math.Max(0, seconds) / duration * 100), 0, 99);
    public static string Fingerprint(string source)
    {
        var info = new FileInfo(source);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"v1/240/{info.FullName.ToUpperInvariant()}/{info.Length}/{info.LastWriteTimeUtc.Ticks}")));
    }
    public async Task<string?> GetAsync(string source, double seconds, double duration, CancellationToken token)
    {
        if (!Path.IsPathFullyQualified(source) || source.StartsWith(@"\\") || duration <= 0 || !double.IsFinite(duration)) return null;
        int index = SampleIndex(seconds, duration);
        long generation = Generation;
        await Decoder.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await Task.Run(async () =>
            {
                token.ThrowIfCancellationRequested();
                if (generation != Generation || new DriveInfo(Path.GetPathRoot(source)!).DriveType == DriveType.Network || !File.Exists(source)) return null;
                Directory.CreateDirectory(directory);
                string key = Fingerprint(source), target = Path.Combine(directory, key + "-" + index + ".png");
                if (File.Exists(target))
                {
                    await using var stream = File.OpenRead(target);
                    byte[] header = new byte[24], end = new byte[12]; int read = await stream.ReadAsync(header, token);
                    if (stream.Length >= 45) { stream.Seek(-12, SeekOrigin.End); await stream.ReadExactlyAsync(end, token); }
                    if (read == 24 && header.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) &&
                        header.AsSpan(12, 4).SequenceEqual("IHDR"u8) && end.AsSpan(4, 4).SequenceEqual("IEND"u8) &&
                        System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(16, 4)) is > 0 and <= 240 &&
                        System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(20, 4)) is > 0 and <= 240)
                    { File.SetLastWriteTimeUtc(target, DateTime.UtcNow); return generation == Generation ? target : null; }
                }
                if (File.Exists(target)) File.Delete(target);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(20));
                try
                {
                    await capture(source, index * duration / 100, target, deadline.Token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                }
                catch { if (File.Exists(target)) File.Delete(target); throw; }
                if (generation != Generation) { File.Delete(target); return null; }
                if (Fingerprint(source) != key) { File.Delete(target); return null; }
                Trim();
                return File.Exists(target) ? target : null;
            }, token).ConfigureAwait(false);
        }
        finally { Decoder.Release(); }
    }
    private void Trim()
    {
        var files = OwnedFiles().OrderBy(f => f.LastWriteTimeUtc).ToArray();
        long size = files.Sum(f => f.Length);
        foreach (var file in files)
        {
            if (size <= budgetBytes) break;
            // Match the same ownership boundary as usage reporting and clearing.
            size -= file.Length; file.Delete();
        }
    }
}

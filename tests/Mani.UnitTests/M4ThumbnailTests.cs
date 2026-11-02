using Mani.Playback;
namespace Mani.UnitTests;

public sealed class M4ThumbnailTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mani-thumbnail-" + Guid.NewGuid().ToString("N"));
    private string Media => Path.Combine(_directory, "media.mkv");
    private string Cache => Path.Combine(_directory, "cache");
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a1xkAAAAASUVORK5CYII=");
    public M4ThumbnailTests() { Directory.CreateDirectory(_directory); File.WriteAllText(Media, "media"); }
    public void Dispose() => Directory.Delete(_directory, true);
    [Theory]
    [InlineData(-1, 100, 0)] [InlineData(50, 100, 50)] [InlineData(100, 100, 99)] [InlineData(double.NaN, 100, 0)]
    public void SamplingIsBounded(double seconds, double duration, int expected) => Assert.Equal(expected, ThumbnailPreviewCache.SampleIndex(seconds, duration));
    [Fact]
    public async Task CacheUsesFileFingerprintAndAvoidsRepeatedDecode()
    {
        int calls = 0;
        var cache = new ThumbnailPreviewCache(Cache, async (_, _, destination, token) => { calls++; await File.WriteAllBytesAsync(destination, Png, token); return destination; });
        var first = await cache.GetAsync(Media, 50, 100, TestContext.Current.CancellationToken);
        Assert.Equal(first, await cache.GetAsync(Media, 50.1, 100, TestContext.Current.CancellationToken)); Assert.Equal(1, calls);
        await File.AppendAllTextAsync(Media, "changed", TestContext.Current.CancellationToken);
        Assert.NotEqual(first, await cache.GetAsync(Media, 50, 100, TestContext.Current.CancellationToken)); Assert.Equal(2, calls);
    }
    [Fact]
    public async Task CancellationDrainsDecoderSlotAndNoOldResultEscapes()
    {
        using var cancel = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cache = new ThumbnailPreviewCache(Cache, async (_, _, destination, token) => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return destination; });
        var pending = cache.GetAsync(Media, 5, 100, cancel.Token); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        var next = new ThumbnailPreviewCache(Cache, async (_, _, destination, token) => { await File.WriteAllBytesAsync(destination, Png, token); return destination; });
        Assert.NotNull(await next.GetAsync(Media, 10, 100, TestContext.Current.CancellationToken));
    }
    [Fact]
    public async Task BudgetEvictsOldestAndCorruptionRegenerates()
    {
        int calls = 0;
        var cache = new ThumbnailPreviewCache(Cache, async (_, _, destination, token) => { calls++; await File.WriteAllBytesAsync(destination, Png, token); return destination; }, budgetBytes: Png.Length);
        var old = await cache.GetAsync(Media, 1, 100, TestContext.Current.CancellationToken);
        var current = await cache.GetAsync(Media, 20, 100, TestContext.Current.CancellationToken);
        Assert.False(File.Exists(old)); Assert.True(File.Exists(current));
        await File.WriteAllTextAsync(current!, "corrupt", TestContext.Current.CancellationToken);
        Assert.Equal(current, await cache.GetAsync(Media, 20, 100, TestContext.Current.CancellationToken)); Assert.Equal(3, calls);
        Assert.True(Directory.GetFiles(Cache, "*.png").Sum(f => new FileInfo(f).Length) <= Png.Length);
    }
    [Fact]
    public async Task NetworkSourcesNeverReachDecoder()
    {
        var cache = new ThumbnailPreviewCache(Cache, (_, _, _, _) => throw new InvalidOperationException("must not decode"));
        Assert.Null(await cache.GetAsync("https://test/a?token=secret", 1, 100, TestContext.Current.CancellationToken));
    }
    [Fact]
    public async Task TruncatedPngHeaderDoesNotCountAsACacheHit()
    {
        int calls = 0;
        var cache = new ThumbnailPreviewCache(Cache, async (_, _, target, token) => { calls++; await File.WriteAllBytesAsync(target, Png, token); return target; });
        string path = (await cache.GetAsync(Media, 1, 100, TestContext.Current.CancellationToken))!;
        await File.WriteAllBytesAsync(path, Png[..24], TestContext.Current.CancellationToken);
        await cache.GetAsync(Media, 1, 100, TestContext.Current.CancellationToken);
        Assert.Equal(2, calls);
    }
}

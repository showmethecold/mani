using System.Diagnostics;
using System.Text.Json;

namespace Mani_App.Diagnostics;

// File handshakes use the app's own probe folder (MSIX redirects it for external readers).
// No OS resource counters are read inside the playback sampling loop.
internal sealed class ExternalResourceProbe
{
    public const string Profile = "external-win32-v1";
    private readonly string _directory;
    private readonly int _pid = Environment.ProcessId;
    private readonly long _creation;
    private string? _samplerHash;
    public string RequestId { get; } = Guid.NewGuid().ToString("N");
    public long CreationFileTime => _creation;

    private ExternalResourceProbe(string directory)
    {
        _directory = directory;
        using var process = Process.GetCurrentProcess();
        _creation = process.StartTime.ToUniversalTime().ToFileTimeUtc();
    }
    public static async Task<ExternalResourceProbe> PrepareAsync(string directory, string baselineCase,
        CancellationToken cancellation, TimeSpan? timeout = null, int? requestedDurationSeconds = null, string? automationId = null)
    {
        var probe = new ExternalResourceProbe(directory);
        try
        {
            await probe.PhaseAsync("waiting");
            await probe.WriteAsync("external-request.json", new { schema_version = 1, resourceSampler = Profile,
                requestId = probe.RequestId, hostProcessId = probe._pid, creationFileTime = probe._creation,
                requestedAtUtc = DateTimeOffset.UtcNow, baselineCase, requestedDurationSeconds, automationId });
            var ready = await probe.WaitAsync("external-ready.json", timeout ?? TimeSpan.FromSeconds(30), cancellation);
            probe.ValidateIdentity(ready);
            if (ready.GetProperty("status").GetString() != "ready")
                throw new InvalidOperationException("External collector could not start. Check the collector terminal.");
            probe._samplerHash = ready.GetProperty("samplerSha256").GetString();
            if (probe._samplerHash is not { Length: 64 }) throw new InvalidDataException("Missing sampler identity.");
            return probe;
        }
        catch
        {
            await probe.CancelAsync();
            throw;
        }
    }
    public Task StartAsync() => PhaseAsync("playing");
    public Task StopAsync() => PhaseAsync("done");
    public async Task<JsonElement> CompleteAsync(CancellationToken cancellation)
    {
        var report = await WaitAsync("external-resources.json", TimeSpan.FromSeconds(10), cancellation);
        ValidateIdentity(report);
        if (report.GetProperty("status").GetString() != "completed" || report.GetProperty("errors").GetArrayLength() != 0 ||
            report.GetProperty("samplerSha256").GetString() != _samplerHash || report.GetProperty("samples").GetArrayLength() < 2)
            throw new InvalidDataException("External resource report is incomplete or has a different sampler.");
        return report;
    }
    public async Task CheckHeartbeatAsync(CancellationToken cancellation)
    {
        var path = Path.Combine(_directory, "external-heartbeat.json");
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path, cancellation));
        var heartbeat = document.RootElement;
        ValidateIdentity(heartbeat);
        var age = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency - heartbeat.GetProperty("qpcSeconds").GetDouble();
        if (!double.IsFinite(age) || age < -1 || age > 15 || heartbeat.GetProperty("status").GetString() != "collecting")
            throw new IOException("External collector stopped responding. Resource measurement is incomplete.");
    }
    public Task CancelAsync() => PhaseAsync("cancelled");
    private Task PhaseAsync(string phase) => WriteAsync("external-phase.json", new { requestId = RequestId, phase,
        qpcSeconds = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency });
    private void ValidateIdentity(JsonElement value)
    {
        if (value.GetProperty("requestId").GetString() != RequestId || value.GetProperty("hostProcessId").GetInt32() != _pid ||
            value.GetProperty("creationFileTime").GetInt64() != _creation || value.GetProperty("resourceSampler").GetString() != Profile)
            throw new InvalidDataException("External resource report belongs to another process or request.");
    }
    private async Task WriteAsync(string name, object value)
    {
        var path = Path.Combine(_directory, name);
        var temporary = path + ".host-tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(value));
        for (int attempt = 0; ; attempt++)
        {
            try { File.Move(temporary, path, overwrite: true); return; }
            catch (IOException) when (attempt < 9) { await Task.Delay(20); }
        }
    }
    private async Task<JsonElement> WaitAsync(string name, TimeSpan timeout, CancellationToken cancellation)
    {
        var timer = Stopwatch.StartNew();
        var path = Path.Combine(_directory, name);
        while (timer.Elapsed < timeout)
        {
            cancellation.ThrowIfCancellationRequested();
            if (File.Exists(path))
            {
                using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path, cancellation));
                return document.RootElement.Clone();
            }
            await Task.Delay(100, cancellation);
        }
        throw new TimeoutException("External collector did not respond. Start the matching collector command before starting this test.");
    }
}

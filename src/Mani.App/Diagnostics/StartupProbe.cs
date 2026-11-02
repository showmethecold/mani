using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Mani.Playback;

namespace Mani_App.Diagnostics;

// Reuses Mani's one-shot activation and atomic reports; no IINA equivalent for Windows startup inventory.
internal sealed record StartupRequest(int schema_version, string automationId, int iteration, DateTimeOffset requestedAtUtc)
{
    public static StartupRequest Claim(string directory)
    {
        foreach (string other in new[] { "pending-long-playback.json", "pending-lifecycle.json", "pending-overhead.json" })
            if (File.Exists(Path.Combine(directory, other))) throw new InvalidOperationException("Another unattended request is pending.");
        string pending = Path.Combine(directory, "pending-startup.json");
        string claimed = Path.Combine(directory, "claimed-startup-" + Guid.NewGuid().ToString("N") + ".json");
        File.Move(pending, claimed);
        var request = JsonSerializer.Deserialize<StartupRequest>(File.ReadAllText(claimed))
            ?? throw new InvalidDataException("Empty startup request.");
        if (request.schema_version != 1 || request.iteration is < 1 or > 3 ||
            !Guid.TryParseExact(request.automationId, "N", out var id) || id.ToString("N") != request.automationId)
            throw new InvalidDataException("Invalid startup request.");
        double age = (DateTimeOffset.UtcNow - request.requestedAtUtc).TotalSeconds;
        if (age < -5 || age > 600) throw new InvalidDataException("Startup request expired.");
        return request;
    }
}

internal sealed class StartupProbe
{
    public static readonly string[] Labels = ["before-engine", "engine-ready", "idle-settled", "after-shutdown"];
    public StartupRequest Request { get; }
    public bool CloseRequested { get; private set; }
    public bool ObserverAttached { get; private set; }
    private readonly string _tests, _session, _directory;
    private readonly long _creation;
    private readonly object _build;
    private readonly List<object> _snapshots = [];
    private string _status = "running";
    private string? _failure;

    public StartupProbe(StartupRequest request, string tests, string session, string nativePath)
    {
        Request = request; _tests = tests; _session = session;
        _directory = Path.Combine(session, "startup-" + request.automationId);
        Directory.CreateDirectory(_directory);
        using var process = Process.GetCurrentProcess();
        _creation = process.StartTime.ToUniversalTime().ToFileTimeUtc();
        static string Hash(string path) { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant(); }
        _build = new { hostSha256 = Hash(Assembly.GetExecutingAssembly().Location),
            playbackSha256 = Hash(typeof(PlaybackSession).Assembly.Location), libmpvSha256 = Hash(nativePath) };
    }

    internal static void WriteAtomic(string path, object value)
    {
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
        // A Windows reader may briefly hold the destination without delete sharing.
        // Bound retries to this diagnostic boundary; preserve the complete previous report.
        for (int attempt = 0; ; attempt++)
        {
            try { File.Move(path + ".tmp", path, overwrite: true); break; }
            catch (Exception error) when (attempt < 19 && (error is IOException or UnauthorizedAccessException))
            { Thread.Sleep(10); }
        }
    }
    private void Save()
    {
        var report = new { schema_version = 1, diagnosticOnly = true, Request.automationId, Request.iteration,
            status = _status, failure = _failure, hostProcessId = Environment.ProcessId, processCreationFileTime = _creation,
            sessionDirectoryName = Path.GetFileName(_session), runDirectoryName = Path.GetFileName(_directory),
            closeRequested = CloseRequested, observerAttached = ObserverAttached, build = _build, snapshots = _snapshots,
            fullM1Acceptance = false, limitation = "Startup inventory only; no media, performance or root-cause acceptance." };
        WriteAtomic(Path.Combine(_directory, "result.json"), report);
        WriteAtomic(Path.Combine(_tests, Request.automationId + ".startup-status.json"), report);
    }
    public void CheckCancellation()
    {
        if (File.Exists(Path.Combine(_tests, Request.automationId + ".cancel")))
            throw new OperationCanceledException("Startup observer stopped.");
    }
    public void Capture(string label, object context)
    {
        if (_snapshots.Count >= Labels.Length || label != Labels[_snapshots.Count])
            throw new InvalidOperationException("Unexpected startup checkpoint.");
        if (label != "after-shutdown") CheckCancellation();
        var snapshot = StartupInventory.Capture(label, context);
        WriteAtomic(Path.Combine(_directory, label + ".json"), snapshot);
        _snapshots.Add(snapshot);
        Save();
    }
    public async Task WaitForObserverAsync(CancellationToken cancellation, double timeoutSeconds = 30)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed.TotalSeconds < timeoutSeconds)
        {
            CheckCancellation();
            cancellation.ThrowIfCancellationRequested();
            string path = Path.Combine(_tests, Request.automationId + ".startup-observing.json");
            if (File.Exists(path))
            {
                using var ack = JsonDocument.Parse(File.ReadAllText(path));
                var data = ack.RootElement;
                if (data.GetProperty("automationId").GetString() != Request.automationId ||
                    data.GetProperty("hostProcessId").GetInt32() != Environment.ProcessId ||
                    data.GetProperty("processCreationFileTime").GetInt64() != _creation)
                    throw new InvalidDataException("Startup observer identity differs.");
                ObserverAttached = true;
                Save();
                return;
            }
            await Task.Delay(100, cancellation);
        }
        throw new TimeoutException("Startup observer did not attach.");
    }
    public void RequestClose()
    {
        CheckCancellation();
        if (_snapshots.Count != 3 || !ObserverAttached) throw new InvalidOperationException("Startup checkpoints are incomplete.");
        CloseRequested = true; _status = "closing"; Save();
    }
    public void Fail(Exception error) { _failure ??= error.ToString(); _status = "failed"; Save(); }
    public void Finish(Exception? error, object context)
    {
        _failure ??= error?.ToString();
        if (!CloseRequested) _failure ??= "Window closed before the automatic close checkpoint.";
        if (_snapshots.Count == 3)
        {
            try { Capture("after-shutdown", context); }
            catch (Exception captureError) { _failure ??= captureError.ToString(); }
        }
        if (_snapshots.Count != 4) _failure ??= "Startup checkpoints are incomplete.";
        _status = _failure is null ? "shutdown-completed" : "failed";
        Save();
    }
}

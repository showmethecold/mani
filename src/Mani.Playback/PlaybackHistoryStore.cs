// IINA 1.5.1/build 180 HistoryController / PlaybackHistory: one shared writer,
// remove an existing identity then insert on playback start, atomic persistence.
// Copyright (c) 2016-2026 lhc and contributors. GPL-3.0; licenses/IINA-GPL-3.0.txt.
// JSON/backup recovery is the .NET adaptation; mpv owns actual resume files.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Mani.Playback;

public sealed record PlaybackHistoryEntry(string Identity, string Source, string Title, double Position, double Duration, DateTimeOffset PlayedAt);
public sealed class PlaybackHistoryStore(string path)
{
    private sealed record Document(int SchemaVersion, PlaybackHistoryEntry[] Entries);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<PlaybackHistoryEntry>? _entries;
    private DateTimeOffset _lastWrite;
    private bool _dirty;
    public bool Recovered { get; private set; }
    public static string Identity(string source)
    {
        // Keep the complete URL, including query and case: distinct signed resources
        // must not collide. Windows file names are compared without case.
        string key = Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            ? source : Path.GetFullPath(source).ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
    }
    private async Task LoadUnderLockAsync()
    {
        if (_entries is not null) return;
        async Task<List<PlaybackHistoryEntry>> Read(string file)
        {
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(file));
            if (json.RootElement.ValueKind != JsonValueKind.Object ||
                !json.RootElement.TryGetProperty("SchemaVersion", out var version) ||
                version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int schema) || schema < 1)
                throw new JsonException("Invalid history version.");
            // An unsupported newer file is intact user data, not corruption.
            if (schema > 1) throw new InvalidDataException("Unsupported history version.");
            var doc = json.RootElement.Deserialize<Document>() ?? throw new JsonException("Empty history.");
            try
            {
                if (doc.Entries is null || doc.Entries.Any(e => e is null || string.IsNullOrWhiteSpace(e.Source) || e.Title is null || e.Identity != Identity(e.Source) ||
                    !double.IsFinite(e.Position) || !double.IsFinite(e.Duration) || e.Position < 0 || e.Duration < 0)) throw new JsonException("Invalid history entry.");
            }
            catch (ArgumentException error) { throw new JsonException("Invalid history path.", error); }
            return doc.Entries.DistinctBy(e => e.Identity).ToList();
        }
        if (!File.Exists(path))
        {
            if (!File.Exists(path + ".backup")) { _entries = []; return; }
            try { _entries = await Read(path + ".backup"); }
            catch (JsonException)
            {
                File.Copy(path + ".backup", path + ".corrupt-backup-" + Guid.NewGuid().ToString("N"));
                _entries = [];
            }
            Recovered = true; return;
        }
        try { _entries = await Read(path); }
        catch (JsonException)
        {
            // Preserve failed evidence; never silently overwrite unreadable data.
            File.Copy(path, path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"));
            try { _entries = File.Exists(path + ".backup") ? await Read(path + ".backup") : []; }
            catch (JsonException) { _entries = []; }
            Recovered = true;
        }
    }
    public async Task<IReadOnlyList<PlaybackHistoryEntry>> ReadAsync()
    {
        await _gate.WaitAsync();
        try { await LoadUnderLockAsync(); return _entries!.ToArray(); }
        finally { _gate.Release(); }
    }
    public async Task RecordAsync(string source, string title, double position, double duration, bool started, bool force = false)
    {
        if (string.IsNullOrWhiteSpace(source) || !double.IsFinite(position) || !double.IsFinite(duration)) return;
        await _gate.WaitAsync();
        try
        {
            await LoadUnderLockAsync(); string id = Identity(source);
            var previous = _entries!.FirstOrDefault(e => e.Identity == id);
            if (previous is null && !started) return; // Clearing does not resurrect active media.
            var entry = new PlaybackHistoryEntry(id, source, title, Math.Max(0, position), Math.Max(0, duration), started || previous is null ? DateTimeOffset.UtcNow : previous.PlayedAt);
            if (previous != entry)
            {
                _entries!.RemoveAll(e => e.Identity == id);
                _entries.Add(entry); _entries.Sort((a, b) => b.PlayedAt.CompareTo(a.PlayedAt)); _dirty = true;
            }
            // A paused snapshot can be unchanged while an earlier throttled
            // update still needs persistence, including a new playback start.
            if (_dirty && (force || DateTimeOffset.UtcNow - _lastWrite >= TimeSpan.FromSeconds(5))) await SaveUnderLockAsync();
        }
        finally { _gate.Release(); }
    }
    public async Task RemoveAsync(string? identity = null)
    {
        await _gate.WaitAsync();
        try
        {
            await LoadUnderLockAsync(); _entries!.RemoveAll(e => identity is null || e.Identity == identity);
            _dirty = true; await SaveUnderLockAsync();
            // A later damaged primary must not resurrect explicitly removed history.
            File.Copy(path, path + ".backup", true);
            if (identity is null)
                foreach (string failed in Directory.EnumerateFiles(Path.GetDirectoryName(Path.GetFullPath(path))!, Path.GetFileName(path) + ".corrupt-*")) File.Delete(failed);
        }
        finally { _gate.Release(); }
    }
    public async Task FlushAsync()
    {
        await _gate.WaitAsync();
        try { if (_dirty) await SaveUnderLockAsync(); }
        finally { _gate.Release(); }
    }
    // Keep the five-second recovery contract. Serialize/copy/flush on a worker;
    // the caller retains _gate until completion, including shutdown flushes.
    private Task SaveUnderLockAsync() => Task.Run(SaveSnapshotUnderLockAsync);
    private async Task SaveSnapshotUnderLockAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, new Document(1, _entries!.ToArray()));
                await stream.FlushAsync(); stream.Flush(true);
            }
            if (File.Exists(path) && !Recovered) File.Copy(path, path + ".backup", true);
            File.Move(temp, path, true); Recovered = false; _dirty = false; _lastWrite = DateTimeOffset.UtcNow;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

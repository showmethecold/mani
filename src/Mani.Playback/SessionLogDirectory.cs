namespace Mani.Playback;

// Windows retention for newly owned, ordinary sessions only. Legacy logs and
// diagnostic evidence are deliberately outside this cleanup policy.
public sealed class SessionLogDirectory : IDisposable
{
    private const string Marker = ".mani-session-v1";
    private static readonly HashSet<string> OwnedFiles = new(StringComparer.OrdinalIgnoreCase)
    { Marker, ".active", "host-state.jsonl", "mpv.log", "mpv.log.session.jsonl", "runtime.json", "host-errors.txt", "file-picker.txt" };
    private readonly FileStream _lease;
    public string Path { get; }

    public SessionLogDirectory(string root)
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetFullPath(root), DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path);
        _lease = new FileStream(System.IO.Path.Combine(Path, ".active"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        File.WriteAllText(System.IO.Path.Combine(Path, Marker), "Mani session logs v1");
    }

    public void PreserveEvidence() => File.WriteAllText(System.IO.Path.Combine(Path, ".preserve-evidence"), "diagnostic session");

    public static void Prune(string root, int keepSessions = 20, long budgetBytes = 100L * 1024 * 1024)
    {
        string boundary = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(root)) + System.IO.Path.DirectorySeparatorChar;
        if (!Directory.Exists(root)) return;
        int kept = 0; long bytes = 0;
        foreach (var directory in new DirectoryInfo(root).EnumerateDirectories().OrderByDescending(d => d.CreationTimeUtc))
        {
            try
            {
                if (!directory.FullName.StartsWith(boundary, StringComparison.OrdinalIgnoreCase) ||
                    (directory.Attributes & FileAttributes.ReparsePoint) != 0 || directory.EnumerateDirectories().Any()) continue;
                var files = directory.GetFiles();
                if (!files.Any(f => f.Name == Marker) || files.Any(f => !OwnedFiles.Contains(f.Name) || (f.Attributes & FileAttributes.ReparsePoint) != 0)) continue;
                if (File.ReadAllText(System.IO.Path.Combine(directory.FullName, Marker)) != "Mani session logs v1") continue;
                // An active window holds an exclusive lease, including in another process.
                using (new FileStream(System.IO.Path.Combine(directory.FullName, ".active"), FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                long size = files.Sum(f => f.Length);
                if (kept < keepSessions && bytes + size <= budgetBytes) { kept++; bytes += size; continue; }
                foreach (var file in files) file.Delete();
                directory.Delete(); // Never recursive: unexpected new files make removal fail safely.
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
    public void Dispose() => _lease.Dispose();
}

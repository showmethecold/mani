// Windows-only deployment boundary. IINA has no MSIX user-data container.
// Keep the original bytes (including future schemas and recovery evidence),
// and never merge old data into an existing desktop profile.
using System.Text.Json.Nodes;

namespace Mani_App;

internal static class LegacyUserDataMigration
{
    internal static void ImportIfMissing(string source, string destination)
    {
        if (!Directory.Exists(source) || IsLink(source) || IsLink(destination)) return;
        var pending = Path.Combine(destination, ".msix-importing");
        if (File.Exists(Path.Combine(destination, ".msix-imported"))) return;
        if (!File.Exists(pending) && (File.Exists(Path.Combine(destination, "settings.json")) ||
            File.Exists(Path.Combine(destination, "settings.json.backup")) ||
            File.Exists(Path.Combine(destination, "history.json")))) return;
        if (File.Exists(pending) && File.ReadAllText(pending) != Path.GetFullPath(source)) return;
        var files = Directory.GetFiles(source).Where(path =>
            Path.GetFileName(path).StartsWith("settings.json", StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(path).StartsWith("history.json", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (files.Length == 0) return;
        Directory.CreateDirectory(destination);
        WriteNew(pending, System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(source)));
        foreach (var file in files) Copy(file, Path.Combine(destination, Path.GetFileName(file)), adjustAutoHide: true);
        var watchLater = Path.Combine(source, "watch-later");
        if (Directory.Exists(watchLater) && !IsLink(watchLater))
        {
            var target = Path.Combine(destination, "watch-later");
            if (!IsLink(target))
            {
                Directory.CreateDirectory(target);
                foreach (var file in Directory.GetFiles(watchLater)) Copy(file, Path.Combine(target, Path.GetFileName(file)), adjustAutoHide: false);
            }
        }
        // A failed/interrupted import resumes without overwriting any copied or
        // subsequently edited file. The original container is never modified.
        File.WriteAllText(Path.Combine(destination, ".msix-imported"), "Imported from the previous MSIX data directory. Original files retained.\n");
        File.Delete(pending);
    }

    private static bool IsLink(string path) => (File.Exists(path) || Directory.Exists(path)) &&
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static void Copy(string source, string destination, bool adjustAutoHide)
    {
        if (IsLink(source) || File.Exists(destination)) return;
        var bytes = File.ReadAllBytes(source);
        if (adjustAutoHide && Path.GetFileName(source).StartsWith("settings.json", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                // Older PowerShell-created backups may contain a UTF-8 BOM.
                var text = System.Text.Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
                var json = JsonNode.Parse(text);
                if (json?["schemaVersion"]?.GetValue<int>() == 1 && json["controlBarAutoHideTimeout"]?.GetValue<double>() == 0)
                {
                    // Preserve the unmodified recovery evidence beside the import.
                    WriteNew(destination + ".before-msix-import", bytes);
                    json["controlBarAutoHideTimeout"] = 2.5;
                    bytes = System.Text.Encoding.UTF8.GetBytes(json.ToJsonString());
                }
            }
            catch (Exception error) when (error is System.Text.Json.JsonException or InvalidOperationException or FormatException) { }
        }
        WriteNew(destination, bytes);
    }

    private static void WriteNew(string destination, byte[] bytes)
    {
        var temporary = destination + ".import-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(temporary, bytes);
            try { File.Move(temporary, destination, overwrite: false); }
            catch (IOException) when (File.Exists(destination)) { }
        }
        finally { File.Delete(temporary); }
    }
}

using Mani_App;
using System.Text.Json;

namespace Mani.UnitTests;

public sealed class LegacyUserDataMigrationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "Mani-Migration-" + Guid.NewGuid().ToString("N"));
    private string Old => Path.Combine(root, "old");
    private string New => Path.Combine(root, "new");
    public LegacyUserDataMigrationTests() => Directory.CreateDirectory(Old);
    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact]
    public void ImportsFutureAndDamagedFilesWithoutRewritingAndKeepsSource()
    {
        const string future = "{\"schemaVersion\":999,\"unknown\":true}";
        File.WriteAllText(Path.Combine(Old, "settings.json"), future);
        File.WriteAllText(Path.Combine(Old, "history.json"), "damaged evidence");
        Directory.CreateDirectory(Path.Combine(Old, "watch-later"));
        File.WriteAllText(Path.Combine(Old, "watch-later", "resume"), "start=14");
        Directory.CreateDirectory(Path.Combine(Old, "Logs"));
        File.WriteAllText(Path.Combine(Old, "Logs", "not-profile"), "private log");
        LegacyUserDataMigration.ImportIfMissing(Old, New);
        Assert.Equal(future, File.ReadAllText(Path.Combine(New, "settings.json")));
        Assert.Equal("damaged evidence", File.ReadAllText(Path.Combine(New, "history.json")));
        Assert.Equal("start=14", File.ReadAllText(Path.Combine(New, "watch-later", "resume")));
        Assert.False(Directory.Exists(Path.Combine(New, "Logs")));
        Assert.Equal(future, File.ReadAllText(Path.Combine(Old, "settings.json")));
    }

    [Theory]
    [InlineData("settings.json")]
    [InlineData("settings.json.backup")]
    [InlineData("history.json")]
    public void ExistingDesktopProfileIsNeverMergedWithOldData(string existing)
    {
        Directory.CreateDirectory(New);
        File.WriteAllText(Path.Combine(New, existing), "current");
        File.WriteAllText(Path.Combine(Old, "settings.json"), "old");
        File.WriteAllText(Path.Combine(Old, "history.json"), "old history");
        LegacyUserDataMigration.ImportIfMissing(Old, New);
        Assert.Equal("current", File.ReadAllText(Path.Combine(New, existing)));
        Assert.Single(Directory.GetFiles(New));
    }

    [Theory]
    [InlineData(0, 2.5, false)]
    [InlineData(0, 2.5, true)]
    [InlineData(4.5, 4.5, false)]
    [InlineData(4.5, 4.5, true)]
    public void ZeroAutoHideIsAdjustedButPositivePreferenceIsPreserved(double oldValue, double expected, bool bom)
    {
        var original = JsonSerializer.Serialize(new { schemaVersion=1, controlBarAutoHideTimeout=oldValue });
        File.WriteAllText(Path.Combine(Old, "settings.json"), original, new System.Text.UTF8Encoding(bom));
        LegacyUserDataMigration.ImportIfMissing(Old, New);
        using var parsed = JsonDocument.Parse(File.ReadAllText(Path.Combine(New, "settings.json")));
        Assert.Equal(expected, parsed.RootElement.GetProperty("controlBarAutoHideTimeout").GetDouble());
        Assert.Equal(original, File.ReadAllText(Path.Combine(Old, "settings.json")));
        Assert.Equal(oldValue == 0, File.Exists(Path.Combine(New, "settings.json.before-msix-import")));
    }

    [Fact]
    public void InterruptedImportResumesWithoutOverwritingEditedFiles()
    {
        Directory.CreateDirectory(New);
        File.WriteAllText(Path.Combine(New, ".msix-importing"), Path.GetFullPath(Old));
        File.WriteAllText(Path.Combine(New, "settings.json"), "edited since failure");
        File.WriteAllText(Path.Combine(Old, "settings.json"), "original");
        File.WriteAllText(Path.Combine(Old, "history.json"), "history");
        LegacyUserDataMigration.ImportIfMissing(Old, New);
        Assert.Equal("edited since failure", File.ReadAllText(Path.Combine(New, "settings.json")));
        Assert.Equal("history", File.ReadAllText(Path.Combine(New, "history.json")));
        Assert.False(File.Exists(Path.Combine(New, ".msix-importing")));
        File.Delete(Path.Combine(New, "history.json"));
        LegacyUserDataMigration.ImportIfMissing(Old, New);
        Assert.False(File.Exists(Path.Combine(New, "history.json"))); // deliberate later deletion stays deleted
    }
}

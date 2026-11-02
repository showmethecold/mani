using System.Globalization;
using System.Text.Json;
using Mani.Playback;

namespace Mani.UnitTests;

// Self-contained file-component checks migrated from UiRefreshProbes.
public sealed class SessionTraceLogTests
{
    [Fact]
    public void AppendsFlushCompleteUtf8RecordsWhileTheWriterIsOpen()
    {
        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "trace.jsonl");
        using var log = new SessionTraceLog(path);
        log.Append("state", new { value = "line1\nline2", name = "Title \u4e2d\u6587" });
        string contents = ReadShared(path);
        Assert.EndsWith("\n", contents);
        Assert.Single(contents.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        using var row = JsonDocument.Parse(contents);
        Assert.Equal("state", row.RootElement.GetProperty("action").GetString());
        Assert.Equal("line1\nline2", row.RootElement.GetProperty("details").GetProperty("value").GetString());
        Assert.Equal("Title \u4e2d\u6587", row.RootElement.GetProperty("details").GetProperty("name").GetString());
        Assert.True(DateTimeOffset.TryParse(row.RootElement.GetProperty("at").GetString(),
            CultureInfo.InvariantCulture, DateTimeStyles.None, out _));
    }

    [Fact]
    public void ConcurrentWritersPreserveEveryCompleteRecord()
    {
        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "trace.jsonl");
        using var log = new SessionTraceLog(path);
        Parallel.For(0, 64, i => log.Append("parallel", new { i }));
        string[] rows = ReadShared(path).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(64, rows.Length);
        var values = new List<int>();
        foreach (string line in rows)
        {
            using var row = JsonDocument.Parse(line);
            Assert.Equal("parallel", row.RootElement.GetProperty("action").GetString());
            values.Add(row.RootElement.GetProperty("details").GetProperty("i").GetInt32());
        }
        Assert.Equal(Enumerable.Range(0, 64), values.Order());
    }

    [Fact]
    public void DisposedLogsCannotReopenOrAppend()
    {
        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "trace.jsonl");
        var log = new SessionTraceLog(path);
        log.Append("shutdown-finished", new { });
        log.Dispose();
        byte[] completed = File.ReadAllBytes(path);
        log.Dispose();
        log.Append("ignored-after-close", new { });
        Assert.Equal(completed, File.ReadAllBytes(path));
    }

    [Fact]
    public void ReopeningAppendsWithoutTruncationOrABom()
    {
        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "trace.jsonl");
        using (var first = new SessionTraceLog(path)) first.Append("first", new { });
        using (var second = new SessionTraceLog(path)) second.Append("second", new { });
        Assert.False(File.ReadAllBytes(path).AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }));
        string[] rows = File.ReadAllLines(path);
        Assert.Equal(2, rows.Length);
        using var firstRow = JsonDocument.Parse(rows[0]);
        using var secondRow = JsonDocument.Parse(rows[1]);
        Assert.Equal("first", firstRow.RootElement.GetProperty("action").GetString());
        Assert.Equal("second", secondRow.RootElement.GetProperty("action").GetString());
    }

    [Fact]
    public void AnUnavailableDirectoryCanRecoverOnTheNextAppend()
    {
        using var directory = new TemporaryDirectory();
        string parent = Path.Combine(directory.Path, "created-later");
        string path = Path.Combine(parent, "trace.jsonl");
        using var log = new SessionTraceLog(path);
        log.Append("unavailable", new { });
        Assert.False(File.Exists(path));
        Directory.CreateDirectory(parent);
        log.Append("recovered", new { });
        string row = Assert.Single(ReadShared(path).Split('\n', StringSplitOptions.RemoveEmptyEntries));
        using var data = JsonDocument.Parse(row);
        Assert.Equal("recovered", data.RootElement.GetProperty("action").GetString());
    }

    private static string ReadShared(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(file);
        return reader.ReadToEnd();
    }

}

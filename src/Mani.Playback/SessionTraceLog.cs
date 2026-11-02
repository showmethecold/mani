using System.Text;
using System.Text.Json;

namespace Mani.Playback;

// IINA 1.5.1 Logger.log retains a locked file handle and ignores writes after close.
// IINA: Copyright (c) 2018 lhc (Collider LI), GPL v3 (licenses/IINA-GPL-3.0.txt).
// .NET adaptation retains Mani's JSONL schema and immediate per-record visibility.
public sealed class SessionTraceLog(string path, long maxBytes = 8 * 1024 * 1024) : IDisposable
{
    private readonly object _gate = new();
    private StreamWriter? _writer;
    private bool _closed, _limited;
    private long _bytes;

    public void Append(string action, object details)
    {
        lock (_gate)
        {
            if (_closed || _limited) return;
            try
            {
                if (_writer is null)
                {
                    _bytes = File.Exists(path) ? new FileInfo(path).Length : 0;
                    _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write,
                        FileShare.ReadWrite), new UTF8Encoding(false));
                    _writer.NewLine = "\n";
                    _writer.AutoFlush = true;
                }
                string record = DiagnosticPrivacy.Redact(JsonSerializer.Serialize(new { at = DateTimeOffset.Now, action, details }));
                long length = Encoding.UTF8.GetByteCount(record) + 1;
                if (_bytes + length > maxBytes)
                {
                    _writer.WriteLine("{\"action\":\"log-budget-reached\"}");
                    _limited = true; CloseWriter(); return;
                }
                _writer.WriteLine(record); _bytes += length;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                CloseWriter(); // Preserve best-effort logging; retry opening on the next record.
            }
        }
    }

    private void CloseWriter()
    {
        var writer = _writer;
        _writer = null;
        try { writer?.Dispose(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _closed = true;
            CloseWriter();
        }
    }
}

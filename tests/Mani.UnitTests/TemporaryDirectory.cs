namespace Mani.UnitTests;

internal sealed class TemporaryDirectory : IDisposable
{
    private readonly string _parent = System.IO.Path.GetFullPath(
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Mani.UnitTests"));
    public string Path { get; }

    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(_parent, Guid.NewGuid().ToString("N"));
        if (Directory.Exists(Path)) throw new IOException("Test directory already exists.");
        Directory.CreateDirectory(Path);
    }

    public void Dispose()
    {
        string target = System.IO.Path.GetFullPath(Path);
        if (!target.StartsWith(_parent + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Test cleanup escaped its temporary parent.");
        Directory.Delete(target, recursive: true);
    }
}

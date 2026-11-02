using Mani.Playback;
namespace Mani.UnitTests;

public sealed class M4SourcesTests
{
    [Fact]
    public void FoldersFilterRecurseDeduplicateAndNaturallySort()
    {
        string root = Path.Combine(Path.GetTempPath(), "mani-sources-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Path.Combine(root, "nested"));
        try
        {
            foreach (var name in new[] { "中文 10.mkv", "中文 2.mkv", "ignored.srt", "notes.txt", "nested/audio.flac" }) File.WriteAllText(Path.Combine(root, name), "");
            var files = MediaSources.Expand([root, Path.Combine(root, "中文 2.mkv")]);
            Assert.Equal(3, files.Count); Assert.True(files.ToList().IndexOf(Path.Combine(root, "中文 2.mkv")) < files.ToList().IndexOf(Path.Combine(root, "中文 10.mkv")));
            Assert.Contains(Path.GetFullPath(Path.Combine(root, "nested/audio.flac")), files);
            Assert.Equal(new[] { "https://test/a?token=1", "https://test/a?token=2" }, MediaSources.Expand(["https://test/a?token=1", "https://test/a?token=2"]));
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public void SinglePlaylistIsPassedToEngineWhileMultiplePlaylistFilesAreFiltered()
    {
        Assert.Single(MediaSources.Expand([@"C:\test\list.m3u8"]));
        Assert.Single(MediaSources.Expand([@"C:\test\list.m3u8", @"C:\test\film.mkv"]));
    }
}

using Mani_App;

namespace Mani.UnitTests;

public sealed class LocalMediaFileTests
{
    [Theory]
    [InlineData(@"C:\Videos\中文 日本語 [1], 100% #.mkv", @"C:\Videos\中文 日本語 [1], 100% #.mkv")]
    [InlineData(@"C:\Videos\literal%20name.mkv", @"C:\Videos\literal%20name.mkv")]
    [InlineData("C:/Videos/clip.mkv", @"C:\Videos\clip.mkv")]
    [InlineData("file:///C:/Videos/a%20b%23%25.mkv", @"C:\Videos\a b#%.mkv")]
    [InlineData(@"\\server\share\a b.mkv", @"\\server\share\a b.mkv")]
    [InlineData("file://server/share/a%20b.mkv", @"\\server\share\a b.mkv")]
    public void NativePathsAndFileUrisKeepTheirActualNames(string source, string expected) =>
        Assert.Equal(expected, LocalMediaFile.PathFrom(source));

    [Theory]
    [InlineData("")] [InlineData(" ")] [InlineData("relative.mkv")] [InlineData(@"C:relative.mkv")]
    [InlineData("https://example.test/video.mkv")] [InlineData("http://localhost/video.mkv")]
    [InlineData("ftp://example.test/video.mkv")] [InlineData("edl://test")]
    [InlineData("memory://test")] [InlineData("shell:AppsFolder")] [InlineData("C:\\bad\0file.mkv")]
    public void NonFileAndAmbiguousSourcesAreNotShellTargets(string source) => Assert.Null(LocalMediaFile.PathFrom(source));
}

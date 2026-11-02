using Mani_App;

namespace Mani.UnitTests;

public sealed class FileActivationTests
{
    [Fact]
    public void RedirectedLaunchUsesWindowsQuotingAndExcludesOnlyItsOwnExecutable()
    {
        string exe = @"C:\Program Files\Mani\Mani.App.exe";
        string first = @"C:\视频 样片\第 1 集 %20 # &.mp4";
        string second = @"C:\视频\--pause=yes.mkv";
        Assert.Equal([first, second], FileActivationRequest.FromCommandLine($"\"{exe}\" \"{first}\" \"{second}\"", exe));
        Assert.Equal([first], FileActivationRequest.FromCommandLine($"\"{first}\"", exe));
        Assert.Empty(FileActivationRequest.FromCommandLine($"\"{exe}\" --m2-verify=C:\\test", exe));
    }
    [Fact]
    public void KeepsWindowsFilenameCharactersAndGroupsMultipleFiles()
    {
        string first = @"C:\视频 样片\第 1 集 %20 # &.mp4";
        string second = @"C:\视频 样片\--pause=yes.mkv";
        Assert.Equal([first, second], FileActivationRequest.FromPaths([first, second, first.ToUpperInvariant()]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("--m2-verify=C:\\test")]
    [InlineData("--pause=yes")]
    [InlineData("https://example.com/movie.mp4")]
    [InlineData("C:\\bad\0name.mp4")]
    public void DoesNotInterpretSwitchesUrlsOrMalformedPathsAsFiles(string input) =>
        Assert.Empty(FileActivationRequest.FromPaths([input]));

    [Fact]
    public void NormalizesFileUrisAndDoesNotDiscardMissingFilesBeforePlaybackCanReportFailure()
    {
        Assert.Equal([@"C:\视频\missing file.mp4", @"\\server\share\movie.mkv"],
            FileActivationRequest.FromPaths(["file:///C:/视频/missing%20file.mp4", @"\\server\share\movie.mkv"]));
    }
}

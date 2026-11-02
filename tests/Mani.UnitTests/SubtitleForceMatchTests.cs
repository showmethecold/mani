using Mani.Playback;

namespace Mani.UnitTests;

public sealed class SubtitleForceMatchTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    [Theory]
    [InlineData("iina")]
    [InlineData("name")]
    public void UnmatchedFileUsesFixedIinaFallbackInBothEnabledModes(string mode)
        => Assert.Equal(["The Film (sub).srt"], SubtitleMatcher.Match("The Film [1080p].mkv", [],
            ["The Film (sub).srt", "Unrelated.srt"], new() { SubtitleMatching = mode }, Token));

    [Fact]
    public void DisabledMatchingDoesNotForceAnyCandidate()
        => Assert.Empty(SubtitleMatcher.Match("Film.A.mkv", [], ["Film.B.srt"], new() { SubtitleMatching = "no" }, Token));

    [Fact]
    public void StageOneMatchesTakePrecedenceAndAreNotStolen()
    {
        string[] videos = ["Film.A.mkv", "Film.AA.mkv"];
        Assert.Equal(["Film.A.en.srt"], SubtitleMatcher.Match(videos[0], videos,
            ["Film.A.en.srt", "Film.B.srt"], new(), Token));
        Assert.Equal(["Film.B.srt"], SubtitleMatcher.Match(videos[1], videos,
            ["Film.A.en.srt", "Film.B.srt"], new(), Token));
    }

    [Fact]
    public void CandidateMustChooseTheSameMediaAsItsNearestNeighbor()
    {
        string[] videos = ["Film.A.mkv", "Film.BB.mkv"];
        Assert.Empty(SubtitleMatcher.Match(videos[1], videos, ["Film.C.srt"], new(), Token));
        Assert.Equal(["Film.C.srt"], SubtitleMatcher.Match(videos[0], videos, ["Film.C.srt"], new(), Token));
    }

    [Fact]
    public void MutualDistanceTiesRetainEveryCandidateAndAreNotGreedy()
    {
        string[] videos = ["Film.A.mkv", "Film.B.mkv"];
        string[] subs = ["Film.C.srt", "Film.D.srt"];
        foreach (string video in videos) Assert.Equal(subs, SubtitleMatcher.Match(video, videos, subs, new(), Token));
    }

    [Theory]
    [InlineData("ab", "aX", false)] // distance 2 equals floor(4 * .6): reject.
    [InlineData("abcd", "abcX", true)]
    [InlineData("abcdef", "uvwxyz", false)]
    public void ThresholdIsStrictAndUsesInsertionDeletionOneSubstitutionFour(string video, string sub, bool matches)
        => Assert.Equal(matches, SubtitleMatcher.Match(video + ".mkv", [], [sub + ".srt"], new(), Token).Count > 0);

    [Fact]
    public void ThresholdUsesSwiftCharactersRatherThanUtf16Length()
        => Assert.Empty(SubtitleMatcher.Match("📺a.mkv", [], ["📺X.srt"], new(), Token));

    [Theory]
    [InlineData(99, true)]
    [InlineData(100, false)]
    public void PairLimitIsStrictAndCountsOnlyUnmatchedFiles(int subtitleCount, bool matches)
    {
        string[] videos = Enumerable.Range(0,100).Select(i => Path.Combine($"video{i}", "Film A.mkv")).ToArray();
        // Shared base names in different directories retain equal minimum distances.
        string[] subs = Enumerable.Range(0,subtitleCount).Select(i => Path.Combine($"subs{i}", "Film B.srt")).ToArray();
        Assert.Equal(matches, SubtitleMatcher.Match(videos[0], videos, subs, new() { SubtitleMatching = "name" }, Token).Count > 0);
    }

    [Fact]
    public void ForceStageDoesNotReorderTiesByStageOnePriority()
        => Assert.Equal(["Film.A.srt", "Film.Z.srt"], SubtitleMatcher.Match("Film.X.mkv", [],
            ["Film.Z.srt", "Film.A.srt"], new() { SubtitlePriority = "Z" }, Token));

    [Fact]
    public void CancelledRequestDoesNotReturnFallbackResults()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => SubtitleMatcher.Match("Film.A.mkv", [],
            ["Film.B.srt"], new(), cancellation.Token));
    }

    [Fact]
    public async Task DirectoryFallbackLoadsSubfolderCandidateWithoutChangingInputFiles()
    {
        using var directory = new TemporaryDirectory();
        string media = Path.Combine(directory.Path, "Film 1080p.mkv");
        Directory.CreateDirectory(Path.Combine(directory.Path, "Subs"));
        string sub = Path.Combine(directory.Path, "Subs", "Film 720p.srt");
        await File.WriteAllTextAsync(media, "media", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(sub, "subtitle", TestContext.Current.CancellationToken);
        Assert.Equal([sub], await SubtitleMatcher.FindAsync(media, new(), TestContext.Current.CancellationToken));
        Assert.Equal("media", await File.ReadAllTextAsync(media, TestContext.Current.CancellationToken));
    }
}

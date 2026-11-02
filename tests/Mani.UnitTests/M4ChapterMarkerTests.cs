using System.Text.Json;
using Mani.Playback;
using Mani_App;

namespace Mani.UnitTests;

public sealed class M4ChapterMarkerTests
{
    private static MediaChapter[] Chapters(params double[] starts) => starts.Select((start, index) => new MediaChapter(index, "章 " + index, start)).ToArray();

    [Fact]
    public void ChapterBoundariesSkipFirstEvenWhenItStartsAfterZero()
    {
        Assert.Equal(new[] { .25, .75 }, ChapterMarkerLayout.Fractions(100, Chapters(5, 25, 75)));
        Assert.Empty(ChapterMarkerLayout.Fractions(100, Chapters(25)));
    }

    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)]
    public void UnknownOrInvalidDurationCannotCreateGeometry(double duration)
        => Assert.Empty(ChapterMarkerLayout.Fractions(duration, Chapters(0, 20, 40)));

    [Fact]
    public void BadNativeMetadataCannotEscapeTheTrackOrDuplicateSeparators()
        => Assert.Equal(new[] { .5 }, ChapterMarkerLayout.Fractions(100,
            Chapters(0, double.NaN, double.NegativeInfinity, -1, 0, 50, 50, 100, 101)));

    [Theory]
    [InlineData(0, 320, 12, 6)]
    [InlineData(.5, 320, 12, 160)]
    [InlineData(1, 320, 12, 314)]
    [InlineData(.25, 80, 12, 23)]
    public void MarkerAndHoverUseNativeThumbTravel(double fraction, double width, double thumb, double center)
    {
        Assert.Equal(center, ChapterMarkerLayout.Center(fraction, width, thumb));
        Assert.Equal(fraction, ChapterMarkerLayout.Fraction(center, width, thumb), 10);
    }

    [Fact]
    public void TinyTracksAndEndpointHoversStayFiniteAndBounded()
    {
        Assert.Equal(4, ChapterMarkerLayout.Center(.5, 8, 12));
        Assert.Equal(0, ChapterMarkerLayout.Fraction(4, 8, 12));
        Assert.Equal(0, ChapterMarkerLayout.Fraction(-20, 100, 12));
        Assert.Equal(1, ChapterMarkerLayout.Fraction(200, 100, 12));
    }

    [Fact]
    public async Task MissingKeyUsesIinaDefaultAndSavePreservesOtherPreferences()
    {
        string folder = Path.Combine(Path.GetTempPath(), "mani-chapters-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string path = Path.Combine(folder, "settings.json");
            var token = TestContext.Current.CancellationToken;
            await File.WriteAllTextAsync(path, "{\"volumePercent\":37,\"language\":\"zh-CN\",\"controlBarAutoHideTimeout\":4,\"audioLanguage\":\"ja\"}", token);
            var store = new PlayerSettingsStore(path);
            var original = await store.LoadAsync(token); Assert.False(original.ShowChapterPositions);
            await store.SaveAsync(original with { ShowChapterPositions = true }, token);
            Assert.Equal(original with { ShowChapterPositions = true }, await store.LoadAsync(token));
            await store.SaveAsync(original, token);
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(path, token));
            Assert.False(json.RootElement.GetProperty("showChapterPos").GetBoolean());
            Assert.Single(json.RootElement.EnumerateObject(), p => p.Name == "showChapterPos");
            Assert.Equal("ja", json.RootElement.GetProperty("audioLanguage").GetString());
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void ConcurrentWindowEditsRetainChapterPreferenceAndLocalVolume()
    {
        var before = new PlayerSettings();
        var shared = before with { ShowChapterPositions = true };
        var local = before with { Language = "zh-CN", VolumePercent = 19 };
        var merged = SharedPlayerSettings.ForExistingWindow(shared, before, local);
        Assert.True(merged.ShowChapterPositions); Assert.Equal(19, merged.VolumePercent); Assert.Equal("zh-CN", merged.Language);
        Assert.False(SharedPlayerSettings.Merge(merged, merged, merged with { ShowChapterPositions = false }).ShowChapterPositions);
    }
}

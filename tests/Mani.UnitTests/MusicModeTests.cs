using Mani_App;
using Mani.Playback;

namespace Mani.UnitTests;

public sealed class MusicModeTests
{
    private static MusicMediaInfo Info(string tracks, string path = "C:\\music\\test.mp3", Dictionary<string, string>? extra = null, string? chapter = null)
    {
        var properties = extra ?? new();
        properties["track-list"] = tracks;
        return MusicMediaInfo.Read(path, "File title", properties, chapter);
    }

    [Theory]
    [InlineData("[]", 0)]
    [InlineData("", 0)]
    [InlineData("not json", 0)]
    [InlineData("{}", 0)]
    [InlineData("[{\"type\":\"sub\"}]", 0)]
    [InlineData("[{\"type\":\"audio\"}]", 1)]
    [InlineData("[{\"type\":\"video\",\"albumart\":true}]", 1)]
    [InlineData("[{\"type\":\"audio\"},{\"type\":\"video\",\"albumart\":true}]", 1)]
    [InlineData("[{\"type\":\"video\"}]", 2)]
    [InlineData("[{\"type\":\"audio\"},{\"type\":\"video\",\"selected\":false}]", 2)]
    [InlineData("[{\"type\":\"video\",\"albumart\":true},{\"type\":\"video\",\"albumart\":false}]", 2)]
    public void ClassificationUsesAllTracksAndRetainsUnknown(string tracks, int expected)
        => Assert.Equal((MusicMediaKind)expected, Info(tracks).Kind);

    [Theory]
    [InlineData("https://example.invalid/audio.mp3")]
    [InlineData("http://localhost/audio.wav")]
    [InlineData("rtsp://example.invalid/music")]
    public void NetworkResourcesNeverAutomaticallyBecomeMusic(string path)
        => Assert.Equal(MusicMediaKind.Video, Info("[{\"type\":\"audio\"}]", path).Kind);

    [Fact]
    public void AudioUsesSquareFallbackAndFirstEmbeddedArtworkDimensions()
    {
        var plain = Info("[{\"type\":\"audio\"}]");
        Assert.Equal(new VideoSize(1, 1), plain.DisplaySize); Assert.True(plain.DefaultArtwork);
        var cover = Info("[{\"type\":\"audio\"},{\"type\":\"video\",\"albumart\":true,\"demux-w\":600,\"demux-h\":400},{\"type\":\"video\",\"albumart\":true,\"demux-w\":300,\"demux-h\":900}]");
        Assert.Equal(new VideoSize(600, 400), cover.DisplaySize); Assert.False(cover.DefaultArtwork);
    }
    [Theory]
    [InlineData(0, 500)]
    [InlineData(-1, 400)]
    [InlineData(600, 0)]
    public void InvalidArtworkDimensionsUseSquareWithoutPaintingFallbackOverEmbeddedArt(int width, int height)
    {
        var info = Info($$"""[{"type":"video","albumart":true,"demux-w":{{width}},"demux-h":{{height}}}]""");
        Assert.Equal(new VideoSize(1, 1), info.DisplaySize); Assert.False(info.DefaultArtwork);
    }
    [Theory]
    [InlineData("yes", "1", "no", "no", false)]
    [InlineData("no", "1", "yes", "2", false)]
    [InlineData("yes", "no", "yes", "no", true)]
    [InlineData("no", "1", "no", "2", true)]
    public void VisibleSubtitlesTakePrecedenceOverDefaultArtwork(string primary, string sid, string secondary, string secondarySid, bool fallback)
        => Assert.Equal(fallback, Info("[{\"type\":\"audio\"}]", extra: new() { ["sub-visibility"] = primary, ["sid"] = sid, ["secondary-sub-visibility"] = secondary, ["secondary-sid"] = secondarySid }).DefaultArtwork);

    [Theory]
    [InlineData("Artist", "Album", "Artist - Album")]
    [InlineData("", "Album", "Album")]
    [InlineData("Artist", "", "Artist")]
    [InlineData("", "", "")]
    [InlineData("艺术家", "专辑", "艺术家 - 专辑")]
    public void MetadataCombinesArtistAndAlbumWithoutInventingLabels(string artist, string album, string expected)
        => Assert.Equal(expected, Info("[]", extra: new() { ["metadata/by-key/artist"] = artist, ["metadata/by-key/album"] = album }).ArtistAlbum);
    [Fact]
    public void ChapterTitleAndPerformerOverrideGeneralMetadata()
    {
        var info = Info("[]", extra: new() { ["chapters"] = "2", ["metadata/by-key/artist"] = "Artist", ["metadata/by-key/album"] = "Album", ["chapter-metadata/by-key/performer"] = "Performer" }, chapter: "Chapter title");
        Assert.Equal("Chapter title", info.Title); Assert.Equal("Performer - Album", info.ArtistAlbum);
    }
    [Fact]
    public void MissingChapterMetadataFallsBackToMediaTitleAndArtist()
    {
        var info = Info("[]", extra: new() { ["chapters"] = "2", ["metadata/by-key/artist"] = "Artist" });
        Assert.Equal("File title", info.Title); Assert.Equal("Artist", info.ArtistAlbum);
    }
    [Fact]
    public void VideoKeepsDisplayAspectAndRotationInManualMusicMode()
    {
        var info = Info("[{\"type\":\"video\"}]", extra: new() { ["dwidth"] = "1920", ["dheight"] = "1080", ["video-params/rotate"] = "90", ["video-rotate"] = "0", ["vid"] = "1" });
        Assert.Equal(new VideoSize(1080, 1920), info.DisplaySize); Assert.False(info.DefaultArtwork);
    }
    [Fact]
    public void AutomaticSwitchHonorsFullscreenUnknownAndDisabledPreference()
    {
        var state = new MusicModeState();
        Assert.Null(state.TrackListChanged(MusicMediaKind.Audio, true, true));
        Assert.Null(state.TrackListChanged(MusicMediaKind.Unknown, true, false));
        Assert.Null(state.TrackListChanged(MusicMediaKind.Audio, false, false));
        Assert.True(state.TrackListChanged(MusicMediaKind.Audio, true, false));
        state.Switch(true, true);
        Assert.False(state.ManualOverride);
        Assert.False(state.TrackListChanged(MusicMediaKind.Video, true, false));
    }
    [Fact]
    public void ManualSwitchTogglesOverrideInsteadOfResettingItForEveryNewFile()
    {
        var state = new MusicModeState(); state.Switch(true, true); state.Switch(false, false);
        Assert.True(state.ManualOverride);
        Assert.Null(state.TrackListChanged(MusicMediaKind.Audio, true, false));
        state.Switch(true, false);
        Assert.False(state.ManualOverride);
        Assert.False(state.TrackListChanged(MusicMediaKind.Video, true, false));
        state.Switch(false, true); Assert.False(state.ManualOverride);
    }
    [Fact]
    public void RepeatedSameModeDoesNotFlipManualOverride()
    {
        var state = new MusicModeState(); state.Switch(true, false); state.Switch(true, false);
        Assert.True(state.ManualOverride);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1.25)]
    [InlineData(1.75)]
    [InlineData(2)]
    public void InitialWindowUsesIina300By372ContentPlusSystemCaption(double scale)
    {
        var chrome = new VideoSize(16 * scale, 39 * scale);
        var frame = MusicModeLayout.Initial(new(600, 200, 1400, 900), new(1, 1), true, new(0, 0, 3840, 2076), chrome, scale);
        Assert.Equal(300 * scale + chrome.Width, frame.Width);
        Assert.Equal(372 * scale + chrome.Height, frame.Height);
        var hidden = MusicModeLayout.Size(frame, new(1, 1), false, new(0, 0, 3840, 2076), chrome, scale);
        Assert.Equal(frame.Y, hidden.Y); Assert.Equal(frame.Width, hidden.Width);
        Assert.Equal(72 * scale + chrome.Height, hidden.Height);
        Assert.Equal(frame, MusicModeLayout.Size(hidden, new(1, 1), true, new(0, 0, 3840, 2076), chrome, scale));
    }
    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)]
    public void ResizeKeepsArtworkAspectOutsideSeventyTwoDipControls(int edge)
    {
        var frame = MusicModeLayout.Size(new(500, 300, 900, 740), new(16, 9), true, new(0, 0, 3840, 2076), new(28, 68), 1.75, edge);
        Assert.Equal(16d / 9, (frame.Width - 28) / (frame.Height - 68 - 126), 6);
        Assert.InRange(frame.Right, 0, 3840); Assert.InRange(frame.Bottom, 0, 2076);
    }
    [Fact]
    public void TallArtworkKeepsControlsWideEnoughAndFitsWorkArea()
    {
        var frame = MusicModeLayout.Initial(new(100, 100, 1000, 800), new(1, 20), true, new(0, 0, 1920, 1040), new(28, 68), 1.75);
        Assert.Equal(553, frame.Width); Assert.Equal(1040, frame.Height); Assert.Equal(0, frame.Y);
    }
    [Theory]
    [InlineData(1, 1)] [InlineData(1.5, 1.75)] [InlineData(2.25, 1.75)] [InlineData(2.25, 2)]
    public void LargerTextReservesBothMetadataLinesWithoutChangingArtworkOrReturnGeometry(double textScale, double dpi)
    {
        var rows = MusicModeLayout.ControlRows(textScale);
        Assert.True(rows.Header >= 32 * textScale); Assert.True(rows.Timeline >= 16 * textScale);
        double bar = rows.Header + rows.Timeline;
        var work = new WindowFrame(0, 0, 3840, 2076); var chrome = new VideoSize(28, 68);
        var initial = MusicModeLayout.Initial(new(500, 300, 1400, 860), new(16, 9), true, work, chrome, dpi, bar);
        Assert.Equal(300 * dpi + chrome.Width, initial.Width);
        Assert.Equal(16d / 9, (initial.Width - chrome.Width) / (initial.Height - chrome.Height - bar * dpi), 6);
        var hidden = MusicModeLayout.Size(initial, new(16, 9), false, work, chrome, dpi, barHeight: bar);
        Assert.Equal(chrome.Height + bar * dpi, hidden.Height);
        Assert.Equal(initial, MusicModeLayout.Size(hidden, new(16, 9), true, work, chrome, dpi, barHeight: bar));
    }

    [Fact]
    public async Task MusicPreferencesDefaultLikeIinaAndMergeAcrossWindows()
    {
        string dir = Path.Combine(Path.GetTempPath(), "mani-music-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir); string path = Path.Combine(dir, "settings.json");
            await File.WriteAllTextAsync(path, "{\"schemaVersion\":1,\"volumePercent\":23,\"unknown\":42}", TestContext.Current.CancellationToken);
            var store = new PlayerSettingsStore(path); var shared = new SharedPlayerSettings(store);
            var baseline = await shared.LoadAsync(); Assert.True(baseline.AutoSwitchToMusicMode); Assert.True(baseline.MusicModeShowAlbumArt);
            await shared.UpdateAsync(baseline, baseline with { AutoSwitchToMusicMode = false });
            await shared.UpdateAsync(baseline, baseline with { MusicModeShowAlbumArt = false });
            var saved = await store.LoadAsync(TestContext.Current.CancellationToken);
            Assert.False(saved.AutoSwitchToMusicMode); Assert.False(saved.MusicModeShowAlbumArt); Assert.Equal(23, saved.VolumePercent);
            Assert.Contains("unknown", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}

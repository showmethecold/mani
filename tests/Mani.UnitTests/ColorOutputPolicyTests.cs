using Mani.Playback;

namespace Mani.UnitTests;

// Preserve IINA's classification and the existing Windows container adaptation.
public sealed class ColorOutputPolicyTests
{
    [Theory]
    [InlineData("bt.2020", "pq", true)]
    [InlineData("bt.2020", "hlg", true)]
    [InlineData("display-p3", "pq", true)]
    [InlineData("display-p3", "hlg", true)]
    [InlineData("bt.709", "pq", false)]
    [InlineData("bt.2020", "srgb", false)]
    [InlineData("", "pq", false)]
    [InlineData("bt.2020", "", false)]
    [InlineData("unknown", "pq", false)]
    public void HdrClassificationRequiresKnownPrimariesAndTransfer(string primaries, string transfer, bool expected)
    {
        Assert.Equal(expected, ColorOutputPolicy.IsHdrVideo(primaries, transfer));
    }

    [Fact]
    public void AnSdrDisplayDoesNotRequestAnHdrContainer()
    {
        Assert.Equal(new ColorOutputTarget(false, "bt.709", "srgb", "auto"),
            ColorOutputPolicy.Select("bt.2020", "pq", new(false, 1000)));
    }

    [Fact]
    public void ForceSdrUsesTheExplicitReferencePeak()
    {
        Assert.Equal(new ColorOutputTarget(false, "bt.709", "srgb", "100"),
            ColorOutputPolicy.Select("bt.2020", "pq", new(true, 1000), forceSdr: true));
    }

    [Fact]
    public void SdrMediaKeepsTheSdrTargetOnAnHdrDisplay()
    {
        Assert.Equal(new ColorOutputTarget(false, "bt.709", "srgb", "auto"),
            ColorOutputPolicy.Select("bt.709", "bt.1886", new(true, 1000)));
    }

    [Fact]
    public void P3HlgUsesTheWindowsBt2020PqContainer()
    {
        Assert.Equal(new ColorOutputTarget(true, "bt.2020", "pq", "436"),
            ColorOutputPolicy.Select("display-p3", "hlg", new(true, 436.208)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(9.99)]
    [InlineData(10000.01)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void InvalidPeaksUseAutomaticNegotiation(double peak)
    {
        Assert.Equal("auto", ColorOutputPolicy.Select("bt.2020", "pq", new(true, peak)).Peak);
    }

    [Theory]
    [InlineData(10, "10")]
    [InlineData(10000, "10000")]
    [InlineData(436.208, "436")]
    public void ValidPeaksUseTheExpectedIntegralValue(double peak, string expected)
    {
        Assert.Equal(expected, ColorOutputPolicy.Select("bt.2020", "pq", new(true, peak)).Peak);
    }
}

using Mani.Playback;

namespace Mani.UnitTests;

public sealed class SubtitleFontFallbackTests
{
    [Theory]
    [InlineData(true, true, "Microsoft YaHei")]
    [InlineData(true, false, "Microsoft YaHei")]
    [InlineData(false, true, "Microsoft JhengHei")]
    [InlineData(false, false, "sans-serif")]
    public void DefaultRequiresAnInstalledFamily(bool yahei, bool jhenghei, string expected) =>
        Assert.Equal(expected, SubtitleFontFallback.SelectDefault(f => f == "Microsoft YaHei" ? yahei : jhenghei));

    [Theory]
    [InlineData("sans-serif", "Microsoft YaHei")]
    [InlineData(" SANS-SERIF ", "Microsoft YaHei")]
    [InlineData("Arial", "Arial")]
    [InlineData("微软雅黑", "微软雅黑")]
    [InlineData("Missing user font", "Missing user font")]
    [InlineData("  Consolas  ", "  Consolas  ")]
    [InlineData("serif", "serif")]
    public void OnlyGenericDefaultIsResolved(string requested, string expected) =>
        Assert.Equal(expected, SubtitleFontFallback.Resolve(requested, "Microsoft YaHei"));
}

using Mani.Playback;

namespace Mani.UnitTests;

// Classify synthetic counter samples only; do not create a swapchain or query a GPU.
public sealed class PresentationStatisticsTests
{
    private static PresentationStatistics.ReadResult First()
    {
        var frame = new PresentationStatistics.Frame(7, 10, 10, 90, 0);
        return new(100, 101, 1000, "test-chain", 0, frame, 0, 12, 0, 0, frame);
    }

    [Theory]
    [InlineData("stale", "stale", "advanced")]
    [InlineData("advanced", "advanced", "advanced")]
    [InlineData("disjoint", "disjoint", "advanced")]
    [InlineData("unavailable", "unavailable", "advanced")]
    [InlineData("empty", "empty", "advanced")]
    [InlineData("future-clock", "invalid-clock", "advanced")]
    [InlineData("counter-reset", "counter-reset", "advanced")]
    [InlineData("partial", "partial-advance", "advanced")]
    [InlineData("present-reset", "stale", "counter-reset")]
    [InlineData("present-unavailable", "stale", "unavailable")]
    [InlineData("present-stale", "stale", "stale")]
    public void CountersAreClassifiedWithoutClaimingAMediaPresentationTime(string change, string frameStatus, string presentStatus)
    {
        var before = First();
        var after = before with { BeforeQpc = 200, AfterQpc = 201, LastPresentCount = 16 };
        after = change switch
        {
            "stale" => after,
            "advanced" => after with { FrameStatistics = before.FrameStatistics! with { PresentCount = 8, SyncQPCTime = 190 } },
            "disjoint" => after with { FrameHResult = unchecked((int)0x887A000B) },
            "unavailable" => after with { FrameHResult = -1, FrameStatistics = null },
            "empty" => after with { FrameStatistics = before.FrameStatistics! with { PresentCount = 0 } },
            "future-clock" => after with { FrameStatistics = before.FrameStatistics! with { SyncQPCTime = 202 } },
            "counter-reset" => after with { FrameStatistics = before.FrameStatistics! with { PresentCount = 1 } },
            "partial" => after with { FrameStatistics = before.FrameStatistics! with { PresentCount = 8 } },
            "present-reset" => after with { LastPresentCount = 1 },
            "present-unavailable" => after with { LastPresentHResult = -1, LastPresentCount = null },
            "present-stale" => after with { LastPresentCount = 12 },
            _ => throw new ArgumentOutOfRangeException(nameof(change))
        };
        var result = PresentationStatistics.Compare(before, after);
        Assert.Equal(frameStatus, result.FrameStatus);
        Assert.Equal(presentStatus, result.LastPresentStatus);
        Assert.False(result.FirstMediaFrameIdentified);
        Assert.False(result.PerformanceAccepted);
    }

    [Theory]
    [InlineData("different-chain")]
    [InlineData("different-frequency")]
    [InlineData("invalid-frequency")]
    [InlineData("overlapping-window")]
    [InlineData("reversed-window")]
    public void DifferentChainsOrInvalidWindowsAreIncomparable(string change)
    {
        var before = First();
        var after = before with { BeforeQpc = 200, AfterQpc = 201 };
        after = change switch
        {
            "different-chain" => after with { SwapChainIdentity = "other-chain" },
            "different-frequency" => after with { Frequency = 2000 },
            "invalid-frequency" => after with { Frequency = 0 },
            "overlapping-window" => after with { BeforeQpc = 100 },
            "reversed-window" => after with { AfterQpc = 199 },
            _ => throw new ArgumentOutOfRangeException(nameof(change))
        };
        var result = PresentationStatistics.Compare(before, after);
        Assert.Equal("incomparable", result.FrameStatus);
        Assert.Equal("incomparable", result.MediaStatus);
        Assert.Equal("incomparable", result.LastPresentStatus);
        Assert.False(result.PerformanceAccepted);
    }

    [Fact]
    public void AnUnavailableMediaInterfaceDoesNotInvalidateOtherCounters()
    {
        var before = First();
        var after = before with { BeforeQpc = 200, AfterQpc = 201, MediaQueryHResult = -1 };
        var result = PresentationStatistics.Compare(before, after);
        Assert.Equal("stale", result.FrameStatus);
        Assert.Equal("unavailable", result.MediaStatus);
        Assert.Equal("stale", result.LastPresentStatus);
    }
}

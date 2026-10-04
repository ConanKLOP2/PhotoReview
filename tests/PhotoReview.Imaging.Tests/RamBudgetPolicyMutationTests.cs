using PhotoReview.Core.Catalog;

namespace PhotoReview.Imaging.Tests;

/// <summary>
/// Mutation-testing follow-ups for <see cref="RamBudgetPolicy"/> (docs/MUTATION-TESTING.md): unknown RAM, the bounded/unbounded
/// box tests, a measured mean of 0 or exactly the box bound, partly known dimensions, a RAW in a width-only box and the
/// overflow guard of the folder sum.
/// </summary>
[Trait("Category", "HotPath")]
public sealed class RamBudgetPolicyMutationTests
{
    private static CatalogEntry Entry(string path, long? length = null, int? width = null, int? height = null) =>
        new(path) { Length = length, Width = width, Height = height };

    [Theory(DisplayName = "The source-bytes clamp leaves the request unchanged when physical RAM is unknown")]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void ClampSourceBytes_UnknownRam_ReturnsRequest(long physical)
    {
        Assert.Equal(123_456_789L, RamBudgetPolicy.ClampSourceBytesToPhysicalMemory(123_456_789L, physical));
    }

    // ---- entry-list overload --------------------------------------------------------------------------------------

    [Theory(DisplayName = "EstimateFolderPreviewBytes(entries): a box unbounded on either axis estimates from each entry's compressed size x 10")]
    [InlineData(256, 0)]
    [InlineData(0, 256)]
    public void EstimateEntries_UnboundedBox_UsesCompressedSize(int width, int height)
    {
        var entries = new[] { Entry("a.jpg", length: 1000), Entry("b.png", length: 500) };

        Assert.Equal(15_000, RamBudgetPolicy.EstimateFolderPreviewBytes(entries, new DecodeBox(width, height)));
    }

    [Fact(DisplayName = "EstimateFolderPreviewBytes(entries): a measured mean of 0 is not a measurement; each entry costs the box bound")]
    public void EstimateEntries_ZeroMeasured_IsIgnored()
    {
        var entries = new[] { Entry("a.jpg", length: 1000), Entry("b.jpg", length: 1000) };

        Assert.Equal(2 * 40_000, RamBudgetPolicy.EstimateFolderPreviewBytes(entries, new DecodeBox(100, 100), measuredMeanPreviewBytes: 0));
    }

    [Fact(DisplayName = "EstimateFolderPreviewBytes(entries): a measured mean exactly at the box bound is capped at it")]
    public void EstimateEntries_MeasuredAtBoxBound_IsCapped()
    {
        var entries = new[] { Entry("a.jpg"), Entry("b.jpg"), Entry("c.jpg") };
        var box = new DecodeBox(100, 100);

        Assert.Equal(3 * 40_000, RamBudgetPolicy.EstimateFolderPreviewBytes(entries, box, measuredMeanPreviewBytes: 40_000));
        Assert.Equal(3 * 25_000, RamBudgetPolicy.EstimateFolderPreviewBytes(entries, box, measuredMeanPreviewBytes: 20_000));
        Assert.Equal(3 * 60_000, RamBudgetPolicy.EstimateFolderPreviewBytes(entries, box, measuredMeanPreviewBytes: 48_000));
    }

    [Theory(DisplayName = "EstimateFolderPreviewBytes(entries): an entry with only one known dimension is estimated at the box bound, not from its dimensions")]
    [InlineData(100, 0)]
    [InlineData(0, 100)]
    [InlineData(100, null)]
    [InlineData(null, 100)]
    public void EstimateEntries_PartlyKnownDimensions_UseTheBoxBound(int? width, int? height)
    {
        var entries = new[] { Entry("a.jpg", length: 1000, width: width, height: height) };

        Assert.Equal(40_000, RamBudgetPolicy.EstimateFolderPreviewBytes(entries, new DecodeBox(100, 100)));
    }

    [Fact(DisplayName = "EstimateFolderPreviewBytes(entries): a RAW with known dimensions in a width-only box is scaled by the box width (preview may be 2x the sensor)")]
    public void EstimateEntries_RawInWidthOnlyBox_ScalesByWidth()
    {
        var entries = new[] { Entry("a.cr2", length: 5_000_000, width: 4000, height: 3000) };

        // preview assumed 8000 x 6000; the box width 2000 scales it by 0.25 -> 2000 x 1500 x 4 bytes
        Assert.Equal(12_000_000, RamBudgetPolicy.EstimateFolderPreviewBytes(entries, new DecodeBox(2000, 0)));
        // a height-only box scales by height: 0.5 -> 4000 x 3000 x 4
        Assert.Equal(48_000_000, RamBudgetPolicy.EstimateFolderPreviewBytes(entries, new DecodeBox(0, 3000)));
    }

    [Fact(DisplayName = "EstimateFolderPreviewBytes(entries): a sum that reaches the long range disables whole-folder preload (long.MaxValue), without overflowing")]
    public void EstimateEntries_SumReachingLongRange_IsMaxValue()
    {
        var box = new DecodeBox(1 << 30, 1 << 30); // 2^60 pixels x 4 bytes = 2^62 per image
        var entries = new[] { Entry("a.jpg"), Entry("b.jpg") }; // 2^63 in total

        Assert.Equal(long.MaxValue, RamBudgetPolicy.EstimateFolderPreviewBytes(entries, box));
    }
}

namespace PhotoReview.Imaging.Tests;

using PhotoReview.Core.Catalog;

[Trait("Category", "HotPath")]
public sealed class RamBudgetPolicyTests
{
    [Fact]
    public void WholeFolderRequiresCapacityAndMemoryHeadroom()
    {
        Assert.True(RamBudgetPolicy.ShouldPreloadWholeFolderEstimate(1_000, 2_000, new FakeMemoryProbe(true)));
        Assert.True(RamBudgetPolicy.ShouldPreloadWholeFolderEstimate(1_000, 1_000, new FakeMemoryProbe(true)));
        Assert.False(RamBudgetPolicy.ShouldPreloadWholeFolderEstimate(1_000, 999, new FakeMemoryProbe(true)));
        Assert.False(RamBudgetPolicy.ShouldPreloadWholeFolderEstimate(1_000, 2_000, new FakeMemoryProbe(false)));
    }

    [Fact]
    public void RawOriginalEstimateWithoutDimensionsScalesTheCompressedFileLengthByTheDocumentedFactor()
    {
        var small = new CatalogEntry("small.cr3").WithMetadata(1_000, DateTime.UnixEpoch);
        var large = new CatalogEntry("large.cr3").WithMetadata(100_000_000, DateTime.UnixEpoch);

        Assert.Equal(1_000 * 6L, RamBudgetPolicy.EstimateFolderPreviewBytes([small], DecodeBox.Unbounded));
        Assert.Equal(100_000_000 * 6L, RamBudgetPolicy.EstimateFolderPreviewBytes([large], DecodeBox.Unbounded));
    }

    [Fact]
    public void RawFolderEstimateUsesPerFileDimensionsAtTheDecodeBox()
    {
        const int imageCount = 200;
        const int targetWidth = 2048;
        const int targetHeight = 1536;
        var entries = Enumerable.Range(0, imageCount)
            .Select(index => new CatalogEntry($"image-{index:D3}.arw")
                .WithMetadata(20_000_000 + index, DateTime.UnixEpoch, 4000, 3000))
            .ToArray();

        var estimate = RamBudgetPolicy.EstimateFolderPreviewBytes(entries, new DecodeBox(targetWidth, targetHeight));
        var measured = (long)imageCount * targetWidth * targetHeight * 4;

        Assert.InRange(estimate, (long)(measured * 0.8), (long)(measured * 1.2));
    }

    private static CatalogEntry[] Jpegs(int count, long length, int width, int height) => Enumerable.Range(0, count)
        .Select(index => new CatalogEntry($"photo-{index:D3}.jpg").WithMetadata(length, DateTime.UnixEpoch, width, height))
        .ToArray();

    [Theory]
    [InlineData(100_000.0, 200L * 125_000)]      // measured + 25 % margin, under the box bound
    [InlineData(3_600_000.0, 200L * 4_000_000)]  // margin would pass the 4 MB bound: capped at it
    [InlineData(8_000_000.0, 200L * 10_000_000)] // 16-bit images measured above the bound: the measurement wins
    public void EntriesEstimate_WithMeasuredMean_MatchesTheScalarCalibrationEvenWhenDimensionsAreKnown(double measured, long expected)
    {
        var box = new DecodeBox(1000, 1000);
        var entries = Jpegs(200, length: 1, width: 4000, height: 3000);

        var fromEntries = RamBudgetPolicy.EstimateFolderPreviewBytes(entries, box, measured);

        Assert.Equal(expected, fromEntries);
        Assert.Equal(RamBudgetPolicy.EstimateFolderPreviewBytes(200, box, totalSourceBytes: 1, measuredMeanPreviewBytes: measured), fromEntries);
    }

    [Fact]
    public void EntriesEstimate_UnboundedBoxWithKnownDimensions_KeepsTheCompressedTimesTenEstimateForNonRawFiles()
    {
        var entries = Jpegs(200, length: 500, width: 4000, height: 3000);

        var fromEntries = RamBudgetPolicy.EstimateFolderPreviewBytes(entries, DecodeBox.Unbounded);

        Assert.Equal(200L * 500 * 10, fromEntries);
        Assert.Equal(RamBudgetPolicy.EstimateFolderPreviewBytes(200, DecodeBox.Unbounded, totalSourceBytes: 200L * 500), fromEntries);
    }

    [Fact]
    public void EntriesEstimate_UnboundedBoxWithKnownDimensions_UsesTheMeasuredMeanWhenAvailable()
    {
        var entries = Jpegs(10, length: 500, width: 4000, height: 3000);

        Assert.Equal(10L * 250_000, RamBudgetPolicy.EstimateFolderPreviewBytes(entries, DecodeBox.Unbounded, measuredMeanPreviewBytes: 200_000));
    }
}


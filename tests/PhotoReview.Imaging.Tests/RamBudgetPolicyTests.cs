namespace PhotoReview.Imaging.Tests;

using PhotoReview.Core.Catalog;

[Trait("Category", "HotPath")]
public sealed class RamBudgetPolicyTests
{
    [Fact]
    public void DimensionEstimateScalesToTargetWidth()
    {
        var bytes = RamBudgetPolicy.EstimateDecodedBytes([new RamBudgetEntry(100, 4000, 2000, ".jpg")], 1000);

        Assert.Equal(1000L * 500 * 4, bytes);
    }

    [Fact]
    public void MissingDimensionsUseFormatExpansionFactors()
    {
        var bytes = RamBudgetPolicy.EstimateDecodedBytes([
            new RamBudgetEntry(100, Extension: ".jpg"),
            new RamBudgetEntry(100, Extension: ".png")], 1000);

        Assert.Equal(1300, bytes);
    }

    [Fact]
    public void MeasuredBytesPerPixelOverridesDefaultForKnownDimensions()
    {
        var bytes = RamBudgetPolicy.EstimateDecodedBytes([new RamBudgetEntry(100, 100, 100)], 1000, 8);

        Assert.Equal(80_000, bytes);
    }

    [Fact]
    public void WholeFolderRequiresCapacityAndMemoryHeadroom()
    {
        var allowed = RamBudgetPolicy.Decide([new RamBudgetEntry(100, Extension: ".jpg")], 1000, 2_000,
            new FakeMemoryProbe(true));
        var deniedByCapacity = RamBudgetPolicy.Decide([new RamBudgetEntry(100, Extension: ".jpg")], 1000, 999,
            new FakeMemoryProbe(true));
        var deniedByPressure = RamBudgetPolicy.Decide([new RamBudgetEntry(100, Extension: ".jpg")], 1000, 2_000,
            new FakeMemoryProbe(false));

        Assert.True(allowed.ShouldPreloadWholeFolder);
        Assert.False(deniedByCapacity.ShouldPreloadWholeFolder);
        Assert.False(deniedByPressure.ShouldPreloadWholeFolder);
    }

    [Fact]
    public void SourceOnlyDecisionUsesConservativeJpegFactor()
    {
        Assert.True(RamBudgetPolicy.ShouldPreloadWholeFolder(100, 1_000, new FakeMemoryProbe(true)));
        Assert.False(RamBudgetPolicy.ShouldPreloadWholeFolder(101, 1_000, new FakeMemoryProbe(true)));
    }

    [Fact]
    public void RawOriginalEstimateWithoutDimensionsDoesNotUseCompressedFileLength()
    {
        var small = new CatalogEntry("small.cr3").WithMetadata(1_000, DateTime.UnixEpoch);
        var large = new CatalogEntry("large.cr3").WithMetadata(100_000_000, DateTime.UnixEpoch);

        Assert.Equal(long.MaxValue, RamBudgetPolicy.EstimateFolderPreviewBytes([small], DecodeBox.Unbounded));
        Assert.Equal(long.MaxValue, RamBudgetPolicy.EstimateFolderPreviewBytes([large], DecodeBox.Unbounded));
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
}


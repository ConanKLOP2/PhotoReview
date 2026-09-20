namespace PhotoReview.Imaging.Tests;

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
}


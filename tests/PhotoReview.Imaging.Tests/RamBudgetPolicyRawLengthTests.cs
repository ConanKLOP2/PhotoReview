using PhotoReview.Core.Catalog;
using PhotoReview.Imaging.Decoding;
using PhotoReview.Imaging.Preload;
using Xunit;

namespace PhotoReview.Imaging.Tests;

/// <summary>
/// CatalogEntry.Width/Height are never populated in production, so the RAW estimate must not depend on them: without
/// dimensions it derives from the compressed RAW length (documented factor) or the calibrated measured mean, and the
/// per-entry work must neither allocate nor repeat on every order rebuild.
/// </summary>
public sealed class RamBudgetPolicyRawLengthTests
{
    private static CatalogEntry Raw(int index, long length) => new CatalogEntry($"folder/IMG_{index:D6}.CR2").WithMetadata(length, DateTime.UnixEpoch);

    private static CatalogEntry[] RawFolder(int count, long length) => Enumerable.Range(0, count).Select(i => Raw(i, length)).ToArray();

    [Fact]
    public void Unbounded_RawWithoutDimensions_UsesTheCompressedLengthTimesTheDocumentedFactor()
    {
        var entries = RawFolder(10, 25_000_000);

        var estimate = RamBudgetPolicy.EstimateFolderPreviewBytes(entries, DecodeBox.Unbounded);

        Assert.Equal((long)(10 * 25_000_000 * RamBudgetPolicy.RawCompressedToPreviewFactor), estimate);
        Assert.NotEqual(long.MaxValue, estimate);
    }

    [Fact]
    public void Unbounded_RawWithoutDimensionsAndWithoutLength_StaysConservative()
    {
        var entries = new[] { new CatalogEntry("folder/IMG_000001.CR2") };

        Assert.Equal(long.MaxValue, RamBudgetPolicy.EstimateFolderPreviewBytes(entries, DecodeBox.Unbounded));
    }

    [Fact]
    public void Bounded_RawWithoutDimensions_StaysAtTheBoxBound()
    {
        var entries = RawFolder(10, 25_000_000);

        var estimate = RamBudgetPolicy.EstimateFolderPreviewBytes(entries, new DecodeBox(1920, 1080));

        Assert.Equal(10L * 1920 * 1080 * 4, estimate);
    }

    [Fact]
    public void Measured_RawWithoutDimensions_UsesTheCalibratedMean()
    {
        var entries = RawFolder(10, 25_000_000);

        Assert.Equal(10L * 1_250_000, RamBudgetPolicy.EstimateFolderPreviewBytes(entries, DecodeBox.Unbounded, 1_000_000));
    }

    [Fact]
    public void Estimate_HundredThousandMixedEntries_AllocatesNothingPerEntry()
    {
        var entries = new CatalogEntry[100_000];
        for (var i = 0; i < entries.Length; i++)
            entries[i] = i % 2 == 0 ? Raw(i, 25_000_000) : new CatalogEntry($"folder/IMG_{i:D6}.jpg").WithMetadata(5_000_000, DateTime.UnixEpoch);
        // Warm every path (JIT and static initialisers allocate on first use, which is not per-entry cost).
        for (var warm = 0; warm < 2; warm++)
        {
            _ = RamBudgetPolicy.EstimateFolderPreviewBytes(entries, DecodeBox.Unbounded);
            _ = RamBudgetPolicy.EstimateFolderPreviewBytes(entries, new DecodeBox(1920, 1080));
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        var unbounded = RamBudgetPolicy.EstimateFolderPreviewBytes(entries, DecodeBox.Unbounded);
        var bounded = RamBudgetPolicy.EstimateFolderPreviewBytes(entries, new DecodeBox(1920, 1080));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(unbounded > 0 && bounded > 0);
        // One path-extension string per entry would be >= 3 MB here. The budget is 64 KB, not a few KB: the first hot loop over
        // 100k entries triggers tiered-JIT/OSR work that allocates a few KB on this thread (8 KB seen), which is constant cost.
        Assert.True(allocated < 64 * 1024, $"allocated {allocated} bytes for 200k entry visits");
    }

    [Fact]
    public void Estimate_WithMeasuredMean_DoesNotVisitEntries()
    {
        var entries = RawFolder(100_000, 25_000_000);
        _ = RamBudgetPolicy.EstimateFolderPreviewBytes(entries, DecodeBox.Unbounded, 1_000_000);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var estimate = RamBudgetPolicy.EstimateFolderPreviewBytes(entries, DecodeBox.Unbounded, 1_000_000);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(100_000L * 1_250_000, estimate);
        Assert.True(allocated < 1024, $"allocated {allocated} bytes");
    }

    [Fact]
    public void FolderEstimateCache_SameInputs_ComputesOnce_AndRecomputesWhenAnyInputChanges()
    {
        var cache = new FolderEstimateCache();
        var entries = RawFolder(50, 25_000_000);
        var box = new DecodeBox(1920, 1080);

        var first = cache.GetOrCompute(entries, box, null);
        var again = cache.GetOrCompute(entries, box, null);
        Assert.Equal(first, again);
        Assert.Equal(1, cache.ComputeCount);

        cache.GetOrCompute(entries, box, 1_000_000);              // calibration changed
        cache.GetOrCompute(entries, DecodeBox.Unbounded, 1_000_000); // mode (box) changed
        cache.GetOrCompute(RawFolder(50, 25_000_000), DecodeBox.Unbounded, 1_000_000); // different catalog snapshot
        Assert.Equal(4, cache.ComputeCount);
    }
}

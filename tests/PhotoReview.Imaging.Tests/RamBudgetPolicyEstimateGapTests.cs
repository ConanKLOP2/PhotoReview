using PhotoReview.Core.Catalog;
using PhotoReview.Imaging.Preload;

namespace PhotoReview.Imaging.Tests;

/// <summary>RV-T48: estimate rules not pinned elsewhere (PNG factor per overload, empty input, mixed RAW with unknown length).</summary>
[Trait("Category", "HotPath")]
public sealed class RamBudgetPolicyEstimateGapTests
{
    [Fact]
    public void EstimateFolderPreviewBytes_UnboundedBoxWithPngEntries_UsesTheJpegFactorTenOfTheEntriesOverload()
    {
        // Pinned on purpose: the entries overload does not special-case PNG (x10).
        var entries = new[] { new CatalogEntry("a.png").WithMetadata(1_000, DateTime.UnixEpoch) };

        Assert.Equal(10_000L, RamBudgetPolicy.EstimateFolderPreviewBytes(entries, DecodeBox.Unbounded));
    }

    [Fact]
    public void Estimates_ZeroEntries_AreZeroForEveryBox()
    {
        Assert.Equal(0L, RamBudgetPolicy.EstimateFolderPreviewBytes(Array.Empty<CatalogEntry>(), DecodeBox.Unbounded));
        Assert.Equal(0L, RamBudgetPolicy.EstimateFolderPreviewBytes(Array.Empty<CatalogEntry>(), new DecodeBox(1920, 1080)));
    }

    [Fact]
    public void EstimateFolderPreviewBytes_RawWithUnknownLengthAfterOtherEntries_DisablesWholeFolderPreload()
    {
        var entries = new[]
        {
            new CatalogEntry("a.jpg").WithMetadata(1_000, DateTime.UnixEpoch),
            new CatalogEntry("b.cr3"), // length unknown
            new CatalogEntry("c.jpg").WithMetadata(1_000, DateTime.UnixEpoch),
        };

        Assert.Equal(long.MaxValue, RamBudgetPolicy.EstimateFolderPreviewBytes(entries, DecodeBox.Unbounded));
    }
}
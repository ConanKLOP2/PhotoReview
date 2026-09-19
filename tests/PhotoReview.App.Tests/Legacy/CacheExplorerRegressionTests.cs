using PhotoReview.App;
using System.IO;
using PhotoReview.Core.Caching;
using PhotoReview.Core.Catalog;

namespace PhotoReview.Tests.Unit;

/// <summary>
/// Behavior contracts for cache/preload/Explorer regressions from ERROR-HISTORY.
/// </summary>
public sealed class CacheExplorerRegressionTests : IDisposable
{
    private readonly TempRoot _root = new("explorer-regression");
    private readonly string _folder;
    private readonly string _a;
    private readonly string _b;
    private readonly string _c;

    public CacheExplorerRegressionTests()
    {
        _folder = _root.Dir("explorer-regression");
        _a = _root.File(Path.Combine("explorer-regression", "photo 2.jpg"), 1, 2);
        _b = _root.File(Path.Combine("explorer-regression", "photo 10.jpg"), 3, 4, 5);
        _c = _root.File(Path.Combine("explorer-regression", "photo 11.jpg"), 6, 7, 8, 9);
    }

    public void Dispose() => _root.Dispose();

    private ExplorerViewSnapshot Snapshot() => new(_folder, [_c, _a, _b], [], ExplorerGroupState.None,
        ExplorerOrderStatus.Available, null, DateTime.UtcNow);

    [Fact(DisplayName = "RAM cache hit returns the same decoded variant")]
    public void RamCacheHitReturnsSameDecodedVariant()
    {
        var cache = new BoundedLruCache<ImageCacheKey, string>(64, value => value.Length);
        var preview = ImageCacheKey.Create(_a, false, 2048);
        cache.Set(preview, "preview");
        Assert.True(cache.TryGet(preview, out var value) && value == "preview");
    }

    [Fact(DisplayName = "Original quality does not reuse preview cache entry")]
    public void OriginalQualityDoesNotReusePreviewCacheEntry()
    {
        var cache = new BoundedLruCache<ImageCacheKey, string>(64, value => value.Length);
        cache.Set(ImageCacheKey.Create(_a, false, 2048), "preview");
        Assert.False(cache.TryGet(ImageCacheKey.Create(_a, true, 0), out _));
    }

    [Fact(DisplayName = "Replaced source cannot reuse stale decoded cache")]
    public void ReplacedSourceCannotReuseStaleDecodedCache()
    {
        var cache = new BoundedLruCache<ImageCacheKey, string>(64, value => value.Length);
        var preview = ImageCacheKey.Create(_a, false, 2048);
        cache.Set(preview, "preview");
        File.WriteAllBytes(_a, [9, 9, 9, 9, 9]);
        var replaced = ImageCacheKey.Create(_a, false, 2048);
        Assert.True(replaced != preview && !cache.TryGet(replaced, out _));
    }

    [Fact(DisplayName = "Preload order prioritizes next images then previous images without duplicates")]
    public void PreloadOrderPrioritizesNextThenPrevious()
    {
        var partial = PreloadOrderService.Build(1, 5, false).ToArray();
        Assert.True(partial.SequenceEqual(new[] { 2, 3, 4, 0 }) && partial.Distinct().Count() == partial.Length);
    }

    [Fact(DisplayName = "Full-folder preload schedules every catalog item exactly once")]
    public void FullFolderPreloadSchedulesEveryItemOnce()
    {
        var full = PreloadOrderService.Build(2, 100, true).ToArray();
        Assert.True(full.Length == 99 && full.Distinct().Count() == 99 && !full.Contains(2));
    }

    [Fact(DisplayName = "Explorer validator preserves complete native order")]
    public void ExplorerValidatorPreservesCompleteNativeOrder()
    {
        Assert.True(ExplorerSnapshotValidator.TryValidate(Snapshot(), [_a, _b, _c], out var ordered, out _)
            && ordered.SequenceEqual(new[] { _c, _a, _b }));
    }

    [Fact(DisplayName = "Explorer validator rejects duplicate native items")]
    public void ExplorerValidatorRejectsDuplicateNativeItems()
    {
        Assert.True(!ExplorerSnapshotValidator.TryValidate(Snapshot() with { OrderedPaths = [_c, _c, _a] },
                [_a, _b, _c], out _, out var duplicateReason)
            && duplicateReason?.Contains("duplicate", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact(DisplayName = "Explorer validator rejects an item outside the folder")]
    public void ExplorerValidatorRejectsItemOutsideFolder()
    {
        Assert.True(!ExplorerSnapshotValidator.TryValidate(
                Snapshot() with { OrderedPaths = [_c, _a, _root.Combine("outside.jpg")] },
                [_a, _b, _c], out _, out var outsideReason)
            && outsideReason?.Contains("outside", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact(DisplayName = "Explorer validator rejects incomplete snapshots")]
    public void ExplorerValidatorRejectsIncompleteSnapshots()
    {
        Assert.True(!ExplorerSnapshotValidator.TryValidate(Snapshot() with { OrderedPaths = [_c, _a] },
                [_a, _b, _c], out _, out var missingReason)
            && missingReason?.Contains("complete", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact(DisplayName = "Explorer validator falls back on malformed native paths")]
    public void ExplorerValidatorFallsBackOnMalformedNativePaths()
    {
        Assert.False(ExplorerSnapshotValidator.TryValidate(Snapshot() with { OrderedPaths = ["not-a-path"] },
            [_a, _b, _c], out _, out _));
    }
}

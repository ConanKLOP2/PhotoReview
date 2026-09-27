using System.Reflection;

namespace PhotoReview.Imaging.Tests.Caching;

/// <summary>
/// R06 (conditional, full-code-review-2026-09-27): queued persist bitmaps stay referenced outside the RAM LRU
/// cache's byte budget until a background worker writes and drops them (<see cref="PreviewImageService"/>'s
/// <c>PersistQueueCapacity</c>/<c>PersistWorkerCount</c> constants). Measurement (see PERF-STATUS.md): the worst
/// case is <c>(PersistQueueCapacity + PersistWorkerCount)</c> bitmaps, each at most the app's own "largest sane
/// preview" box (<see cref="RamBudgetPolicy.MinimumBudgetBoxWidth"/> x
/// <see cref="RamBudgetPolicy.MinimumBudgetBoxHeight"/>, 4 bytes/pixel) -- about 0.56 GiB, well under the
/// smallest disk/RAM budget the app configures and negligible against the 16 GiB preview-cache target this class
/// is tuned for. No realistic slow-disk scenario could be constructed with the existing
/// test seams (the persist writer, <see cref="PreviewCacheFile.WriteAtomicallyAsync"/>, is a static method with no
/// injectable slow-writer seam), so no source change was made; this test only pins the two constants that bound
/// the overshoot so a future change to either is a deliberate, reviewed decision instead of a silent regression.
/// </summary>
[Trait("Category", "HotPath")]
public sealed class PreviewImagePersistQueueBoundTests
{
    [Fact(DisplayName = "PersistQueueCapacity + PersistWorkerCount (the worst-case count of bitmaps retained outside the RAM cache while queued for disk persistence) is pinned at 18")]
    public void PersistQueueBound_IsPinnedAt18()
    {
        var type = typeof(PreviewImageService);
        var capacity = GetPrivateConst<int>(type, "PersistQueueCapacity");
        var workers = GetPrivateConst<int>(type, "PersistWorkerCount");

        Assert.Equal(16, capacity);
        Assert.Equal(2, workers);
        Assert.Equal(18, capacity + workers);
    }

    private static T GetPrivateConst<T>(Type type, string name)
    {
        var field = type.GetField(name, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingFieldException(type.FullName, name);
        return (T)field.GetRawConstantValue()!;
    }
}

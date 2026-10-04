using PhotoReview.Core.Catalog;
using PhotoReview.Imaging.Preload;

namespace PhotoReview.Imaging.Tests;

/// <summary>Mutation-gap tests for <see cref="PreloadBusyBackoff"/> and <see cref="PreviewSizeSampler"/>.</summary>
public sealed class PreloadSmallGapTests
{
    private const string Path = @"C:\busy\small-gap.cr2";
    private static readonly DateTime Written = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static ImageCacheKey Key(long length) =>
        ImageCacheKey.Create(new CatalogEntry(Path).WithMetadata(length, Written), false, new DecodeBox(100, 100));

    [Fact(DisplayName = "A busy path that changed on disk (new key) restarts its busy count; the same key keeps counting")]
    public void NoteBusy_KeyChange_RestartsTheCount()
    {
        var backoff = new PreloadBusyBackoff();

        Assert.Equal(1, backoff.NoteBusy(Path, Key(1)));
        Assert.Equal(2, backoff.NoteBusy(Path, Key(1)));
        Assert.Equal(1, backoff.NoteBusy(Path, Key(2)));
        Assert.Equal(2, backoff.NoteBusy(Path, Key(2)));
    }

    [Fact(DisplayName = "Zero and negative byte samples are ignored and do not dilute the mean")]
    public void Record_NonPositiveBytes_AreIgnored()
    {
        var sampler = new PreviewSizeSampler();
        var box = new DecodeBox(100, 100);
        for (var i = 0; i < PreviewSizeSampler.MinimumSamples - 1; i++) sampler.Record(box, 100);
        sampler.Record(box, 0);
        sampler.Record(box, -50);
        Assert.Null(sampler.MeanBytes(box)); // zero and negative samples did not count towards the minimum

        sampler.Record(box, 100);

        Assert.Equal(100.0, sampler.MeanBytes(box));
    }
}

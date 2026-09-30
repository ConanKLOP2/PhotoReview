using System.IO;
using PhotoReview.Core.Diagnostics;

namespace PhotoReview.Imaging.Tests;

/// <summary>The preview decode path must refuse a RawFullDecode key in Release too, or its read would silently feed the decode EWMA.</summary>
[Trait("Category", "Slow")]
public sealed class PreviewDecodeRawFullKeyGuardTests
{
    [Fact]
    public async Task GetPreviewAsync_RawFullDecodeKey_ThrowsAndRecordsNoSourceRead()
    {
        using var root = new TempRoot("raw-full-key-guard");
        var path = Path.Combine(root.Dir("images"), "a.png");
        File.WriteAllBytes(path, TestImages.PreviewPng);
        var metrics = new ReviewMetrics();
        var service = new PreviewImageService(metrics, () => false, () => 512, capacityBytes: 16L * 1024 * 1024,
            diskCacheDirectory: root.Dir("disk-cache"));
        try
        {
            var key = ImageCacheKey.Create(path, false, 512, sourceKind: ImageSourceKind.RawFullDecode);

            await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetPreviewAsync(path, key));

            Assert.Equal(0, metrics.Snapshot().SourceReads);
        }
        finally
        {
            await service.ShutdownPersistWorkersAsync();
        }
    }
}

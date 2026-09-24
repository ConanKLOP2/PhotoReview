using System.IO;
using PhotoReview.Imaging;
using PhotoReview.App.Coordinators;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.IO;
using PhotoReview.Imaging.Caching;

namespace PhotoReview.App.Tests.Coordinators;

public sealed class DuplicateCleanupControllerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PhotoReview_DupCtl_" + Guid.NewGuid().ToString("N"));

    public DuplicateCleanupControllerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact(DisplayName = "Clear cache empties the source-bytes RAM cache and the preview disk cache (R2-F-16)")]
    public async Task ClearCacheAsync_ClearsSourceBytesCacheAndPreviewDisk()
    {
        var photo = Path.Combine(_root, "photo.bin");
        File.WriteAllBytes(photo, new byte[4096]);
        var sourceBytes = new SourceBytesCache(1024 * 1024);
        _ = sourceBytes.GetOrRead(photo);
        Assert.Equal(1, sourceBytes.Count);

        var previewDir = Path.Combine(_root, "preview");
        Directory.CreateDirectory(previewDir);
        var cached = Path.Combine(previewDir, "entry.pv4");
        File.WriteAllBytes(cached, new byte[128]);

        var metrics = new ReviewMetrics();
        var preview = new PreviewImageService(
            metrics, () => false, () => new DecodeBox(1920, 0), capacityBytes: 16 * 1024 * 1024,
            diskCacheDirectory: previewDir, sourceBytesCache: sourceBytes);
        var controller = new DuplicateCleanupController(
            new GenerationClock(), new ReviewCatalog(), fileActionService: null, hashService: null,
            new PhysicalFileSystem(), dialogService: null, new InlineUiScheduler(),
            preloadController: null, thumbnailCache: null, previewService: preview, new NullSink());

        await controller.ClearCacheAsync();

        Assert.Equal(0, sourceBytes.Count);
        Assert.False(File.Exists(cached));
    }

    private sealed class InlineUiScheduler : IUiScheduler
    {
        public void Post(Action action) => action();
        public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
        public ValueTask YieldAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private sealed class NullSink : IDuplicateCleanupSink
    {
        public void SetStatusText(string status) { }
        public Task OpenFolderAsync(string folder, string? initialPath = null) => Task.CompletedTask;
        public void NotifyNavigationStateChanged() { }
    }
}

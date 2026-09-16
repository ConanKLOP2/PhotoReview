using System.IO;
using System.Reflection;
using PhotoReview.App;

namespace PhotoReview.Tests.Unit;

/// <summary>
/// D10: PreloadScheduler's worker count and PreviewImageService's disk-cache use can each be
/// overridden -- in production via PHOTOREVIEW_DIAG_PRELOAD_WORKERS / PHOTOREVIEW_DIAG_DISABLE_DISKCACHE
/// (see DiagOptionsTests, D05), and here via the constructor parameters D10 added
/// (workerCountOverride / disableDiskCacheOverride) so these tests never have to touch process
/// environment variables (no "GlobalState" collection needed). Precedence for both is: explicit
/// parameter, then DiagOptions, then the AppConstants/enabled-by-default fallback -- these tests
/// only exercise the "explicit parameter" leg of that chain; DiagOptionsTests covers the
/// environment-variable parsing itself.
/// </summary>
public sealed class DiagOverrideTests : IAsyncLifetime
{
    private readonly TempRoot _root = new("diag-override");
    private readonly List<(PreviewImageService Service, string DiskDirectory)> _services = [];

    private PreviewImageService Track(PreviewImageService service, string diskDirectory)
    {
        _services.Add((service, diskDirectory));
        return service;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        // See PreviewImageServiceTests.DisposeAsync: every service's persist workers (and their
        // fire-and-forget prune passes) must be waited out before the TempRoot is deleted.
        await Task.WhenAll(_services.Select(s => s.Service.ShutdownPersistWorkersAsync()));
        await Task.WhenAll(_services.Select(s => DiskCacheStore.WaitForPruneAsync(s.DiskDirectory, TimeSpan.FromSeconds(5))));
        _root.Dispose();
    }

    private static string[] MakePreviewFiles(TempRoot root, string folderName, int count)
    {
        var folder = root.Dir(folderName);
        return Enumerable.Range(0, count).Select(i =>
        {
            var path = Path.Combine(folder, $"preview-{i}.png");
            File.WriteAllBytes(path, PreviewImageServiceTests.PreviewPng);
            return path;
        }).ToArray();
    }

    // PreloadScheduler keeps the resolved worker count (and the SemaphoreSlim sized from it)
    // in private fields; reflection is the only way to observe them without adding
    // production-only test hooks.
    private static int ReadWorkerCountField(PreloadScheduler scheduler) =>
        (int)typeof(PreloadScheduler).GetField("_workerCount", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(scheduler)!;

    private static SemaphoreSlim ReadPreloadSlotsField(PreloadScheduler scheduler) =>
        (SemaphoreSlim)typeof(PreloadScheduler).GetField("_preloadSlots", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(scheduler)!;

    [Fact(DisplayName = "workerCountOverride=2 sizes the preload semaphore to 2 slots")]
    public async Task WorkerCountOverrideTwoSizesSemaphoreToTwo()
    {
        // A real decode of the 1x1 fixture PNG completes far faster than any poll loop could
        // reliably sample mid-flight, so sampling SemaphoreSlim.CurrentCount during a preload
        // run would be flaky (the exact failure mode PERF-DIAGNOSIS-TASKS.md D10 warns about).
        // Checking the semaphore's configured capacity right after construction proves the same
        // thing deterministically: SemaphoreSlim itself guarantees WaitAsync never admits more
        // than that many concurrent holders, so "capacity == 2" is equivalent to "at most 2
        // decodes run at once" without depending on decode timing.
        var diskDirectory = _root.Dir("override-2-cache");
        var metrics = new ReviewMetrics();
        var service = Track(new PreviewImageService(metrics, () => false, () => 256,
            capacityBytes: 64L * 1024 * 1024, diskCacheDirectory: diskDirectory), diskDirectory);
        // 2 files, center 0: PreloadOrderService.Build only ever queues the one neighbor
        // (index 1) here, so RunPreloadSchedulerAsync never queues "workers" items in a single
        // batch and never reaches its Dispatcher.Yield call (examinedSinceYield >= workers in
        // PreloadScheduler.cs). That call requires a live WPF Dispatcher on the current thread;
        // the production app always has one, but this headless unit test does not, so hitting it
        // would throw (silently, since RunPreloadSchedulerAsync's outer catch swallows it) and
        // abandon the in-flight decode as unobserved background work -- PreloadSchedulerTests
        // avoids the same trap by always using fewer files than AppConstants.PreloadWorkerCount (8).
        var files = MakePreviewFiles(_root, "override-2", 2);
        using var scheduler = new PreloadScheduler(service, metrics, () => files, () => 0L, long.MaxValue,
            memoryLoadLimit: 1.0, hasHeadroom: _ => true, workerCountOverride: 2);

        Assert.Equal(2, ReadWorkerCountField(scheduler));
        Assert.Equal(2, ReadPreloadSlotsField(scheduler).CurrentCount);

        // Functional sanity check alongside the configuration check: preload still actually
        // warms the cache under the override, it just does so through (at most) 2 slots.
        await scheduler.PreloadAroundAsync(0);
        Assert.True(service.CacheCount > 0);
    }

    [Fact(DisplayName = "workerCountOverride=0 makes PreloadAroundAsync a no-op")]
    public async Task WorkerCountOverrideZeroIsNoOp()
    {
        var files = MakePreviewFiles(_root, "override-0", 5);
        var diskDirectory = _root.Dir("override-0-cache");
        var metrics = new ReviewMetrics();
        var service = Track(new PreviewImageService(metrics, () => false, () => 256,
            diskCacheDirectory: diskDirectory), diskDirectory);
        using var scheduler = new PreloadScheduler(service, metrics, () => files, () => 0L, long.MaxValue,
            memoryLoadLimit: 1.0, hasHeadroom: _ => true, workerCountOverride: 0);

        Assert.Equal(0, ReadWorkerCountField(scheduler));

        var task = scheduler.PreloadAroundAsync(0);
        Assert.True(task.IsCompletedSuccessfully, "PreloadAroundAsync must return an already-completed task when the worker count is 0.");
        Assert.Equal(0, service.CacheCount);
        Assert.Equal(0, metrics.Snapshot().SourceReads);
    }

    // AppConstants is internal to PhotoReview.App (no InternalsVisibleTo to this test project),
    // so the expected default is spelled out here instead of referenced; it must track
    // AppConstants.PreloadWorkerCount in PhotoReview.App/AppConstants.cs (currently 8).
    private const int DefaultPreloadWorkerCount = 8;

    [Fact(DisplayName = "With no override, PreloadScheduler uses AppConstants.PreloadWorkerCount")]
    public void NoOverrideUsesAppConstantsDefault()
    {
        var files = MakePreviewFiles(_root, "no-override", 2);
        var diskDirectory = _root.Dir("no-override-cache");
        var metrics = new ReviewMetrics();
        var service = Track(new PreviewImageService(metrics, () => false, () => 256, diskCacheDirectory: diskDirectory), diskDirectory);
        using var scheduler = new PreloadScheduler(service, metrics, () => files, () => 0L, long.MaxValue,
            memoryLoadLimit: 1.0, hasHeadroom: _ => true);

        Assert.Equal(DefaultPreloadWorkerCount, ReadWorkerCountField(scheduler));
        Assert.Equal(DefaultPreloadWorkerCount, ReadPreloadSlotsField(scheduler).CurrentCount);
    }

    [Fact(DisplayName = "disableDiskCacheOverride=true never writes a decoded preview to the disk cache")]
    public async Task DisableDiskCacheOverrideSkipsPersist()
    {
        var folder = _root.Dir("disable-write-source");
        var previewPath = Path.Combine(folder, "preview.png");
        File.WriteAllBytes(previewPath, PreviewImageServiceTests.PreviewPng);
        var diskDirectory = _root.Dir("disable-write-cache");
        var service = Track(new PreviewImageService(new ReviewMetrics(), () => false, () => 256,
            diskCacheDirectory: diskDirectory, disableDiskCacheOverride: true), diskDirectory);

        await service.GetPreviewAsync(previewPath);
        await service.ShutdownPersistWorkersAsync();

        var files = Directory.Exists(diskDirectory) ? Directory.GetFiles(diskDirectory, "*.png") : [];
        Assert.Empty(files);
    }

    [Fact(DisplayName = "disableDiskCacheOverride=true ignores an existing disk cache entry and re-reads the source")]
    public async Task DisableDiskCacheOverrideSkipsExistingEntry()
    {
        var folder = _root.Dir("disable-read-source");
        var previewPath = Path.Combine(folder, "preview.png");
        File.WriteAllBytes(previewPath, PreviewImageServiceTests.PreviewPng);
        var diskDirectory = _root.Dir("disable-read-cache");

        // Populate a real, valid disk-cache entry at the exact path a matching key would use,
        // via a normal (disk cache enabled) service -- this is the same fixture pattern
        // PreviewImageServiceDiskCacheTests uses for "DiskCacheHitAvoidsSourceRead".
        var writer = Track(new PreviewImageService(new ReviewMetrics(), () => false, () => 256,
            diskCacheDirectory: diskDirectory), diskDirectory);
        await writer.GetPreviewAsync(previewPath);
        await writer.ShutdownPersistWorkersAsync();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        string[] seededFiles;
        do
        {
            seededFiles = Directory.GetFiles(diskDirectory, "*.png");
            if (seededFiles.Length > 0) break;
            await Task.Delay(25);
        } while (DateTime.UtcNow < deadline);
        Assert.True(seededFiles.Length == 1, "Expected the writer service to have persisted exactly one disk cache entry.");

        // A fresh service (its own RAM cache is empty) reads the same path/key with the disk
        // cache disabled: it must decode from source instead of the entry seeded above, even
        // though that entry is present and valid on disk.
        var readerMetrics = new ReviewMetrics();
        var reader = Track(new PreviewImageService(readerMetrics, () => false, () => 256,
            diskCacheDirectory: diskDirectory, disableDiskCacheOverride: true), diskDirectory);
        var image = await reader.GetPreviewAsync(previewPath);
        var snapshot = readerMetrics.Snapshot();

        Assert.True(image.PixelWidth > 0);
        Assert.Equal(1, snapshot.SourceReads);
        Assert.Equal(0, snapshot.DiskCacheHits);

        // And it must not have deleted or overwritten the seeded entry (D10's "không xóa file
        // cache có sẵn" constraint): the corrupt-entry fallback path deletes on read failure,
        // but disableDiskCacheOverride must skip that whole branch, existing file untouched.
        await reader.ShutdownPersistWorkersAsync();
        var filesAfter = Directory.GetFiles(diskDirectory, "*.png");
        Assert.Single(filesAfter);
        Assert.Equal(seededFiles[0], filesAfter[0]);
    }
}

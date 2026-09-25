using System.Collections.Concurrent;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Imaging.Preload;

namespace PhotoReview.Imaging.Tests;

/// <summary>Q-R17: whole-folder estimate from the decode box, then from measured preview sizes.</summary>
[Trait("Category", "HotPath")]
public sealed class PreloadEstimateCalibrationTests
{
    private const int ImageCount = 200;
    private static readonly DecodeBox Box = new(1000, 1000); // bound: 4 MB per image, 800 MB for the folder
    private const long Budget = 100L * 1024 * 1024;

    [Fact(DisplayName = "Box bound over budget keeps the window; small measured previews then unlock the whole folder")]
    public async Task SmallMeasuredPreviews_UnlockWholeFolder()
    {
        var target = new InMemoryTarget(Box, previewBytes: 100_000); // 200 x 100 KB x 1.25 = 25 MB
        using var scheduler = Create(target);

        await scheduler.PreloadAroundAsync(0).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(ImageCount - 1, target.PreloadedCount);
    }

    [Fact(DisplayName = "Measured previews too large for the budget keep the 32-ahead window")]
    public async Task LargeMeasuredPreviews_KeepWindow()
    {
        var target = new InMemoryTarget(Box, previewBytes: 3_000_000); // 200 x 3 MB x 1.25 = 750 MB
        using var scheduler = Create(target);

        await scheduler.PreloadAroundAsync(0).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(PreloadOrderService.ForwardLookahead, target.PreloadedCount);
    }

    [Fact(DisplayName = "No measured sizes: the box bound alone decides (window here)")]
    public async Task NoMeasurement_UsesBoxBound()
    {
        var target = new InMemoryTarget(Box, previewBytes: null);
        using var scheduler = Create(target);

        await scheduler.PreloadAroundAsync(0).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(PreloadOrderService.ForwardLookahead, target.PreloadedCount);
    }

    [Fact(DisplayName = "Box bound within budget preloads the whole folder before any measurement")]
    public async Task BoxBoundWithinBudget_WholeFolderFromStart()
    {
        var target = new InMemoryTarget(new DecodeBox(100, 100), previewBytes: null); // 200 x 40 KB = 8 MB
        using var scheduler = Create(target);

        await scheduler.PreloadAroundAsync(0).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(ImageCount - 1, target.PreloadedCount);
    }

    [Fact(DisplayName = "Whole folder stops adding far images once the cache is 90% full, the window is still preloaded")]
    public async Task WholeFolder_StopsAtCacheFillLimit()
    {
        // Estimate fits (200 x 40 KB box bound = 8 MB), but the cache really holds 1 MB per image: past
        // ~90 images each far decode would only evict a near one.
        var target = new InMemoryTarget(new DecodeBox(100, 100), previewBytes: null, cacheBytesPerImage: 1024 * 1024);
        using var scheduler = Create(target);

        await scheduler.PreloadAroundAsync(0).WaitAsync(TimeSpan.FromSeconds(10));

        var limit = (int)Math.Ceiling(Budget * PreloadScheduler.WholeFolderCacheFillLimit / (1024 * 1024));
        Assert.InRange(target.PreloadedCount, PreloadOrderService.ForwardLookahead, limit + 4); // + in-flight workers
    }

    [Fact]
    public void Estimate_BoundedBox_IsCountTimesBoxTimesFour()
    {
        Assert.Equal(200L * 1000 * 1000 * 4, RamBudgetPolicy.EstimateFolderPreviewBytes(200, Box, totalSourceBytes: 1));
    }

    [Fact]
    public void Estimate_UnboundedAxis_FallsBackToCompressedTimesTen()
    {
        Assert.Equal(5_000, RamBudgetPolicy.EstimateFolderPreviewBytes(200, new DecodeBox(1000, 0), totalSourceBytes: 500));
        Assert.Equal(5_000, RamBudgetPolicy.EstimateFolderPreviewBytes(200, DecodeBox.Unbounded, totalSourceBytes: 500));
    }

    [Fact]
    public void Estimate_Measured_AddsMarginButStaysUnderBoxBound()
    {
        Assert.Equal(200L * 125_000, RamBudgetPolicy.EstimateFolderPreviewBytes(200, Box, 1, measuredMeanPreviewBytes: 100_000));
        // 3.6 MB x 1.25 would pass the 4 MB bound: capped at the bound.
        Assert.Equal(200L * 4_000_000, RamBudgetPolicy.EstimateFolderPreviewBytes(200, Box, 1, measuredMeanPreviewBytes: 3_600_000));
        // 16-bit images measured above the 4-byte bound: the measurement wins over the bound.
        Assert.Equal(200L * 10_000_000, RamBudgetPolicy.EstimateFolderPreviewBytes(200, Box, 1, measuredMeanPreviewBytes: 8_000_000));
    }

    [Fact]
    public void Sampler_NeedsMinimumSamples_AndRestartsOnBoxChange()
    {
        var sampler = new PreviewSizeSampler();
        for (var i = 0; i < PreviewSizeSampler.MinimumSamples - 1; i++) sampler.Record(Box, 100);
        Assert.Null(sampler.MeanBytes(Box));

        sampler.Record(Box, 900);
        Assert.Equal((7 * 100 + 900) / 8.0, sampler.MeanBytes(Box));

        var other = new DecodeBox(500, 500);
        sampler.Record(other, 50);
        Assert.Null(sampler.MeanBytes(Box));
        Assert.Null(sampler.MeanBytes(other));
    }

    private static PreloadScheduler Create(InMemoryTarget target) =>
        new(target, new ReviewMetrics(), () => target.Entries, () => 1,
            new PreloadOptions(WorkerCount: 4, FullFolderThresholdBytes: Budget),
            new FakeMemoryProbe(true), ImmediateUiScheduler.Instance);

    /// <summary>Preview cache fake with no files: keys come from catalog metadata, sizes are fixed.</summary>
    private sealed class InMemoryTarget : IPreloadTarget
    {
        private readonly DecodeBox _box;
        private readonly long? _previewBytes;
        private readonly Dictionary<string, CatalogEntry> _byPath = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, byte> _cached = new(StringComparer.OrdinalIgnoreCase);
        private int _preloaded;

        private readonly long _cacheBytesPerImage;

        public InMemoryTarget(DecodeBox box, long? previewBytes, long cacheBytesPerImage = 0)
        {
            _cacheBytesPerImage = cacheBytesPerImage;
            _box = box;
            _previewBytes = previewBytes;
            var written = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            Entries = Enumerable.Range(0, ImageCount)
                .Select(i => new CatalogEntry($@"C:\qr17-fake\img-{i:D3}.jpg").WithMetadata(1000 + i, written))
                .ToArray();
            foreach (var entry in Entries) _byPath[entry.Path] = entry;
        }

        public CatalogEntry[] Entries { get; }
        public int PreloadedCount => Volatile.Read(ref _preloaded);
        public int CacheCount => _cached.Count;
        public long CacheBytes => _cached.Count * _cacheBytesPerImage;

        public bool TryGetCachedPreview(string path) => _cached.ContainsKey(path);
        public bool TryGetCachedPreview(ImageCacheKey key) => _cached.ContainsKey(key.Path);
        public ImageCacheKey GetCurrentCacheKey(string path) => GetCurrentCacheKey(_byPath[path]);
        public ImageCacheKey GetCurrentCacheKey(CatalogEntry entry) => ImageCacheKey.Create(entry, false, _box);
        public long? CachedPreviewBytes(ImageCacheKey key) => _cached.ContainsKey(key.Path) ? _previewBytes : null;

        public Task PreloadAsync(string path, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _preloaded);
            _cached.TryAdd(GetCurrentCacheKey(path).Path, 0);
            return Task.CompletedTask;
        }
    }
}

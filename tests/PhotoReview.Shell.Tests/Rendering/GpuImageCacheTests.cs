using PhotoReview.Core.Abstractions;
using PhotoReview.Imaging.Pixels;
using PhotoReview.Shell.Rendering;

namespace PhotoReview.Shell.Tests.Rendering;

/// <summary>
/// WP-15 (C-10): logic của <see cref="GpuImageCache"/> (LRU theo byte, ngân sách, Retain, ảnh hiện tại không bị evict,
/// prefetch theo dải ở Background, mất thiết bị) trên backend giả - không cần GPU. Phần D2D thật: GpuImageCacheNativeTests.
/// </summary>
public sealed class GpuImageCacheTests : IDisposable
{
    private readonly List<PixelBuffer> _buffers = [];
    private readonly FakeBackend _backend = new();
    private readonly ManualDispatcher _dispatcher = new();

    public void Dispose()
    {
        foreach (PixelBuffer buffer in _buffers)
        {
            buffer.Dispose();
        }
    }

    private PixelBuffer Image(int width = 10, int height = 10)
    {
        var buffer = PixelBuffer.Allocate(width, height, PixelLayout.Pbgra32);
        _buffers.Add(buffer);
        return buffer;
    }

    private GpuImageCache Cache(long budget, int stripeBytes = 40) => new(_backend, _dispatcher, budget, stripeBytes);

    [Fact]
    public void TiledGpuImage_IsDrawable_OnlyForItsOwnDeviceGenerationAndWhenCompleteAndNotReleased()
    {
        PixelBuffer px = Image(4, 4);
        var image = new TiledGpuImage(px, [], generation: 3) { UploadedRows = 4 };

        Assert.True(image.IsDrawable(3));
        Assert.False(image.IsDrawable(4));   // thiết bị đã được tạo lại: bitmap thuộc thế hệ cũ không được vẽ

        image.UploadedRows = 2;
        Assert.False(image.IsDrawable(3));   // prefetch dở

        image.UploadedRows = 4;
        image.IsReleased = true;
        Assert.False(image.IsDrawable(3));
    }

    [Fact]
    public void PixelBuffer_HasNoRowPadding_SoCacheMayAssumeStrideIsWidthTimesFour()
    {
        // GpuImageCache/D2DGpuImageBackend chép theo Stride của PixelBuffer; nếu PixelBuffer sau này có đệm hàng,
        // test này đỏ để buộc rà lại các phép tính offset (đột biến M21/M22 của WP-15 tương đương tới lúc đó).
        PixelBuffer px = Image(7, 3);

        Assert.Equal(7 * 4, px.Stride);
    }

    [Fact]
    public void GetOrUpload_SameBuffer_UploadsOnceAndReturnsSameImage()
    {
        using GpuImageCache cache = Cache(budget: 10_000);
        PixelBuffer px = Image();

        IGpuImage first = cache.GetOrUpload(px);
        IGpuImage second = cache.GetOrUpload(px);

        Assert.Same(first, second);
        Assert.Equal(1, _backend.UploadCount);
        Assert.Equal((px.Id, 10, 10, 400L), (first.PixelBufferId, first.PixelWidth, first.PixelHeight, first.GpuBytes));
        Assert.Equal(400, cache.TotalGpuBytes);
        Assert.True(_backend.Uploads[0].WithData);
    }

    [Fact]
    public void GetOrUpload_OverBudget_EvictsLeastRecentlyUsed()
    {
        using GpuImageCache cache = Cache(budget: 1_000);   // 2 ảnh 400 B
        PixelBuffer a = Image(), b = Image(), c = Image();

        cache.GetOrUpload(a);
        cache.GetOrUpload(b);
        cache.GetOrUpload(a);   // a mới dùng lại -> b là cũ nhất
        cache.GetOrUpload(c);

        Assert.True(cache.Contains(a.Id));
        Assert.False(cache.Contains(b.Id));
        Assert.True(cache.Contains(c.Id));
        Assert.Equal(800, cache.TotalGpuBytes);
        Assert.Equal([b.Id], _backend.Released);
    }

    [Fact]
    public void GetOrUpload_ImageLargerThanBudget_IsKeptAsCurrent()
    {
        using GpuImageCache cache = Cache(budget: 100);
        PixelBuffer small = Image(2, 2);
        PixelBuffer big = Image(100, 100);

        cache.GetOrUpload(small);
        IGpuImage current = cache.GetOrUpload(big);

        Assert.True(cache.Contains(big.Id));
        Assert.False(cache.Contains(small.Id));
        Assert.Equal(current.GpuBytes, cache.TotalGpuBytes);
        Assert.False(((TiledGpuImage)current).IsReleased);
    }

    [Fact]
    public void Prefetch_DoesNotEvictCurrentImage()
    {
        using GpuImageCache cache = Cache(budget: 500);
        PixelBuffer current = Image(), next = Image();

        cache.GetOrUpload(current);
        cache.Prefetch(next);

        Assert.True(cache.Contains(current.Id));
        Assert.True(cache.Contains(next.Id));
        Assert.Empty(_backend.Released);
    }

    [Fact]
    public void Retain_PinsIdsBeyondBudget_AndUnpinningEvicts()
    {
        using GpuImageCache cache = Cache(budget: 10_000);
        PixelBuffer a = Image(), b = Image(), c = Image();
        cache.GetOrUpload(a);
        cache.GetOrUpload(b);
        cache.GetOrUpload(c);

        cache.Retain([a.Id, b.Id]);
        cache.BudgetBytes = 0;

        Assert.True(cache.Contains(a.Id));
        Assert.True(cache.Contains(b.Id));
        Assert.True(cache.Contains(c.Id)); // c = ảnh của GetOrUpload gần nhất

        cache.Retain([b.Id]);
        Assert.False(cache.Contains(a.Id));
        Assert.True(cache.Contains(b.Id));
        Assert.Equal(800, cache.TotalGpuBytes);
    }

    [Fact]
    public void BudgetBytes_Lowered_EvictsImmediately_NegativeRejected()
    {
        using GpuImageCache cache = Cache(budget: 10_000);
        PixelBuffer a = Image(), b = Image(), c = Image();
        cache.GetOrUpload(a);
        cache.GetOrUpload(b);
        cache.GetOrUpload(c);

        cache.BudgetBytes = 800;

        Assert.Equal(800, cache.TotalGpuBytes);
        Assert.False(cache.Contains(a.Id));
        Assert.Equal(800, cache.BudgetBytes);
        Assert.Throws<ArgumentOutOfRangeException>(() => cache.BudgetBytes = -1);
    }

    [Fact]
    public void Prefetch_UploadsInBackgroundStripes_NotSynchronously()
    {
        using GpuImageCache cache = Cache(budget: 10_000, stripeBytes: 80);   // stride 40 -> 2 dòng / dải
        PixelBuffer px = Image(10, 9);

        cache.Prefetch(px);

        Assert.False(_backend.Uploads[0].WithData);
        Assert.Empty(_backend.RowUploads);
        Assert.Equal(1, cache.PendingPrefetchCount);
        Assert.Equal(1, _dispatcher.PendingCount);

        int steps = _dispatcher.RunAll();

        Assert.Equal(5, steps);   // 2+2+2+2+1 dòng
        Assert.Equal([(0, 2), (2, 4), (4, 6), (6, 8), (8, 9)], _backend.RowUploads);
        Assert.All(_dispatcher.PostedPriorities, p => Assert.Equal(UiPriority.Background, p));
        Assert.Equal(0, cache.PendingPrefetchCount);

        IGpuImage image = cache.GetOrUpload(px);
        Assert.True(((TiledGpuImage)image).IsComplete);
        Assert.Equal(1, _backend.UploadCount);
    }

    [Fact]
    public void GetOrUpload_DuringPrefetch_UploadsRemainingRowsAtOnce()
    {
        using GpuImageCache cache = Cache(budget: 10_000, stripeBytes: 80);
        PixelBuffer px = Image(10, 9);
        cache.Prefetch(px);
        _dispatcher.RunOne();   // dòng 0..2

        IGpuImage image = cache.GetOrUpload(px);

        Assert.Equal([(0, 2), (2, 9)], _backend.RowUploads);
        Assert.True(((TiledGpuImage)image).IsComplete);
        Assert.Equal(1, _dispatcher.RunAll());   // việc nền còn xếp hàng chạy và không upload thêm
        Assert.Equal(2, _backend.RowUploads.Count);
    }

    [Fact]
    public void Prefetch_AlreadyCached_IsNoOp()
    {
        using GpuImageCache cache = Cache(budget: 10_000);
        PixelBuffer px = Image();
        cache.GetOrUpload(px);

        cache.Prefetch(px);
        cache.Prefetch(px);

        Assert.Equal(1, _backend.UploadCount);
        Assert.Equal(0, _dispatcher.PendingCount);
    }

    [Fact]
    public void Prefetch_SourceDisposedMidway_DropsPartialImage()
    {
        using GpuImageCache cache = Cache(budget: 10_000, stripeBytes: 80);
        var px = PixelBuffer.Allocate(10, 9, PixelLayout.Pbgra32);
        long id = px.Id;
        cache.Prefetch(px);
        _dispatcher.RunOne();

        px.Dispose();
        _dispatcher.RunAll();

        Assert.False(cache.Contains(id));
        Assert.Equal(0, cache.TotalGpuBytes);
        Assert.Equal([id], _backend.Released);
        Assert.Single(_backend.RowUploads);
    }

    [Fact]
    public void Prefetch_EvictedBeforeStripesRun_StopsUploading()
    {
        using GpuImageCache cache = Cache(budget: 10_000, stripeBytes: 80);
        PixelBuffer px = Image(10, 9);
        cache.Prefetch(px);

        cache.Clear();
        _dispatcher.RunAll();

        Assert.Empty(_backend.RowUploads);
        Assert.Equal([px.Id], _backend.Released);
    }

    [Fact]
    public void DeviceRecreated_ReleasesEverythingAndNextGetUploadsAgain()
    {
        using GpuImageCache cache = Cache(budget: 10_000);
        PixelBuffer a = Image(), b = Image();
        IGpuImage before = cache.GetOrUpload(a);
        cache.GetOrUpload(b);

        _backend.RaiseDeviceRecreated();

        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.TotalGpuBytes);
        Assert.True(((TiledGpuImage)before).IsReleased);
        Assert.Equal(2, _backend.Released.Count);

        IGpuImage after = cache.GetOrUpload(a);
        Assert.NotSame(before, after);
        Assert.Equal(3, _backend.UploadCount);
    }

    [Fact]
    public void Dispose_ReleasesAllAndUnsubscribes()
    {
        GpuImageCache cache = Cache(budget: 10_000);
        PixelBuffer a = Image();
        cache.GetOrUpload(a);

        cache.Dispose();
        cache.Dispose();

        Assert.Equal([a.Id], _backend.Released);
        Assert.False(_backend.HasSubscribers);
        Assert.Throws<ObjectDisposedException>(() => cache.GetOrUpload(a));
    }

    [Fact]
    public void GetOrUpload_DisposedBuffer_Throws()
    {
        using GpuImageCache cache = Cache(budget: 10_000);
        var px = PixelBuffer.Allocate(2, 2, PixelLayout.Pbgra32);
        px.Dispose();

        Assert.Throws<ObjectDisposedException>(() => cache.GetOrUpload(px));
        cache.Prefetch(px);
        Assert.Equal(0, _backend.UploadCount);
    }

    [Fact]
    public void ThousandUploads_NeverExceedBudgetBeyondCurrent_AndReleaseEverything()
    {
        using GpuImageCache cache = Cache(budget: 4_000);   // 10 ảnh 400 B
        for (int i = 0; i < 1000; i++)
        {
            using PixelBuffer px = PixelBuffer.Allocate(10, 10, PixelLayout.Pbgra32);
            if (i % 3 == 0)
            {
                cache.Prefetch(px);
                _dispatcher.RunAll();
            }

            cache.GetOrUpload(px);
            Assert.True(cache.TotalGpuBytes <= cache.BudgetBytes, $"{cache.TotalGpuBytes} > budget at {i}");
        }

        Assert.Equal(10, cache.Count);
        cache.Clear();
        Assert.Equal(_backend.UploadCount, _backend.Released.Count);
        Assert.Equal(0, cache.TotalGpuBytes);
    }

    private sealed class FakeBackend : IGpuImageBackend
    {
        private EventHandler? _deviceRecreated;

        public event EventHandler? DeviceRecreated
        {
            add => _deviceRecreated += value;
            remove => _deviceRecreated -= value;
        }

        public bool HasSubscribers => _deviceRecreated is not null;

        public List<(long Id, bool WithData)> Uploads { get; } = [];

        public int UploadCount => Uploads.Count;

        public List<(int Start, int End)> RowUploads { get; } = [];

        public List<long> Released { get; } = [];

        public long Generation { get; private set; }

        public TiledGpuImage Upload(PixelBuffer pixels, bool withData)
        {
            Uploads.Add((pixels.Id, withData));
            GpuTile[] tiles = TileLayout.Compute(pixels.Width, pixels.Height, 16384, TileLayout.DefaultGutter)
                .Select(spec => new GpuTile(spec, null)).ToArray();
            return new TiledGpuImage(pixels, tiles, Generation);
        }

        public void UploadRows(TiledGpuImage image, PixelBuffer pixels, int startRow, int endRow) => RowUploads.Add((startRow, endRow));

        public void Release(TiledGpuImage image) => Released.Add(image.PixelBufferId);

        public void RaiseDeviceRecreated()
        {
            Generation++;
            _deviceRecreated?.Invoke(this, EventArgs.Empty);
        }
    }
}

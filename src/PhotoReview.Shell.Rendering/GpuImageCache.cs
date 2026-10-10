using PhotoReview.Core.Abstractions;
using PhotoReview.Imaging.Pixels;
using PhotoReview.Shell.Interop.Graphics;

namespace PhotoReview.Shell.Rendering;

/// <summary>
/// WP-15 (C-10): cache ảnh GPU khoá theo <see cref="PixelBuffer.Id"/>, LRU theo byte tới <see cref="BudgetBytes"/>.
/// <list type="bullet">
/// <item><see cref="GetOrUpload"/>: đồng bộ trên UI thread, một lần copy CPU-&gt;GPU (CreateBitmap từ con trỏ); nếu ảnh đang
/// prefetch dở thì upload nốt phần còn lại.</item>
/// <item><see cref="Prefetch"/>: tạo texture rỗng rồi upload từng DẢI (~<see cref="DefaultStripeBytes"/>) bằng các việc
/// <see cref="UiPriority.Background"/> nối tiếp: UI thread không bị chặn quá một dải, input/khung vẽ chen vào giữa các dải.</item>
/// <item>Ảnh vừa dùng (<see cref="GetOrUpload"/>/<see cref="Prefetch"/> gần nhất) và các id của <see cref="Retain"/> không bị
/// evict kể cả khi vượt ngân sách (ảnh hiện tại 24 MP lớn hơn ngân sách mặc định vẫn phải vẽ được).</item>
/// <item>Mất thiết bị (<see cref="IRenderSurface.DeviceRecreated"/>): giải phóng hết, cache rỗng; lần vẽ kế tiếp upload lại
/// từ PixelBuffer người gọi vẫn giữ.</item>
/// </list>
/// Không thread-safe: mọi lời gọi trên UI thread (như surface). Người gọi không được Dispose một PixelBuffer khi nó đang
/// được prefetch (cùng quy tắc sở hữu của C-01: cache/presenter giữ tham chiếu managed, chỉ chủ duy nhất Dispose).
/// </summary>
public sealed class GpuImageCache : IGpuImageCache, IDisposable
{
    /// <summary>Kích thước một dải upload của <see cref="Prefetch"/> (2 MB ~ 0,3-1 ms CPU mỗi việc nền).</summary>
    public const int DefaultStripeBytes = 2 * 1024 * 1024;

    /// <summary>Số "ảnh cỡ màn hình" của ngân sách mặc định (C-10).</summary>
    public const int DefaultBudgetScreens = 6;

    private const long MinimumScreenBytes = 1920L * 1080 * 4;

    private readonly IGpuImageBackend _backend;
    private readonly IUiDispatcher _dispatcher;
    private readonly int _stripeBytes;
    private readonly Dictionary<long, Entry> _entries = [];
    private readonly LinkedList<Entry> _lru = new(); // đầu = cũ nhất
    private HashSet<long> _retained = [];
    private long _budgetBytes;
    private long _protectedId = -1;
    private bool _disposed;

    /// <summary>Cache trên <paramref name="surface"/>; prefetch chạy qua <paramref name="dispatcher"/> (UiPriority.Background).</summary>
    public GpuImageCache(D2DRenderSurface surface, IUiDispatcher dispatcher)
        : this(new D2DGpuImageBackend(surface), dispatcher,
            DefaultBudgetScreens * Math.Max(MinimumScreenBytes, (long)surface.PixelWidth * surface.PixelHeight * 4),
            DefaultStripeBytes)
    {
    }

    internal GpuImageCache(IGpuImageBackend backend, IUiDispatcher dispatcher, long budgetBytes, int stripeBytes)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentOutOfRangeException.ThrowIfNegative(budgetBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(stripeBytes, 4);
        _backend = backend;
        _dispatcher = dispatcher;
        _budgetBytes = budgetBytes;
        _stripeBytes = stripeBytes;
        _backend.DeviceRecreated += OnDeviceRecreated;
    }

    public long BudgetBytes
    {
        get => _budgetBytes;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _budgetBytes = value;
            EvictOverBudget();
        }
    }

    /// <summary>Tổng <see cref="IGpuImage.GpuBytes"/> của các ảnh đang giữ (kể cả ảnh đang prefetch dở).</summary>
    public long TotalGpuBytes { get; private set; }

    public int Count => _entries.Count;

    /// <summary>Số ảnh đang prefetch dở (còn dải chưa upload).</summary>
    public int PendingPrefetchCount
    {
        get
        {
            int pending = 0;
            foreach (Entry entry in _entries.Values)
            {
                if (!entry.Image.IsComplete)
                {
                    pending++;
                }
            }

            return pending;
        }
    }

    public bool Contains(long pixelBufferId) => _entries.ContainsKey(pixelBufferId);

    public IGpuImage GetOrUpload(PixelBuffer pixels)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        ObjectDisposedException.ThrowIf(_disposed, this);
        ObjectDisposedException.ThrowIf(pixels.IsDisposed, pixels);

        if (_entries.TryGetValue(pixels.Id, out Entry? entry))
        {
            if (!entry.Image.IsComplete)
            {
                UploadRows(entry, pixels, entry.Image.PixelHeight);
            }

            Touch(entry);
            _protectedId = pixels.Id;
            return entry.Image;
        }

        TiledGpuImage image = _backend.Upload(pixels, withData: true);
        image.UploadedRows = image.PixelHeight;
        _protectedId = pixels.Id;
        entry = Add(image);
        return entry.Image;
    }

    public void Prefetch(PixelBuffer pixels)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (pixels.IsDisposed)
        {
            return;
        }

        if (_entries.TryGetValue(pixels.Id, out Entry? existing))
        {
            Touch(existing);
            return;
        }

        TiledGpuImage image = _backend.Upload(pixels, withData: false);
        Entry entry = Add(image);
        entry.PendingSource = pixels;
        ScheduleStripe(entry);
    }

    public void Retain(IReadOnlyCollection<long> pixelBufferIds)
    {
        ArgumentNullException.ThrowIfNull(pixelBufferIds);
        ObjectDisposedException.ThrowIf(_disposed, this);
        _retained = [.. pixelBufferIds];
        EvictOverBudget();
    }

    public void Clear()
    {
        foreach (Entry entry in _entries.Values)
        {
            ReleaseImage(entry.Image);
        }

        _entries.Clear();
        _lru.Clear();
        TotalGpuBytes = 0;
        _protectedId = -1;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _backend.DeviceRecreated -= OnDeviceRecreated;
        Clear();
    }

    private Entry Add(TiledGpuImage image)
    {
        var entry = new Entry(image);
        entry.Node = _lru.AddLast(entry);
        _entries.Add(image.PixelBufferId, entry);
        TotalGpuBytes += image.GpuBytes;
        EvictOverBudget(image.PixelBufferId);
        return entry;
    }

    private void Touch(Entry entry)
    {
        _lru.Remove(entry.Node!);
        _lru.AddLast(entry.Node!);
    }

    /// <summary>Evict từ cũ nhất tới khi &lt;= ngân sách; không đụng ảnh của GetOrUpload gần nhất, <paramref name="alsoKeep"/>, Retain.</summary>
    private void EvictOverBudget(long alsoKeep = -1)
    {
        LinkedListNode<Entry>? node = _lru.First;
        while (TotalGpuBytes > _budgetBytes && node is not null)
        {
            LinkedListNode<Entry>? next = node.Next;
            long id = node.Value.Image.PixelBufferId;
            if (id != _protectedId && id != alsoKeep && !_retained.Contains(id))
            {
                Remove(node.Value);
            }

            node = next;
        }
    }

    private void Remove(Entry entry)
    {
        _lru.Remove(entry.Node!);
        _entries.Remove(entry.Image.PixelBufferId);
        TotalGpuBytes -= entry.Image.GpuBytes;
        entry.PendingSource = null;
        ReleaseImage(entry.Image);
    }

    private void ReleaseImage(TiledGpuImage image)
    {
        if (image.IsReleased)
        {
            return;
        }

        image.IsReleased = true;
        _backend.Release(image);
    }

    private void ScheduleStripe(Entry entry) => _dispatcher.Post(() => RunStripe(entry), UiPriority.Background);

    private void RunStripe(Entry entry)
    {
        PixelBuffer? source = entry.PendingSource;
        if (_disposed || entry.Image.IsReleased || source is null || entry.Image.IsComplete)
        {
            return;
        }

        if (source.IsDisposed)
        {
            // Chủ sở hữu đã bỏ ảnh: không còn gì để upload, ảnh dở dang không được giữ.
            Remove(entry);
            return;
        }

        int rowsPerStripe = Math.Max(1, _stripeBytes / source.Stride);
        UploadRows(entry, source, entry.Image.UploadedRows + rowsPerStripe);
        if (!entry.Image.IsComplete)
        {
            ScheduleStripe(entry);
        }
    }

    private void UploadRows(Entry entry, PixelBuffer pixels, int endRow)
    {
        TiledGpuImage image = entry.Image;
        int start = image.UploadedRows;
        int end = Math.Min(endRow, image.PixelHeight);
        if (end > start)
        {
            _backend.UploadRows(image, pixels, start, end);
            image.UploadedRows = end;
        }

        if (image.IsComplete)
        {
            entry.PendingSource = null;
        }
    }

    private void OnDeviceRecreated(object? sender, EventArgs e) => Clear();

    private sealed class Entry(TiledGpuImage image)
    {
        public TiledGpuImage Image { get; } = image;

        public LinkedListNode<Entry>? Node { get; set; }

        public PixelBuffer? PendingSource { get; set; }
    }
}

/// <summary>Seam giữa logic cache (LRU, ngân sách, prefetch theo dải) và D2D; unit test dùng backend giả không cần GPU.</summary>
internal interface IGpuImageBackend
{
    event EventHandler? DeviceRecreated;

    /// <summary>Tạo các tile của ảnh; <paramref name="withData"/> = false để texture rỗng (prefetch ghi sau bằng <see cref="UploadRows"/>).</summary>
    TiledGpuImage Upload(PixelBuffer pixels, bool withData);

    /// <summary>Ghi dòng [<paramref name="startRow"/>, <paramref name="endRow"/>) của ảnh vào mọi tile cắt qua chúng.</summary>
    void UploadRows(TiledGpuImage image, PixelBuffer pixels, int startRow, int endRow);

    void Release(TiledGpuImage image);
}

/// <summary>Backend D2D thật: tile = <c>ID2D1Bitmap1</c> trên device context của surface.</summary>
internal sealed unsafe class D2DGpuImageBackend : IGpuImageBackend
{
    private readonly D2DRenderSurface _surface;

    public D2DGpuImageBackend(D2DRenderSurface surface, int? maxTileSize = null, int gutter = TileLayout.DefaultGutter)
    {
        ArgumentNullException.ThrowIfNull(surface);
        _surface = surface;
        MaxTileSize = maxTileSize;
        Gutter = gutter;
    }

    public event EventHandler? DeviceRecreated
    {
        add => _surface.DeviceRecreated += value;
        remove => _surface.DeviceRecreated -= value;
    }

    /// <summary>Giới hạn tile ép nhỏ hơn giới hạn thiết bị (test đường nối không cần ảnh 16384+).</summary>
    public int? MaxTileSize { get; }

    public int Gutter { get; }

    public TiledGpuImage Upload(PixelBuffer pixels, bool withData)
    {
        DeviceResources resources = _surface.Resources;
        // WARP báo GetMaximumBitmapSize rất lớn (8388608 trên máy này) nhưng GPU thật FL 11 = 16384: luôn kẹp về giới hạn
        // D3D11 để mọi thiết bị chia tile giống nhau (và test WARP kiểm đúng đường tile của GPU thật).
        int max = Math.Min(Math.Min(resources.MaxBitmapSize, TileLayout.D3D11MaxTextureDimension), MaxTileSize ?? int.MaxValue);
        TileSpec[] specs = TileLayout.Compute(pixels.Width, pixels.Height, max, Gutter);
        var tiles = new GpuTile[specs.Length];
        D2dAlphaMode alpha = pixels.Layout == PixelLayout.Bgr32 ? D2dAlphaMode.Ignore : D2dAlphaMode.Premultiplied;
        try
        {
            for (int i = 0; i < specs.Length; i++)
            {
                PixelRect texture = specs[i].Texture;
                byte* data = withData ? TileOrigin(pixels, texture) : null;
                tiles[i] = new GpuTile(specs[i], resources.CreateBitmap(texture.Width, texture.Height, data,
                    withData ? (uint)pixels.Stride : 0, alpha, D2dBitmapOptions.None));
            }
        }
        catch
        {
            foreach (GpuTile? tile in tiles)
            {
                resources.ReleaseBitmap(tile?.Bitmap);
            }

            throw;
        }
        finally
        {
            GC.KeepAlive(pixels);
        }

        return new TiledGpuImage(pixels, tiles, resources.Generation);
    }

    public void UploadRows(TiledGpuImage image, PixelBuffer pixels, int startRow, int endRow)
    {
        foreach (GpuTile tile in image.Tiles)
        {
            PixelRect texture = tile.Spec.Texture;
            int top = Math.Max(startRow, texture.Y);
            int bottom = Math.Min(endRow, texture.Bottom);
            if (bottom <= top || tile.Bitmap is null)
            {
                continue;
            }

            D2dRectU dest = new()
            {
                Left = 0,
                Top = (uint)(top - texture.Y),
                Right = (uint)texture.Width,
                Bottom = (uint)(bottom - texture.Y),
            };
            byte* source = (byte*)pixels.Address + ((long)top * pixels.Stride) + ((long)texture.X * 4);
            ComInterop.Check(tile.Bitmap.CopyFromMemory(&dest, source, (uint)pixels.Stride));
        }

        GC.KeepAlive(pixels);
    }

    public void Release(TiledGpuImage image)
    {
        DeviceResources resources = _surface.Resources;
        foreach (GpuTile tile in image.Tiles)
        {
            resources.ReleaseBitmap(tile.Bitmap);
            tile.Bitmap = null;
        }
    }

    private static byte* TileOrigin(PixelBuffer pixels, PixelRect texture) =>
        (byte*)pixels.Address + ((long)texture.Y * pixels.Stride) + ((long)texture.X * 4);
}

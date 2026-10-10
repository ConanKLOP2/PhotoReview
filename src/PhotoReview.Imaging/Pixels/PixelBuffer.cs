namespace PhotoReview.Imaging.Pixels;

// C-01 (NO-WPF-EXEC-PLAN mục 5, đóng băng ở WP-01). Thực thi ở WP-02 (NOWPF-WP02-PIXELBUFFER).

/// <summary>4 byte/pixel, thứ tự byte B,G,R,X|A (khớp GUID_WICPixelFormat32bppBGR / 32bppPBGRA và DXGI_FORMAT_B8G8R8A8_UNORM).</summary>
public enum PixelLayout
{
    Bgr32 = 0,
    Pbgra32 = 1,
}

/// <summary>
/// Bộ nhớ native (NativeMemory.AlignedAlloc, căn 64 byte) + GC.AddMemoryPressure(ByteCount); giải phóng ở Dispose
/// hoặc finalizer (SafeHandle bên trong). KHÔNG đếm tham chiếu: cache/presenter giữ tham chiếu managed như với
/// BitmapSource hôm nay; chỉ chủ sở hữu duy nhất (bộ đệm tạm của decoder, PixelLease có Owned=true) được Dispose.
/// <para>Nội dung sau <see cref="Allocate"/> CHƯA khởi tạo (decoder ghi đè toàn bộ; không trả phí xoá 0).</para>
/// <para>Không thread-safe với Dispose: gọi Dispose khi luồng khác còn giữ <see cref="Span{T}"/> của <see cref="GetRow"/>
/// là lỗi của người gọi (như mọi bộ đệm native). Người gọi giữ tham chiếu tới buffer (hoặc <see cref="GC.KeepAlive"/>)
/// trong lúc dùng span/<see cref="Address"/>, để finalizer không giải phóng giữa chừng.</para>
/// </summary>
public sealed class PixelBuffer : IDisposable
{
    private const int BytesPerPixel = 4;

    private static long _lastId;

    private readonly NativePixelMemory _memory;

    // Test dựng buffer "giả" (khối chưa cấp phát, kích thước tuỳ ý) qua UnsafeAccessor để thử nhánh > 2 GB mà không cấp phát.
    private PixelBuffer(int width, int height, PixelLayout layout, NativePixelMemory memory)
    {
        Width = width;
        Height = height;
        Layout = layout;
        Stride = width * BytesPerPixel;
        ByteCount = (long)Stride * height;
        _memory = memory;
        Id = Interlocked.Increment(ref _lastId);
    }

    /// <summary>width, height &gt;= 1; ném OutOfMemoryException/ArgumentOutOfRangeException.</summary>
    public static PixelBuffer Allocate(int width, int height, PixelLayout layout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        // Stride là int: Width * 4 không được tràn.
        ArgumentOutOfRangeException.ThrowIfGreaterThan(width, int.MaxValue / BytesPerPixel);
        if (layout is not (PixelLayout.Bgr32 or PixelLayout.Pbgra32))
            throw new ArgumentOutOfRangeException(nameof(layout), layout, "Unknown pixel layout.");

        var memory = NativePixelMemory.Allocate((long)width * BytesPerPixel * height);
        return new PixelBuffer(width, height, layout, memory);
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>= Width * 4, không padding.</summary>
    public int Stride { get; }

    public PixelLayout Layout { get; }

    /// <summary>= (long)Stride * Height.</summary>
    public long ByteCount { get; }

    /// <summary>Duy nhất trong tiến trình, tăng dần (khoá GpuImageCache).</summary>
    public long Id { get; }

    public bool IsDisposed => _memory.IsClosed;

    /// <summary>Ném ObjectDisposedException nếu đã Dispose.</summary>
    public nint Address
    {
        get
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            return _memory.DangerousGetHandle();
        }
    }

    /// <summary>0 &lt;= y &lt; Height.</summary>
    public unsafe Span<byte> GetRow(int y)
    {
        var address = Address;
        ArgumentOutOfRangeException.ThrowIfNegative(y);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(y, Height);
        return new Span<byte>((byte*)address + ((long)y * Stride), Stride);
    }

    /// <summary>False khi ByteCount &gt; int.MaxValue.</summary>
    public unsafe bool TryGetSpan(out Span<byte> span)
    {
        var address = Address;
        if (ByteCount > int.MaxValue)
        {
            span = default;
            return false;
        }

        span = new Span<byte>((byte*)address, (int)ByteCount);
        return true;
    }

    /// <summary>Giải phóng bộ nhớ native; gọi nhiều lần an toàn.</summary>
    public void Dispose() => _memory.Dispose();
}

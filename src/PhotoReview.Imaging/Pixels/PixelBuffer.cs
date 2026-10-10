namespace PhotoReview.Imaging.Pixels;

// C-01 (NO-WPF-EXEC-PLAN mục 5, đóng băng ở WP-01). Thực thi ở WP-02; mọi thân hiện ném NotImplementedException.

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
/// </summary>
public sealed class PixelBuffer : IDisposable
{
    private PixelBuffer()
    {
    }

    /// <summary>width, height &gt;= 1; ném OutOfMemoryException/ArgumentOutOfRangeException.</summary>
    public static PixelBuffer Allocate(int width, int height, PixelLayout layout) => throw new NotImplementedException();

    public int Width => throw new NotImplementedException();

    public int Height => throw new NotImplementedException();

    /// <summary>= Width * 4, không padding.</summary>
    public int Stride => throw new NotImplementedException();

    public PixelLayout Layout => throw new NotImplementedException();

    /// <summary>= (long)Stride * Height.</summary>
    public long ByteCount => throw new NotImplementedException();

    /// <summary>Duy nhất trong tiến trình, tăng dần (khoá GpuImageCache).</summary>
    public long Id => throw new NotImplementedException();

    public bool IsDisposed => throw new NotImplementedException();

    /// <summary>Ném ObjectDisposedException nếu đã Dispose.</summary>
    public nint Address => throw new NotImplementedException();

    /// <summary>0 &lt;= y &lt; Height.</summary>
    public Span<byte> GetRow(int y) => throw new NotImplementedException();

    /// <summary>False khi ByteCount &gt; int.MaxValue.</summary>
    public bool TryGetSpan(out Span<byte> span) => throw new NotImplementedException();

    public void Dispose() => throw new NotImplementedException();
}

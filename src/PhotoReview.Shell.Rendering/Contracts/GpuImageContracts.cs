using PhotoReview.Imaging.Pixels;

namespace PhotoReview.Shell.Rendering;

// C-10 (NO-WPF-EXEC-PLAN mục 5): cache ảnh GPU khoá theo PixelBuffer.Id (không theo path, INV-1). Thực thi ở WP-15.

public interface IGpuImage
{
    long PixelBufferId { get; }

    int PixelWidth { get; }

    int PixelHeight { get; }

    long GpuBytes { get; }
}

public interface IGpuImageCache
{
    /// <summary>UI thread. Upload ID2D1Bitmap1 từ PixelBuffer (CreateBitmap từ con trỏ, một lần copy CPU-&gt;GPU).</summary>
    IGpuImage GetOrUpload(PixelBuffer pixels);

    /// <summary>Upload trước trong lúc rảnh (UiPriority.Background), bỏ qua nếu đã có.</summary>
    void Prefetch(PixelBuffer pixels);

    /// <summary>Giữ đúng các id này + LRU tới BudgetBytes.</summary>
    void Retain(IReadOnlyCollection<long> pixelBufferIds);

    /// <summary>Mặc định: 6 ảnh cỡ màn hình (không nhân đôi RAM preload lên GPU).</summary>
    long BudgetBytes { get; set; }

    void Clear();
}

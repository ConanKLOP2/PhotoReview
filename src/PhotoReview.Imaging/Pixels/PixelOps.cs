namespace PhotoReview.Imaging.Pixels;

/// <summary>
/// C-03 (NO-WPF-EXEC-PLAN mục 5): thao tác pixel managed, không WPF. Thực thi ở WP-02 (có test so với WPF).
/// Phần thuần của ExifOrientation (IsTransposed, Normalize) được khoá khi WP-03 dời các thành viên WPF ra (hợp đồng v1.1).
/// </summary>
public static class PixelOps
{
    /// <summary>Orientation EXIF 1..8 (giá trị khác = 1). Trả về src nếu 1; ngược lại buffer mới và Dispose src (nhận quyền sở hữu).</summary>
    public static PixelBuffer ApplyOrientation(PixelBuffer src, int exifOrientation) => throw new NotImplementedException();

    /// <summary>Layout == Pbgra32 &amp;&amp; !IsFullyOpaque.</summary>
    public static bool HasAlpha(PixelBuffer px) => throw new NotImplementedException();

    /// <summary>Mọi A == 255 (Bgr32 -&gt; true).</summary>
    public static bool IsFullyOpaque(PixelBuffer px) => throw new NotImplementedException();
}

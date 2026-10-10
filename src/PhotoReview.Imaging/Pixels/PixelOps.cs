using System.Numerics;
using System.Runtime.InteropServices;

namespace PhotoReview.Imaging.Pixels;

/// <summary>
/// C-03 (NO-WPF-EXEC-PLAN mục 5): thao tác pixel managed, không WPF. Thực thi ở WP-02; test so từng byte với
/// <c>ExifOrientation.Apply</c> (WPF TransformedBitmap) trên 8 orientation.
/// Phần thuần của ExifOrientation (IsTransposed, Normalize) được khoá khi WP-03 dời các thành viên WPF ra (hợp đồng v1.1).
/// </summary>
public static class PixelOps
{
    // Ô vuông cho phép chuyển vị (orientation 5..8): 32 x 32 pixel x 4 byte = 4 KB nguồn + 4 KB đích, nằm gọn trong L1.
    private const int TransposeTile = 32;

    /// <summary>Orientation EXIF 1..8 (giá trị khác = 1). Trả về src nếu 1; ngược lại buffer mới và Dispose src (nhận quyền sở hữu).</summary>
    /// <remarks>Nếu ném (src đã Dispose, hết bộ nhớ khi cấp buffer đích) thì src KHÔNG bị Dispose: người gọi vẫn sở hữu nó.</remarks>
    public static PixelBuffer ApplyOrientation(PixelBuffer src, int exifOrientation)
    {
        ArgumentNullException.ThrowIfNull(src);
        ObjectDisposedException.ThrowIf(src.IsDisposed, src);
        if (exifOrientation is < 2 or > 8) return src;

        var transposed = exifOrientation >= 5;
        var dst = transposed
            ? PixelBuffer.Allocate(src.Height, src.Width, src.Layout)
            : PixelBuffer.Allocate(src.Width, src.Height, src.Layout);
        try
        {
            if (transposed) CopyTransposed(src, dst, exifOrientation);
            else CopyRows(src, dst, exifOrientation);
        }
        catch
        {
            dst.Dispose();
            throw;
        }

        src.Dispose();
        return dst;
    }

    /// <summary>Layout == Pbgra32 &amp;&amp; !IsFullyOpaque.</summary>
    public static bool HasAlpha(PixelBuffer px)
    {
        ArgumentNullException.ThrowIfNull(px);
        ObjectDisposedException.ThrowIf(px.IsDisposed, px);
        return px.Layout == PixelLayout.Pbgra32 && !IsFullyOpaque(px);
    }

    /// <summary>Mọi A == 255 (Bgr32 -&gt; true).</summary>
    /// <remarks>Quét từng dòng, vector hoá, dừng ở pixel đầu tiên có A &lt; 255 (như <c>PreviewCacheFile.IsFullyOpaque</c>).</remarks>
    public static bool IsFullyOpaque(PixelBuffer px)
    {
        ArgumentNullException.ThrowIfNull(px);
        ObjectDisposedException.ThrowIf(px.IsDisposed, px);
        if (px.Layout != PixelLayout.Pbgra32) return true;

        for (var y = 0; y < px.Height; y++)
        {
            if (!RowAlphaOpaque(MemoryMarshal.Cast<byte, uint>(px.GetRow(y)))) return false;
        }

        return true;
    }

    private static bool RowAlphaOpaque(ReadOnlySpan<uint> row)
    {
        const uint AlphaMask = 0xFF000000u; // little-endian: byte 3 (A) là byte cao
        var i = 0;
        if (Vector.IsHardwareAccelerated && row.Length >= Vector<uint>.Count)
        {
            var mask = new Vector<uint>(AlphaMask);
            var last = row.Length - Vector<uint>.Count;
            for (; i <= last; i += Vector<uint>.Count)
            {
                if (!Vector.EqualsAll(new Vector<uint>(row[i..]) & mask, mask)) return false;
            }
        }

        for (; i < row.Length; i++)
        {
            if ((row[i] & AlphaMask) != AlphaMask) return false;
        }

        return true;
    }

    // Orientation 2..4: mỗi dòng đích là một dòng nguồn (lật dọc) có thể đảo thứ tự (lật ngang).
    private static void CopyRows(PixelBuffer src, PixelBuffer dst, int orientation)
    {
        var height = src.Height;
        var flipVertical = orientation is 3 or 4;
        var mirror = orientation is 2 or 3;
        for (var y = 0; y < height; y++)
        {
            var target = dst.GetRow(y);
            src.GetRow(flipVertical ? height - 1 - y : y).CopyTo(target);
            if (mirror) MemoryMarshal.Cast<byte, uint>(target).Reverse();
        }
    }

    // Orientation 5..8: đích (x, y) lấy nguồn tại chỉ số pixel origin + x * stepX + y * stepY (nguồn W x H, đích H x W).
    //   5 transpose      : src(y, x)
    //   6 xoay 90 CW     : src(y, H-1-x)
    //   7 transverse     : src(W-1-y, H-1-x)
    //   8 xoay 270 CW    : src(W-1-y, x)
    private static unsafe void CopyTransposed(PixelBuffer src, PixelBuffer dst, int orientation)
    {
        long w = src.Width;
        long h = src.Height;
        var (origin, stepX, stepY) = orientation switch
        {
            5 => (0L, w, 1L),
            6 => ((h - 1) * w, -w, 1L),
            7 => (((h - 1) * w) + w - 1, -w, -1L),
            _ => (w - 1, w, -1L),
        };

        var source = (uint*)src.Address;
        var target = (uint*)dst.Address;
        var dstWidth = dst.Width;
        var dstHeight = dst.Height;
        for (var tileY = 0; tileY < dstHeight; tileY += TransposeTile)
        {
            var endY = Math.Min(tileY + TransposeTile, dstHeight);
            for (var tileX = 0; tileX < dstWidth; tileX += TransposeTile)
            {
                var endX = Math.Min(tileX + TransposeTile, dstWidth);
                for (var y = tileY; y < endY; y++)
                {
                    var row = target + ((long)y * dstWidth);
                    var index = origin + (tileX * stepX) + (y * stepY);
                    for (var x = tileX; x < endX; x++)
                    {
                        row[x] = source[index];
                        index += stepX;
                    }
                }
            }
        }

        GC.KeepAlive(src);
        GC.KeepAlive(dst);
    }
}

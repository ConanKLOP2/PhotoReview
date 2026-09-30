namespace PhotoReview.Imaging.LibRaw;

/// <summary>
/// Upfront memory check for a full LibRaw decode, done right after the file is opened (sizes are known, nothing big is allocated yet).
/// Peak estimate = width x height x (8 + 3) + targetWidth x targetHeight x 4 bytes:
/// <list type="bullet">
/// <item>8 B/px: LibRaw's working image (4 x 16-bit channels) that lives until the handle is closed;</item>
/// <item>3 B/px: the 8-bit RGB output buffer (libraw_dcraw_make_mem_image) that coexists with the working image;</item>
/// <item>4 B/px of the target: the WPF bitmap (written in place, no second managed copy, see <see cref="RgbBgraResampler.CreateBitmap"/>).</item>
/// </list>
/// A full-size 100 MP decode is therefore about 1.5 GB. The decode is refused (same InvalidOperationException mapping as an
/// OutOfMemoryException) when that exceeds the headroom: GC.GetGCMemoryInfo().TotalAvailableMemoryBytes (physical RAM or the
/// container limit) minus MemoryLoadBytes (memory in use as of the last GC).
/// </summary>
internal static class DecodeMemoryGuard
{
    internal const int WorkingImageBytesPerPixel = 8;
    internal const int RgbOutputBytesPerPixel = 3;
    internal const int BitmapBytesPerPixel = 4;

    /// <summary>Estimated peak bytes of one decode; saturates at <see cref="long.MaxValue"/>.</summary>
    internal static long EstimatePeakBytes(int width, int height, int targetWidth, int targetHeight)
    {
        if (width <= 0 || height <= 0) return 0;
        var source = (decimal)width * height * (WorkingImageBytesPerPixel + RgbOutputBytesPerPixel);
        var target = (decimal)Math.Max(0, targetWidth) * Math.Max(0, targetHeight) * BitmapBytesPerPixel;
        var total = source + target;
        return total >= long.MaxValue ? long.MaxValue : (long)total;
    }

    /// <summary>True when <paramref name="estimatedBytes"/> fits into <paramref name="totalAvailableBytes"/> minus <paramref name="memoryLoadBytes"/>. An unknown total (&lt;= 0) never refuses.</summary>
    internal static bool HasHeadroom(long estimatedBytes, long totalAvailableBytes, long memoryLoadBytes)
    {
        if (totalAvailableBytes <= 0) return true;
        return estimatedBytes <= totalAvailableBytes - Math.Max(0, memoryLoadBytes);
    }

    /// <summary>Production memory reading: (total available, current load) from the GC.</summary>
    internal static (long TotalAvailable, long Load) ReadGcMemoryInfo()
    {
        var info = GC.GetGCMemoryInfo();
        return (info.TotalAvailableMemoryBytes, info.MemoryLoadBytes);
    }
}

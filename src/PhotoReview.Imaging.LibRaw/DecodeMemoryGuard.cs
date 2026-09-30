namespace PhotoReview.Imaging.LibRaw;

/// <summary>
/// Upfront memory check for a full LibRaw decode, done right after the file is opened (sizes are known, nothing big is allocated yet).
/// Peak estimate = width x height x (8 + 3) + rawWidth x rawHeight x (2 or 8) + targetWidth x targetHeight x 4 bytes:
/// <list type="bullet">
/// <item>2 B/px (Bayer) or 8 B/px (linear/float DNG, 4 x 16-bit channels) of the SENSOR size (raw_width x raw_height): the unpacked raw
/// buffer (rawdata.raw_alloc) that LibRaw keeps until libraw_close, alongside the working image. Bayer 100 MP therefore really peaks at
/// ~13 B/px and a linear/float DNG at ~19 B/px;</item>
/// <item>8 B/px: LibRaw's working image (4 x 16-bit channels) that lives until the handle is closed;</item>
/// <item>3 B/px: the 8-bit RGB output buffer (libraw_dcraw_make_mem_image) that coexists with the working image;</item>
/// <item>4 B/px of the target: the WPF bitmap (written in place, no second managed copy, see <see cref="RgbBgraResampler.CreateBitmap"/>).</item>
/// </list>
/// A full-size 100 MP Bayer decode is therefore about 1.7 GB. The decode is refused (same InvalidOperationException mapping as an
/// OutOfMemoryException) when that exceeds the headroom: GC.GetGCMemoryInfo().TotalAvailableMemoryBytes (physical RAM or the
/// container limit) minus MemoryLoadBytes (memory in use as of the last GC).
/// </summary>
internal static class DecodeMemoryGuard
{
    internal const int WorkingImageBytesPerPixel = 8;
    internal const int RgbOutputBytesPerPixel = 3;
    internal const int BitmapBytesPerPixel = 4;
    internal const int BayerRawBytesPerPixel = 2;
    internal const int LinearRawBytesPerPixel = 8;

    /// <summary>How LibRaw stores the unpacked raw data, which decides the size of the buffer it keeps until the handle is closed.</summary>
    internal enum RawBufferFamily
    {
        /// <summary>One 16-bit sample per sensor pixel (raw_image).</summary>
        Bayer,
        /// <summary>Linear/deflate/float DNG and other multi-channel data (color3/color4/float buffers): up to 4 x 16-bit per pixel.</summary>
        LinearOrFloat,
    }

    /// <summary>
    /// Classifies the decoder LibRaw selected on open (libraw_get_decoder_info name). Only the DNG decoders that hold linear or
    /// float multi-channel data count as <see cref="RawBufferFamily.LinearOrFloat"/>; anything unknown stays <see cref="RawBufferFamily.Bayer"/>
    /// (no refusal without evidence).
    /// </summary>
    internal static RawBufferFamily FamilyFromDecoderName(string? decoderName)
    {
        if (decoderName is null) return RawBufferFamily.Bayer;
        return decoderName.Contains("deflate_dng", StringComparison.Ordinal)
            || decoderName.Contains("_fp_dng", StringComparison.Ordinal)
            || decoderName.Contains("lossy_dng", StringComparison.Ordinal)
                ? RawBufferFamily.LinearOrFloat
                : RawBufferFamily.Bayer;
    }

    /// <summary>
    /// Estimated peak bytes of one decode; saturates at <see cref="long.MaxValue"/>. <paramref name="rawWidth"/> x <paramref name="rawHeight"/>
    /// is the sensor size of the unpacked raw buffer; when unknown (&lt;= 0) the output size stands in for it.
    /// </summary>
    internal static long EstimatePeakBytes(int width, int height, int targetWidth, int targetHeight,
        int rawWidth = 0, int rawHeight = 0, RawBufferFamily family = RawBufferFamily.Bayer)
    {
        if (width <= 0 || height <= 0) return 0;
        var source = (decimal)width * height * (WorkingImageBytesPerPixel + RgbOutputBytesPerPixel);
        var rawPixels = rawWidth > 0 && rawHeight > 0 ? (decimal)rawWidth * rawHeight : (decimal)width * height;
        var raw = rawPixels * (family == RawBufferFamily.LinearOrFloat ? LinearRawBytesPerPixel : BayerRawBytesPerPixel);
        var target = (decimal)Math.Max(0, targetWidth) * Math.Max(0, targetHeight) * BitmapBytesPerPixel;
        var total = source + raw + target;
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

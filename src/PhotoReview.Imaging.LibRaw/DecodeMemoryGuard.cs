namespace PhotoReview.Imaging.LibRaw;

/// <summary>
/// Upfront memory check for a full LibRaw decode, done right after the file is opened (sizes are known, nothing big is allocated yet).
/// Peak estimate = width x height x (8 + 3) + rawWidth x rawHeight x (2 or 8) + targetWidth x targetHeight x 4 bytes:
/// <list type="bullet">
/// <item>2 B/px (Bayer), 8 B/px (non-Bayer integer, 4 x 16-bit channels) or 24 B/px (floating-point DNG, conservative) of the SENSOR size
/// (raw_width x raw_height): the unpacked raw buffer (rawdata.raw_alloc) that LibRaw keeps until libraw_close, alongside the working
/// image. Bayer 100 MP therefore really peaks at ~13 B/px, a linear DNG at ~19 B/px and a float DNG at ~35 B/px (see <see cref="Classify"/>);</item>
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
    /// <summary>Conservative peak of a floating-point DNG: LibRaw's float buffer (4, 12 or 16 B/px) stays alive while the 16-bit copy (up to 8 B/px) is built.</summary>
    internal const int FloatRawBytesPerPixel = 24;

    /// <summary>How LibRaw stores the unpacked raw data, which decides the size of the buffer it keeps until the handle is closed.</summary>
    internal enum RawBufferFamily
    {
        /// <summary>One 16-bit sample per sensor pixel (raw_image), 2 B/px; also X-Trans.</summary>
        Bayer,
        /// <summary>Non-Bayer integer data (linear DNG, ProRAW-style; color3/color4 buffers): up to 4 x 16-bit per pixel, 8 B/px.</summary>
        Linear,
        /// <summary>Floating-point DNG: float buffer plus the 16-bit copy alive at the same time, 24 B/px (conservative).</summary>
        Float,
    }

    /// <summary>What the file's own header says about its sampling (libraw_iparams_t): <c>colors</c> and the CFA <c>filters</c> (0 = no mosaic).</summary>
    internal readonly record struct RawStructure(int Colors, uint Filters)
    {
        /// <summary>A mosaic (filters != 0) of at most 4 colours: LibRaw keeps a single 16-bit sample per pixel.</summary>
        internal bool IsBayer => Filters != 0 && Colors is >= 1 and <= 4;
    }

    /// <summary>
    /// Classifies the raw buffer by STRUCTURE, not by decoder name alone (verified against LibRaw 0.22.2 decoder_info.cpp/unpack.cpp/fp_dng.cpp):
    /// <list type="bullet">
    /// <item>the DNG decoders (deflate/lossless/packed/lossy) serve BOTH mosaic files (2 B/px raw_alloc) and non-mosaic ones
    /// (linear DNG, ProRAW-style: ADOBECOPYPIXEL, 4 x 16-bit = 8 B/px). The CFA info (<paramref name="structure"/>) decides;
    /// when it could not be read safely the conservative 8 B/px applies;</item>
    /// <item>a floating-point DNG decoder (<c>_fp_dng</c>) is always <see cref="RawBufferFamily.Float"/>. Known limit: a FLOAT Bayer file
    /// routed through deflate_dng_load_raw cannot be told from an integer one with the public C API (the sample format sits in
    /// imgdata.color/tiff data without a getter), so it is estimated as an integer Bayer file;</item>
    /// <item>any other decoder (or an unknown name) stays <see cref="RawBufferFamily.Bayer"/>: no refusal without evidence.</item>
    /// </list>
    /// </summary>
    internal static RawBufferFamily Classify(string? decoderName, RawStructure? structure)
    {
        if (decoderName is null) return RawBufferFamily.Bayer;
        if (decoderName.Contains("_fp_dng", StringComparison.Ordinal)) return RawBufferFamily.Float;
        var dngFamily = decoderName.Contains("deflate_dng", StringComparison.Ordinal)
            || decoderName.Contains("lossless_dng", StringComparison.Ordinal)
            || decoderName.Contains("packed_dng", StringComparison.Ordinal)
            || decoderName.Contains("lossy_dng", StringComparison.Ordinal);
        if (!dngFamily) return RawBufferFamily.Bayer;
        return structure is { IsBayer: true } ? RawBufferFamily.Bayer : RawBufferFamily.Linear;
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
        var rawBytesPerPixel = family switch
        {
            RawBufferFamily.Float => FloatRawBytesPerPixel,
            RawBufferFamily.Linear => LinearRawBytesPerPixel,
            _ => BayerRawBytesPerPixel,
        };
        var raw = rawPixels * rawBytesPerPixel;
        var target = (decimal)Math.Max(0, targetWidth) * Math.Max(0, targetHeight) * BitmapBytesPerPixel;
        var total = source + raw + target;
        return total >= long.MaxValue ? long.MaxValue : (long)total;
    }

    /// <summary>True when <paramref name="estimatedBytes"/> fits into <paramref name="totalAvailableBytes"/> minus <paramref name="memoryLoadBytes"/>. An unknown total (&lt;= 0) never refuses.</summary>
    internal static bool HasHeadroom(long estimatedBytes, long totalAvailableBytes, long memoryLoadBytes) =>
        PhotoReview.Imaging.Decoding.MemoryHeadroom.HasHeadroom(estimatedBytes, totalAvailableBytes, memoryLoadBytes);

    /// <summary>Production memory reading: (total available, current load) from the GC.</summary>
    internal static (long TotalAvailable, long Load) ReadGcMemoryInfo() =>
        PhotoReview.Imaging.Decoding.MemoryHeadroom.ReadGcMemoryInfo();
}

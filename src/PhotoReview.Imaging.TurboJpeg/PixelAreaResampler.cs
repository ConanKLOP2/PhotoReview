using PhotoReview.Imaging.Pixels;

namespace PhotoReview.Imaging.TurboJpeg;

/// <summary>
/// Final shrink of a DCT-scaled JPEG (WP-05): exact area averaging of a Bgr32 <see cref="PixelBuffer"/>, the pixel-path
/// counterpart of the WPF ScaleTransform pass the legacy decoder runs (WIC Fant, also an area filter on shrink).
/// Pure integer arithmetic (every output channel is the correctly rounded mean of the source area it covers), so the
/// result is deterministic and needs neither WPF nor WIC. Only ever shrinks: the DCT factor is chosen so the scaled image is
/// at least the target on both axes.
/// </summary>
internal static class PixelAreaResampler
{
    /// <summary>Returns a new <paramref name="targetWidth"/> x <paramref name="targetHeight"/> buffer; <paramref name="src"/> is left to the caller.</summary>
    internal static unsafe PixelBuffer Resize(PixelBuffer src, int targetWidth, int targetHeight)
    {
        ArgumentNullException.ThrowIfNull(src);
        if (src.Layout != PixelLayout.Bgr32)
            throw new ArgumentException("Only Bgr32 buffers are resampled.", nameof(src));
        ArgumentOutOfRangeException.ThrowIfLessThan(targetWidth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(targetHeight, 1);
        // Never upscales (see the type summary): an axis that would grow keeps the source size.
        int tw = Math.Min(targetWidth, src.Width);
        int th = Math.Min(targetHeight, src.Height);
        int sw = src.Width;
        int sh = src.Height;

        var dst = PixelBuffer.Allocate(tw, th, PixelLayout.Bgr32);
        try
        {
            if (tw == sw && th == sh)
            {
                for (var y = 0; y < sh; y++) src.GetRow(y).CopyTo(dst.GetRow(y));
                return dst;
            }

            ResizeCore(src, dst, sw, sh, tw, th);
            return dst;
        }
        catch
        {
            dst.Dispose();
            throw;
        }
    }

    private static void ResizeCore(PixelBuffer src, PixelBuffer dst, int sw, int sh, int tw, int th)
    {
        // Horizontal footprint of output pixel x: source pixels [first[x], first[x] + count[x]) with integer overlaps
        // (in 1/tw pixel units) summing to sw. Flattened into one array.
        var first = new int[tw];
        var offset = new int[tw + 1];
        var weights = new List<int>(sw + 2 * tw);
        for (var x = 0; x < tw; x++)
        {
            long lo = (long)x * sw;           // output pixel spans [lo, hi) in 1/tw units; source pixel s spans [s*tw, (s+1)*tw)
            long hi = lo + sw;
            int s0 = (int)(lo / tw);
            int s1 = (int)((hi - 1) / tw);
            first[x] = s0;
            offset[x] = weights.Count;
            for (var s = s0; s <= s1; s++)
            {
                long overlap = Math.Min(hi, (long)(s + 1) * tw) - Math.Max(lo, (long)s * tw);
                weights.Add((int)overlap);
            }
        }

        offset[tw] = weights.Count;
        var w = weights.ToArray();

        long total = (long)sw * sh;
        long half = total / 2;
        var rowSums = new long[tw * 3];
        var accCurrent = new long[tw * 3];
        var accNext = new long[tw * 3];
        int outputRow = 0;

        for (var sy = 0; sy < sh; sy++)
        {
            HorizontalPass(src.GetRow(sy), first, offset, w, tw, rowSums);

            // Output rows overlapped by source row sy (at most two, because it only ever shrinks).
            long srcLo = (long)sy * th;
            long srcHi = srcLo + th;
            int oy0 = (int)(srcLo / sh);
            int oy1 = (int)((srcHi - 1) / sh);
            for (var oy = oy0; oy <= oy1 && oy < th; oy++)
            {
                long overlap = Math.Min(srcHi, (long)(oy + 1) * sh) - Math.Max(srcLo, (long)oy * sh);
                var acc = oy == outputRow ? accCurrent : accNext;
                for (var i = 0; i < rowSums.Length; i++) acc[i] += rowSums[i] * overlap;
            }

            if (srcHi >= (long)(outputRow + 1) * sh)
            {
                EmitRow(dst.GetRow(outputRow), accCurrent, tw, total, half);
                (accCurrent, accNext) = (accNext, accCurrent);
                Array.Clear(accNext);
                outputRow++;
            }
        }
    }

    private static void HorizontalPass(ReadOnlySpan<byte> row, int[] first, int[] offset, int[] weights, int tw, long[] sums)
    {
        for (var x = 0; x < tw; x++)
        {
            long b = 0, g = 0, r = 0;
            int s = first[x] * 4;
            for (var k = offset[x]; k < offset[x + 1]; k++, s += 4)
            {
                long weight = weights[k];
                b += row[s] * weight;
                g += row[s + 1] * weight;
                r += row[s + 2] * weight;
            }

            int o = x * 3;
            sums[o] = b;
            sums[o + 1] = g;
            sums[o + 2] = r;
        }
    }

    private static void EmitRow(Span<byte> row, long[] acc, int tw, long total, long half)
    {
        for (var x = 0; x < tw; x++)
        {
            int o = x * 3;
            int d = x * 4;
            row[d] = (byte)((acc[o] + half) / total);
            row[d + 1] = (byte)((acc[o + 1] + half) / total);
            row[d + 2] = (byte)((acc[o + 2] + half) / total);
            row[d + 3] = 0xFF;
        }
    }
}

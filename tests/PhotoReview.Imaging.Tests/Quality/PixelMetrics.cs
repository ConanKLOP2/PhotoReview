using System;

namespace PhotoReview.Imaging.Tests.Quality;

/// <summary>
/// Quantitative image comparison metrics (PSNR, CIE76 DeltaE, MaxChannelDiff) operating on BGRA32 pixel spans.
/// </summary>
public static class PixelMetrics
{
    private const double DeltaThreshold = 0.0088564516790356308; // (6/29)^3
    private const double DeltaFactor = 7.787037037037037;        // 1 / (3 * (6/29)^2)
    private const double DeltaOffset = 16.0 / 116.0;             // 4 / 29

    // D65 Standard Illuminant reference white point
    private const double Xn = 0.95047;
    private const double Yn = 1.00000;
    private const double Zn = 1.08883;

    /// <summary>
    /// Computes the Peak Signal-to-Noise Ratio (PSNR) across RGB channels for two BGRA32 buffers.
    /// Returns <see cref="double.PositiveInfinity"/> if the images are identical (MSE = 0).
    /// </summary>
    public static double Psnr(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        if (a.Length != b.Length)
        {
            throw new ArgumentException($"Buffer lengths do not match: {a.Length} != {b.Length}");
        }
        if (a.Length == 0 || a.Length % 4 != 0)
        {
            throw new ArgumentException($"Buffer length must be a non-zero multiple of 4 (BGRA32), got {a.Length}");
        }

        long sumSquaredError = 0;
        var pixelCount = a.Length / 4;

        for (var i = 0; i < a.Length; i += 4)
        {
            // BGRA: 0=Blue, 1=Green, 2=Red, 3=Alpha
            var diffB = a[i] - b[i];
            var diffG = a[i + 1] - b[i + 1];
            var diffR = a[i + 2] - b[i + 2];

            sumSquaredError += (diffB * diffB) + (diffG * diffG) + (diffR * diffR);
        }

        if (sumSquaredError == 0)
        {
            return double.PositiveInfinity;
        }

        var mse = (double)sumSquaredError / (pixelCount * 3.0);
        return 10.0 * Math.Log10((255.0 * 255.0) / mse);
    }

    /// <summary>
    /// Computes the average CIE76 color difference (Delta E) across all pixels by converting sRGB to Lab.
    /// </summary>
    public static double MeanDeltaE(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        if (a.Length != b.Length)
        {
            throw new ArgumentException($"Buffer lengths do not match: {a.Length} != {b.Length}");
        }
        if (a.Length == 0 || a.Length % 4 != 0)
        {
            throw new ArgumentException($"Buffer length must be a non-zero multiple of 4 (BGRA32), got {a.Length}");
        }

        var pixelCount = a.Length / 4;
        var totalDeltaE = 0.0;

        for (var i = 0; i < a.Length; i += 4)
        {
            var (l1, a1, b1) = RgbToLab(a[i + 2], a[i + 1], a[i]);
            var (l2, a2, b2) = RgbToLab(b[i + 2], b[i + 1], b[i]);

            var dL = l1 - l2;
            var da = a1 - a2;
            var db = b1 - b2;

            totalDeltaE += Math.Sqrt((dL * dL) + (da * da) + (db * db));
        }

        return totalDeltaE / pixelCount;
    }

    /// <summary>
    /// Computes the maximum absolute difference across all channel bytes.
    /// </summary>
    public static int MaxChannelDiff(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        if (a.Length != b.Length)
        {
            throw new ArgumentException($"Buffer lengths do not match: {a.Length} != {b.Length}");
        }

        var maxDiff = 0;
        for (var i = 0; i < a.Length; i++)
        {
            var diff = Math.Abs(a[i] - b[i]);
            if (diff > maxDiff)
            {
                maxDiff = diff;
            }
        }

        return maxDiff;
    }

    private static (double L, double A, double B) RgbToLab(byte r, byte g, byte b)
    {
        var rLinear = InverseGamma(r / 255.0);
        var gLinear = InverseGamma(g / 255.0);
        var bLinear = InverseGamma(b / 255.0);

        // sRGB to XYZ (D65)
        var x = (0.4124564 * rLinear) + (0.3575761 * gLinear) + (0.1804375 * bLinear);
        var y = (0.2126729 * rLinear) + (0.7151522 * gLinear) + (0.0721750 * bLinear);
        var z = (0.0193339 * rLinear) + (0.1191920 * gLinear) + (0.9503041 * bLinear);

        var fx = F(x / Xn);
        var fy = F(y / Yn);
        var fz = F(z / Zn);

        var l = (116.0 * fy) - 16.0;
        var labA = 500.0 * (fx - fy);
        var labB = 200.0 * (fy - fz);

        return (l, labA, labB);
    }

    private static double InverseGamma(double channel)
    {
        return channel <= 0.04045
            ? channel / 12.92
            : Math.Pow((channel + 0.055) / 1.055, 2.4);
    }

    private static double F(double t)
    {
        return t > DeltaThreshold
            ? Math.Cbrt(t)
            : (DeltaFactor * t) + DeltaOffset;
    }
}

namespace PhotoReview.TestSupport.PixelGolden;

/// <summary>
/// WP-28: image-difference measures for G-PIX. All of them compare the B, G, R channels (frames are opaque) over a region of two
/// equally sized frames. Pure functions over <see cref="PixelFrame"/>: no WPF, no D2D.
/// </summary>
public static class PixelMetrics
{
    /// <summary>Peak signal to noise ratio (dB, peak 255); +infinity when the region is identical.</summary>
    public static double Psnr(PixelFrame expected, PixelFrame actual, FrameRect region)
    {
        var mse = MeanSquaredError(expected, actual, region, 0, 0);
        return mse == 0 ? double.PositiveInfinity : 10 * Math.Log10(255.0 * 255.0 / mse);
    }

    /// <summary>Mean absolute difference per channel (0..255).</summary>
    public static double MeanAbsoluteError(PixelFrame expected, PixelFrame actual, FrameRect region)
    {
        Validate(expected, actual, region);
        long sum = 0;
        long count = 0;
        for (var y = region.Y; y < region.Bottom; y++)
        {
            var row = ((y * expected.Width) + region.X) * 4;
            for (var x = 0; x < region.Width; x++)
            {
                var i = row + (x * 4);
                sum += Math.Abs(expected.Bgra[i] - actual.Bgra[i])
                    + Math.Abs(expected.Bgra[i + 1] - actual.Bgra[i + 1])
                    + Math.Abs(expected.Bgra[i + 2] - actual.Bgra[i + 2]);
                count += 3;
            }
        }

        return count == 0 ? 0 : (double)sum / count;
    }

    /// <summary>Largest absolute channel difference in the region (0 = byte-identical).</summary>
    public static int MaxAbsoluteError(PixelFrame expected, PixelFrame actual, FrameRect region)
    {
        Validate(expected, actual, region);
        var max = 0;
        for (var y = region.Y; y < region.Bottom; y++)
        {
            var row = ((y * expected.Width) + region.X) * 4;
            for (var x = 0; x < region.Width; x++)
            {
                var i = row + (x * 4);
                max = Math.Max(max, Math.Abs(expected.Bgra[i] - actual.Bgra[i]));
                max = Math.Max(max, Math.Abs(expected.Bgra[i + 1] - actual.Bgra[i + 1]));
                max = Math.Max(max, Math.Abs(expected.Bgra[i + 2] - actual.Bgra[i + 2]));
            }
        }

        return max;
    }

    /// <summary>Number of pixels whose largest channel difference exceeds <paramref name="tolerance"/>.</summary>
    public static int DifferingPixels(PixelFrame expected, PixelFrame actual, FrameRect region, int tolerance)
    {
        Validate(expected, actual, region);
        var count = 0;
        for (var y = region.Y; y < region.Bottom; y++)
        {
            var row = ((y * expected.Width) + region.X) * 4;
            for (var x = 0; x < region.Width; x++)
            {
                var i = row + (x * 4);
                var d = Math.Max(
                    Math.Abs(expected.Bgra[i] - actual.Bgra[i]),
                    Math.Max(Math.Abs(expected.Bgra[i + 1] - actual.Bgra[i + 1]), Math.Abs(expected.Bgra[i + 2] - actual.Bgra[i + 2])));
                if (d > tolerance)
                {
                    count++;
                }
            }
        }

        return count;
    }

    /// <summary>
    /// Structural similarity of the luma channel (mean over 8 x 8 windows, stride 4, uniform weights; K1 = 0.01, K2 = 0.03,
    /// L = 255). 1.0 for identical regions; a region smaller than one window is measured as a single window.
    /// </summary>
    public static double Ssim(PixelFrame expected, PixelFrame actual, FrameRect region)
    {
        Validate(expected, actual, region);
        if (region.IsEmpty)
        {
            return 1.0;
        }

        const double C1 = (0.01 * 255) * (0.01 * 255);
        const double C2 = (0.03 * 255) * (0.03 * 255);
        var window = Math.Min(8, Math.Min(region.Width, region.Height));
        var stride = Math.Max(1, window / 2);
        var total = 0.0;
        var windows = 0;
        for (var y = region.Y; y + window <= region.Bottom; y += stride)
        {
            for (var x = region.X; x + window <= region.Right; x += stride)
            {
                double sumA = 0, sumB = 0, sumAA = 0, sumBB = 0, sumAB = 0;
                for (var wy = 0; wy < window; wy++)
                {
                    for (var wx = 0; wx < window; wx++)
                    {
                        var i = (((y + wy) * expected.Width) + x + wx) * 4;
                        var a = Luma(expected.Bgra, i);
                        var b = Luma(actual.Bgra, i);
                        sumA += a;
                        sumB += b;
                        sumAA += a * a;
                        sumBB += b * b;
                        sumAB += a * b;
                    }
                }

                double n = window * window;
                var meanA = sumA / n;
                var meanB = sumB / n;
                var varA = (sumAA / n) - (meanA * meanA);
                var varB = (sumBB / n) - (meanB * meanB);
                var cov = (sumAB / n) - (meanA * meanB);
                total += ((2 * meanA * meanB) + C1) * ((2 * cov) + C2)
                    / (((meanA * meanA) + (meanB * meanB) + C1) * (varA + varB + C2));
                windows++;
            }
        }

        return windows == 0 ? 1.0 : total / windows;
    }

    /// <summary>
    /// The integer shift (dx, dy), |dx|, |dy| &lt;= <paramref name="radius"/>, that makes <paramref name="actual"/> most like
    /// <paramref name="expected"/> over <paramref name="region"/> (compares actual(x + dx, y + dy) with expected(x, y)).
    /// (0, 0) wins ties, so a result other than (0, 0) means a real geometric offset. The region must have
    /// <paramref name="radius"/> pixels of margin inside the frame.
    /// </summary>
    public static (int Dx, int Dy) BestShift(PixelFrame expected, PixelFrame actual, FrameRect region, int radius)
    {
        Validate(expected, actual, region);
        var inner = new FrameRect(region.X, region.Y, region.Width, region.Height);
        var bestError = MeanSquaredError(expected, actual, inner, 0, 0);
        var best = (Dx: 0, Dy: 0);
        for (var dy = -radius; dy <= radius; dy++)
        {
            for (var dx = -radius; dx <= radius; dx++)
            {
                if (dx == 0 && dy == 0)
                {
                    continue;
                }

                var error = MeanSquaredError(expected, actual, inner, dx, dy);
                if (error < bestError)
                {
                    bestError = error;
                    best = (dx, dy);
                }
            }
        }

        return best;
    }

    /// <summary>
    /// Bounding box of the pixels of <paramref name="frame"/> inside <paramref name="region"/> that differ from the flat
    /// background colour by more than <paramref name="tolerance"/> (any channel); empty when there are none.
    /// </summary>
    public static FrameRect ContentBounds(PixelFrame frame, FrameRect region, (byte B, byte G, byte R) background, int tolerance)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var left = int.MaxValue;
        var top = int.MaxValue;
        var right = int.MinValue;
        var bottom = int.MinValue;
        for (var y = region.Y; y < region.Bottom; y++)
        {
            for (var x = region.X; x < region.Right; x++)
            {
                var i = ((y * frame.Width) + x) * 4;
                if (Math.Abs(frame.Bgra[i] - background.B) > tolerance
                    || Math.Abs(frame.Bgra[i + 1] - background.G) > tolerance
                    || Math.Abs(frame.Bgra[i + 2] - background.R) > tolerance)
                {
                    left = Math.Min(left, x);
                    top = Math.Min(top, y);
                    right = Math.Max(right, x + 1);
                    bottom = Math.Max(bottom, y + 1);
                }
            }
        }

        return left == int.MaxValue ? default : new FrameRect(left, top, right - left, bottom - top);
    }

    /// <summary>
    /// Bounding boxes (4-connected components of the changed pixels, after a 2 px dilation that bridges anti-aliased gaps) of the
    /// pixels where <paramref name="withOverlay"/> differs from <paramref name="without"/> by more than
    /// <paramref name="tolerance"/>. Used to measure the HUD panels from pixels, the way the user sees them.
    /// </summary>
    public static IReadOnlyList<FrameRect> ChangedRegions(PixelFrame without, PixelFrame withOverlay, int tolerance, int minArea = 16)
    {
        ArgumentNullException.ThrowIfNull(without);
        ArgumentNullException.ThrowIfNull(withOverlay);
        Validate(without, withOverlay, new FrameRect(0, 0, without.Width, without.Height));
        var width = without.Width;
        var height = without.Height;
        var changed = new bool[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = ((y * width) + x) * 4;
                var d = Math.Max(
                    Math.Abs(without.Bgra[i] - withOverlay.Bgra[i]),
                    Math.Max(Math.Abs(without.Bgra[i + 1] - withOverlay.Bgra[i + 1]), Math.Abs(without.Bgra[i + 2] - withOverlay.Bgra[i + 2])));
                changed[(y * width) + x] = d > tolerance;
            }
        }

        var dilated = new bool[changed.Length];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (!changed[(y * width) + x])
                {
                    continue;
                }

                for (var dy = -2; dy <= 2; dy++)
                {
                    for (var dx = -2; dx <= 2; dx++)
                    {
                        var nx = x + dx;
                        var ny = y + dy;
                        if (nx >= 0 && ny >= 0 && nx < width && ny < height)
                        {
                            dilated[(ny * width) + nx] = true;
                        }
                    }
                }
            }
        }

        var seen = new bool[changed.Length];
        var result = new List<FrameRect>();
        var stack = new Stack<int>();
        for (var start = 0; start < dilated.Length; start++)
        {
            if (!dilated[start] || seen[start])
            {
                continue;
            }

            int left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue, area = 0;
            seen[start] = true;
            stack.Push(start);
            while (stack.Count > 0)
            {
                var index = stack.Pop();
                var x = index % width;
                var y = index / width;
                if (changed[index])
                {
                    left = Math.Min(left, x);
                    top = Math.Min(top, y);
                    right = Math.Max(right, x + 1);
                    bottom = Math.Max(bottom, y + 1);
                    area++;
                }

                foreach (var next in Neighbours(x, y, width, height))
                {
                    if (dilated[next] && !seen[next])
                    {
                        seen[next] = true;
                        stack.Push(next);
                    }
                }
            }

            if (area >= minArea)
            {
                result.Add(new FrameRect(left, top, right - left, bottom - top));
            }
        }

        result.Sort(static (a, b) => a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.X.CompareTo(b.X));
        return result;
    }

    private static IEnumerable<int> Neighbours(int x, int y, int width, int height)
    {
        if (x > 0)
        {
            yield return (y * width) + x - 1;
        }

        if (x + 1 < width)
        {
            yield return (y * width) + x + 1;
        }

        if (y > 0)
        {
            yield return ((y - 1) * width) + x;
        }

        if (y + 1 < height)
        {
            yield return ((y + 1) * width) + x;
        }
    }

    private static double Luma(byte[] bgra, int i) => (0.114 * bgra[i]) + (0.587 * bgra[i + 1]) + (0.299 * bgra[i + 2]);

    private static double MeanSquaredError(PixelFrame expected, PixelFrame actual, FrameRect region, int dx, int dy)
    {
        Validate(expected, actual, region);
        if (region.X + dx < 0 || region.Y + dy < 0 || region.Right + dx > actual.Width || region.Bottom + dy > actual.Height)
        {
            throw new ArgumentOutOfRangeException(nameof(region), "Shifted region leaves the frame.");
        }

        double sum = 0;
        long count = 0;
        for (var y = region.Y; y < region.Bottom; y++)
        {
            var e = ((y * expected.Width) + region.X) * 4;
            var a = (((y + dy) * actual.Width) + region.X + dx) * 4;
            for (var x = 0; x < region.Width; x++)
            {
                var db = expected.Bgra[e] - actual.Bgra[a];
                var dg = expected.Bgra[e + 1] - actual.Bgra[a + 1];
                var dr = expected.Bgra[e + 2] - actual.Bgra[a + 2];
                sum += (db * db) + (dg * dg) + (dr * dr);
                count += 3;
                e += 4;
                a += 4;
            }
        }

        return count == 0 ? 0 : sum / count;
    }

    private static void Validate(PixelFrame expected, PixelFrame actual, FrameRect region)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);
        if (expected.Width != actual.Width || expected.Height != actual.Height)
        {
            throw new ArgumentException(
                $"Frames differ in size: {expected.Width}x{expected.Height} vs {actual.Width}x{actual.Height}.", nameof(actual));
        }

        if (region.X < 0 || region.Y < 0 || region.Right > expected.Width || region.Bottom > expected.Height)
        {
            throw new ArgumentOutOfRangeException(nameof(region), $"Region {region} is outside the {expected.Width}x{expected.Height} frame.");
        }
    }
}

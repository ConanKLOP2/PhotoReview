using System.Globalization;

namespace PhotoReview.TestSupport.PixelGolden;

/// <summary>The floors/ceilings one <see cref="PixelGoldenExpectation"/> is held to.</summary>
/// <param name="ImagePsnr">Picture region PSNR floor, dB (ignored for Identical).</param>
/// <param name="ImageSsim">Picture region SSIM (luma) floor.</param>
/// <param name="ImageMae">Picture region mean absolute error ceiling (per channel, 0..255).</param>
/// <param name="FramePsnr">Whole frame (canvas, picture, scroll bars) PSNR floor, dB.</param>
/// <param name="MaxChannelDelta">Largest allowed channel difference in the picture region; -1 = not a pixel-exact bar.</param>
public sealed record PixelGoldenBar(double ImagePsnr, double ImageSsim, double ImageMae, double FramePsnr, int MaxChannelDelta);

/// <summary>
/// WP-28: the bars of NO-WPF-EXEC-PLAN 7.3 (2) (geometry &lt;= 1 px, PSNR &gt;= 32 dB at Fit, &gt;= 28 dB at other zooms, identical at
/// 100 %) plus the floors this package measured and pinned for the rest (see <c>NOWPF-WP28-GOLDEN-PIXEL.md</c> for the numbers).
/// </summary>
public static class PixelGoldenThresholds
{
    /// <summary>7.3 (2): geometric offset of the picture edges, pixels.</summary>
    public const int GeometryPixels = 1;

    /// <summary>Scroll bar strips and canvas (outside the viewport): flat fills, only rounded thumb ends anti-alias (measured >= 59.9 dB).</summary>
    public const double ChromePsnr = 45.0;

    /// <summary>Tolerance (px) of HUD panel boxes found from pixels (7.3 (2): +/- 2 px).</summary>
    public const int PanelBoxPixels = 2;

    public static PixelGoldenBar For(PixelGoldenExpectation expectation) => expectation switch
    {
        // 7.3 (2) "giong het o 100 %". Frame floor 60 dB: scroll bar anti-aliasing is the only source of difference (measured >= 72.8 dB).
        PixelGoldenExpectation.Identical => new PixelGoldenBar(double.PositiveInfinity, 1.0, 0.0, 60.0, 0),

        // 100 % at DPI > 1: WPF's own resampling puts +/-1 level on ~20 % of the pixels (measured max 1, PSNR 54.7, MAE 0.22).
        PixelGoldenExpectation.NearIdentical => new PixelGoldenBar(50.0, 0.999, 0.5, 50.0, 1),

        // 7.3 (2): PSNR >= 32 dB at Fit. Measured on photographic content: PSNR >= 40.5, SSIM >= 0.985, MAE <= 1.02.
        PixelGoldenExpectation.Fit => new PixelGoldenBar(32.0, 0.93, 4.0, 32.0, -1),

        // 7.3 (2): PSNR >= 28 dB at other zooms. Measured: PSNR >= 28.3 (half-pixel centring), SSIM >= 0.95, MAE <= 2.45.
        PixelGoldenExpectation.Zoom => new PixelGoldenBar(28.0, 0.88, 6.0, 28.0, -1),

        // Not held to 7.3: synthetic 1-2 px patterns. Measured PSNR >= 21.8, SSIM >= 0.488, MAE <= 13.96 -> pinned 3 dB/0.08/3 below.
        PixelGoldenExpectation.HighFrequency => new PixelGoldenBar(19.0, 0.40, 17.0, 19.0, -1),

        // Measured PSNR 11.6, SSIM 0.116, MAE 50: pinned just under; geometry (<= 1 px) is the real bar.
        PixelGoldenExpectation.HalfPixel => new PixelGoldenBar(9.0, 0.05, 60.0, 9.0, -1),
        _ => throw new ArgumentOutOfRangeException(nameof(expectation), expectation, null),
    };
}

/// <summary>Everything measured for one case (all of it also lands in the report).</summary>
public sealed record PixelGoldenMeasure(
    PixelGoldenCase Case,
    FrameRect ImageRegion,
    double ImagePsnr,
    double ImageSsim,
    double ImageMae,
    int ImageMaxAbs,
    int ImageDifferingPixels,
    double FramePsnr,
    double FrameSsim,
    double ChromePsnr,
    int ShiftX,
    int ShiftY,
    int EdgeError)
{
    public string Summary => string.Create(CultureInfo.InvariantCulture,
        $"{Case.Name,-34} {Case.Expectation,-9} img PSNR {Fmt(ImagePsnr)} SSIM {ImageSsim:F4} MAE {ImageMae:F2} max {ImageMaxAbs,3} "
        + $"| frame PSNR {Fmt(FramePsnr)} SSIM {FrameSsim:F4} | chrome PSNR {Fmt(ChromePsnr)} | shift ({ShiftX},{ShiftY}) edge {EdgeError}");

    private static string Fmt(double psnr) => double.IsPositiveInfinity(psnr) ? "  inf " : psnr.ToString("F1", CultureInfo.InvariantCulture).PadLeft(6);
}

/// <summary>
/// WP-28: measures one shell frame against its WPF golden and judges it by <see cref="PixelGoldenThresholds"/>. Geometry (the
/// "<= 1 px" bar) is measured two ways: the integer shift that best aligns the picture region (works when the picture fills the
/// viewport) and, for pictures smaller than the viewport, the distance of each detected picture edge from the shell's own layout.
/// </summary>
public static class PixelGoldenComparer
{
    private const int ShiftSearchRadius = 3;

    /// <param name="shellViewport">Viewport (client minus scroll bars) in device pixels, from the shell layout.</param>
    /// <param name="shellImage">The picture's box in device pixels (unclipped), from the shell layout.</param>
    public static PixelGoldenMeasure Measure(PixelGoldenCase c, PixelFrame golden, PixelFrame actual, FrameRect shellViewport, FrameRect shellImage)
    {
        ArgumentNullException.ThrowIfNull(c);
        ArgumentNullException.ThrowIfNull(golden);
        ArgumentNullException.ThrowIfNull(actual);
        var whole = new FrameRect(0, 0, golden.Width, golden.Height);
        var viewport = shellViewport.Intersect(whole);

        // The picture region the bars apply to: the picture inside the viewport, one pixel inside every edge so the
        // anti-aliased rim (WPF and D2D cover fractional edges differently) does not decide the verdict.
        var image = Inset(shellImage.Intersect(viewport), 1);
        if (image.IsEmpty)
        {
            image = Inset(viewport, 1);
        }

        var shift = PixelMetrics.BestShift(golden, actual, Inset(image, ShiftSearchRadius).IsEmpty ? image : Inset(image, ShiftSearchRadius), ShiftSearchRadius);
        var chrome = ChromePsnrOf(golden, actual, whole, viewport);
        var edge = EdgeError(golden, shellImage, viewport);
        return new PixelGoldenMeasure(
            c,
            image,
            PixelMetrics.Psnr(golden, actual, image),
            PixelMetrics.Ssim(golden, actual, image),
            PixelMetrics.MeanAbsoluteError(golden, actual, image),
            PixelMetrics.MaxAbsoluteError(golden, actual, image),
            PixelMetrics.DifferingPixels(golden, actual, image, 0),
            PixelMetrics.Psnr(golden, actual, whole),
            PixelMetrics.Ssim(golden, actual, whole),
            chrome,
            shift.Dx,
            shift.Dy,
            edge);
    }

    /// <summary>The violations (empty = the case passes), each phrased with the number and the bar it missed.</summary>
    public static IReadOnlyList<string> Violations(PixelGoldenMeasure m)
    {
        ArgumentNullException.ThrowIfNull(m);
        var list = new List<string>();
        var inv = CultureInfo.InvariantCulture;
        if (Math.Abs(m.ShiftX) > PixelGoldenThresholds.GeometryPixels || Math.Abs(m.ShiftY) > PixelGoldenThresholds.GeometryPixels)
        {
            list.Add(string.Create(inv, $"geometry: best shift ({m.ShiftX}, {m.ShiftY}) px exceeds {PixelGoldenThresholds.GeometryPixels}"));
        }

        if (m.EdgeError > PixelGoldenThresholds.GeometryPixels)
        {
            list.Add(string.Create(inv, $"geometry: picture edge off by {m.EdgeError} px (limit {PixelGoldenThresholds.GeometryPixels})"));
        }

        var bar = PixelGoldenThresholds.For(m.Case.Expectation);
        var label = m.Case.Expectation.ToString();
        if (bar.MaxChannelDelta >= 0 && m.ImageMaxAbs > bar.MaxChannelDelta)
        {
            list.Add(string.Create(inv,
                $"{label}: {m.ImageDifferingPixels} px differ, max channel delta {m.ImageMaxAbs} > {bar.MaxChannelDelta}"));
        }

        if (bar.MaxChannelDelta != 0)
        {
            Floor(list, label + " PSNR", m.ImagePsnr, bar.ImagePsnr);
            Floor(list, label + " SSIM", m.ImageSsim, bar.ImageSsim);
            Ceiling(list, label + " MAE", m.ImageMae, bar.ImageMae);
        }

        Floor(list, "frame PSNR", m.FramePsnr, bar.FramePsnr);
        Floor(list, "scroll bar/canvas PSNR", m.ChromePsnr, PixelGoldenThresholds.ChromePsnr);
        return list;
    }

    private static void Floor(List<string> list, string what, double value, double floor)
    {
        if (value < floor)
        {
            list.Add(string.Create(CultureInfo.InvariantCulture, $"{what} {value:F3} < {floor}"));
        }
    }

    private static void Ceiling(List<string> list, string what, double value, double ceiling)
    {
        if (value > ceiling)
        {
            list.Add(string.Create(CultureInfo.InvariantCulture, $"{what} {value:F3} > {ceiling}"));
        }
    }

    private static FrameRect Inset(FrameRect rect, int amount) => rect.Inflate(-amount);

    /// <summary>PSNR over the pixels outside the viewport (scroll bar strips and the corner); +infinity when there are none.</summary>
    private static double ChromePsnrOf(PixelFrame golden, PixelFrame actual, FrameRect whole, FrameRect viewport)
    {
        double squares = 0;
        long count = 0;
        for (var y = 0; y < whole.Height; y++)
        {
            for (var x = 0; x < whole.Width; x++)
            {
                if (viewport.Contains(x, y))
                {
                    continue;
                }

                var i = ((y * whole.Width) + x) * 4;
                for (var ch = 0; ch < 3; ch++)
                {
                    var d = golden.Bgra[i + ch] - actual.Bgra[i + ch];
                    squares += d * d;
                    count++;
                }
            }
        }

        return count == 0 || squares == 0 ? double.PositiveInfinity : 10 * Math.Log10(255.0 * 255.0 / (squares / count));
    }

    /// <summary>
    /// Largest distance (px) between an edge of the picture as the shell lays it out and that edge detected in the golden, over the
    /// edges that lie inside the viewport (a picture edge clipped by the viewport has nothing to detect). 0 when every edge is
    /// clipped.
    /// </summary>
    private static int EdgeError(PixelFrame golden, FrameRect shellImage, FrameRect viewport)
    {
        var inside = shellImage.Intersect(viewport);
        if (inside.IsEmpty)
        {
            return 0;
        }

        var bounds = PixelMetrics.ContentBounds(golden, viewport, PixelGoldenCatalog.Canvas, 5);
        if (bounds.IsEmpty)
        {
            return int.MaxValue;
        }

        var error = 0;
        if (shellImage.X > viewport.X)
        {
            error = Math.Max(error, Math.Abs(bounds.X - shellImage.X));
        }

        if (shellImage.Y > viewport.Y)
        {
            error = Math.Max(error, Math.Abs(bounds.Y - shellImage.Y));
        }

        if (shellImage.Right < viewport.Right)
        {
            error = Math.Max(error, Math.Abs(bounds.Right - shellImage.Right));
        }

        if (shellImage.Bottom < viewport.Bottom)
        {
            error = Math.Max(error, Math.Abs(bounds.Bottom - shellImage.Bottom));
        }

        return error;
    }
}

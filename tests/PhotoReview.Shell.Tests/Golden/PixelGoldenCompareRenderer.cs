using System.Globalization;
using PhotoReview.App.Input;
using PhotoReview.Core.Model;
using PhotoReview.Imaging.Pixels;
using PhotoReview.Shell.Rendering;
using PhotoReview.Shell.Tests.Overlay;
using PhotoReview.Shell.Tests.Rendering;
using PhotoReview.Shell.Tests.Viewing;
using PhotoReview.Shell.Win32.Viewing;
using PhotoReview.TestSupport.PixelGolden;

namespace PhotoReview.Shell.Tests.Golden;

/// <summary>A compare frame of the shell plus where its parts are (device pixels), from the shell's own layout.</summary>
internal sealed record CompareShellFrame(
    PixelFrame Frame,
    FrameRect Panel,
    FrameRect LeftBorder,
    FrameRect RightBorder,
    FrameRect? LeftImage,
    FrameRect? RightImage,
    int BorderPixels);

/// <summary>
/// WP-28b: draws a G-PIX compare case as the shell does - the real Fit layout of the main picture (<see cref="ScrollBarRig"/>), then
/// <see cref="PresentationComposer.Draw"/> with the compare frame (<c>CompareLayer</c>: panel canvas, borders, Uniform pictures) on the
/// D2D WARP offscreen surface. Pictures reach the composer upright (<c>PixelOps.ApplyOrientation</c>), as every app decoder delivers them.
/// </summary>
internal static class PixelGoldenCompareRenderer
{
    internal static CompareShellFrame Render(PixelGoldenCompareCase c)
    {
        var main = c.MainCase;
        using var rig = new ScrollBarRig(c.ClientWidth, c.ClientHeight, main.OrientedWidth, main.OrientedHeight, c.Dpi, actualSize: false);
        rig.Viewer.ScalingQuality = ScalingQuality.HighQuality;
        rig.Viewport.UpdateLayout();
        var origin = rig.Viewport.ImageOrigin;
        var destination = new RectD(origin.X, origin.Y, rig.Viewport.ImageActualWidth, rig.Viewport.ImageActualHeight);

        using var mainBuffer = PixelGoldenShellRenderer.ToPixelBuffer(PixelGoldenScenes.Create(main.Scene, main.ImageWidth, main.ImageHeight));
        using var left = Upright(c.Left);
        using var right = Upright(c.Right);

        using var surface = D2DRenderSurface.CreateOffscreenSurface(c.PixelWidth, c.PixelHeight, c.Dpi);
        var dispatcher = new ManualDispatcher();
        using var cache = new GpuImageCache(surface, dispatcher);
        using var composer = new PresentationComposer(cache, dispatcher, new FakeFrameClock(), () => { }, ticksPerSecond: 1000, timestamp: () => 0);
        var client = new SizeD(c.ClientWidth, c.ClientHeight);
        var compare = new CompareFrame(left, right, c.Selected == PixelGoldenCompareSelection.Left, c.Selected == PixelGoldenCompareSelection.Right);
        var inputs = new PresentationInputs(client, destination, rig.Viewer.EffectiveScalingQuality, rig.Viewer.ScalingQuality, c.Dpi,
            true, compare);
        composer.Set(mainBuffer);

        using var pixels = RenderTestImages.Render(surface, dc =>
        {
            dc.Clear(RenderColors.DarkCanvas);
            composer.Draw(dc, inputs);
            rig.Bars.Draw(dc);
        });

        FrameRect Px(RectD r) => new(
            (int)Math.Round(r.X * c.Dpi), (int)Math.Round(r.Y * c.Dpi),
            (int)Math.Round(r.Right * c.Dpi) - (int)Math.Round(r.X * c.Dpi),
            (int)Math.Round(r.Bottom * c.Dpi) - (int)Math.Round(r.Y * c.Dpi));

        FrameRect? Cell(CompareSide side, PixelGoldenCompareSide? picture) =>
            picture is null ? null : Px(CompareLayout.Image(client, side, picture.OrientedWidth, picture.OrientedHeight));

        return new CompareShellFrame(
            PixelGoldenShellRenderer.ToFrame(pixels),
            Px(CompareLayout.Panel(client)),
            Px(CompareLayout.Border(client, CompareSide.Left)),
            Px(CompareLayout.Border(client, CompareSide.Right)),
            Cell(CompareSide.Left, c.Left),
            Cell(CompareSide.Right, c.Right),
            (int)Math.Round(CompareLayout.BorderThickness * c.Dpi));
    }

    private static PixelBuffer? Upright(PixelGoldenCompareSide? side)
    {
        if (side is null)
        {
            return null;
        }

        using var raw = PixelGoldenShellRenderer.ToPixelBuffer(PixelGoldenScenes.Create(side.Scene, side.Width, side.Height));
        return side.ExifOrientation == 1
            ? PixelGoldenShellRenderer.ToPixelBuffer(PixelGoldenScenes.Create(side.Scene, side.Width, side.Height))
            : PixelOps.ApplyOrientation(raw, side.ExifOrientation);
    }
}

/// <summary>What a compare frame was measured to differ by (shell vs the WPF golden).</summary>
internal sealed record CompareMeasure(
    double LeftPsnr,
    double LeftSsim,
    double LeftMae,
    double RightPsnr,
    double RightSsim,
    double RightMae,
    int PanelFlatMaxAbs,
    int PanelFlatDiffering,
    int BorderMaxAbs,
    double RingPsnr,
    (int Dx, int Dy) LeftShift,
    (int Dx, int Dy) RightShift)
{
    internal string Summary => string.Create(CultureInfo.InvariantCulture,
        $"left PSNR {F(LeftPsnr)} SSIM {LeftSsim:F4} MAE {LeftMae:F2} | right PSNR {F(RightPsnr)} SSIM {RightSsim:F4} MAE {RightMae:F2} "
        + $"| borders max {BorderMaxAbs} | panel flat max {PanelFlatMaxAbs} ({PanelFlatDiffering} px) | ring PSNR {F(RingPsnr)} "
        + $"| shift L{LeftShift} R{RightShift}");

    private static string F(double v) => double.IsPositiveInfinity(v) ? "inf" : v.ToString("F1", CultureInfo.InvariantCulture);
}

/// <summary>
/// WP-28b: the compare bars. 7.3 (2) has no compare line of its own; the cells are Fit/Uniform pictures, so they are held to the
/// Fit bar (PSNR &gt;= 32 dB, plus the pinned SSIM/MAE of Fit) and 1 px geometry; what is flat (panel canvas, the two 2 DIP borders
/// whose colour carries the selection, the empty cell of a loading picture) must be BYTE-IDENTICAL to WPF, and so must the 8 DIP ring
/// of the main picture that the panel's margin leaves visible (held to the Fit bar, it is a rescaled picture).
/// </summary>
internal static class PixelGoldenCompareComparer
{
    internal static CompareMeasure Measure(PixelFrame golden, CompareShellFrame shell)
    {
        var actual = shell.Frame;
        var whole = new FrameRect(0, 0, golden.Width, golden.Height);
        var nonImage = new List<FrameRect>();
        var imageRects = new List<FrameRect>();
        foreach (var image in new[] { shell.LeftImage, shell.RightImage })
        {
            if (image is { } r)
            {
                imageRects.Add(r);
            }
        }

        // Pixels of the panel that are not (the rim of) a picture: canvas, gutters, borders, the empty cell.
        var flatDiff = 0;
        var flatMax = 0;
        for (var y = shell.Panel.Y; y < shell.Panel.Bottom; y++)
        {
            for (var x = shell.Panel.X; x < shell.Panel.Right; x++)
            {
                if (imageRects.Any(r => r.Inflate(1).Contains(x, y)))
                {
                    continue;
                }

                var g = golden.At(x, y);
                var a = actual.At(x, y);
                var d = Math.Max(Math.Abs(g.B - a.B), Math.Max(Math.Abs(g.G - a.G), Math.Abs(g.R - a.R)));
                if (d > 0)
                {
                    flatDiff++;
                    flatMax = Math.Max(flatMax, d);
                }
            }
        }

        var borderMax = 0;
        foreach (var border in new[] { shell.LeftBorder, shell.RightBorder })
        {
            foreach (var strip in BorderStrips(border, golden, shell))
            {
                borderMax = Math.Max(borderMax, PixelMetrics.MaxAbsoluteError(golden, actual, strip));
            }
        }

        var ring = RingStrips(whole, shell.Panel);
        var ringPsnr = double.PositiveInfinity;
        foreach (var strip in ring)
        {
            ringPsnr = Math.Min(ringPsnr, PixelMetrics.Psnr(golden, actual, strip));
        }

        var (lp, ls, lm, lsh) = Cell(golden, actual, shell.LeftImage);
        var (rp, rs, rm, rsh) = Cell(golden, actual, shell.RightImage);
        return new CompareMeasure(lp, ls, lm, rp, rs, rm, flatMax, flatDiff, borderMax, ringPsnr, lsh, rsh);
    }

    private static (double Psnr, double Ssim, double Mae, (int, int) Shift) Cell(PixelFrame golden, PixelFrame actual, FrameRect? image)
    {
        if (image is not { } box)
        {
            return (double.PositiveInfinity, 1.0, 0.0, (0, 0));
        }

        var inner = box.Inflate(-1);
        var shiftRegion = box.Inflate(-4).IsEmpty ? inner : box.Inflate(-4);
        var shift = PixelMetrics.BestShift(golden, actual, shiftRegion, 3);
        return (PixelMetrics.Psnr(golden, actual, inner), PixelMetrics.Ssim(golden, actual, inner),
            PixelMetrics.MeanAbsoluteError(golden, actual, inner), shift);
    }

    /// <summary>The 2 DIP border as four strips (device px), measured from the shell's own box.</summary>
    private static IEnumerable<FrameRect> BorderStrips(FrameRect border, PixelFrame golden, CompareShellFrame shell)
    {
        var t = Math.Max(1, shell.BorderPixels);
        yield return new FrameRect(border.X, border.Y, border.Width, t);
        yield return new FrameRect(border.X, border.Bottom - t, border.Width, t);
        yield return new FrameRect(border.X, border.Y + t, t, border.Height - (2 * t));
        yield return new FrameRect(border.Right - t, border.Y + t, t, border.Height - (2 * t));
    }

    /// <summary>The margin ring around the panel (the part of the main picture that stays visible).</summary>
    private static IEnumerable<FrameRect> RingStrips(FrameRect whole, FrameRect panel)
    {
        yield return new FrameRect(0, 0, whole.Width, panel.Y);
        yield return new FrameRect(0, panel.Bottom, whole.Width, whole.Height - panel.Bottom);
        yield return new FrameRect(0, panel.Y, panel.X, panel.Height);
        yield return new FrameRect(panel.Right, panel.Y, whole.Width - panel.Right, panel.Height);
    }

    internal static IReadOnlyList<string> Violations(CompareMeasure m)
    {
        var list = new List<string>();
        var fit = PixelGoldenThresholds.For(PixelGoldenExpectation.Fit);
        void Cell(string side, double psnr, double ssim, double mae, (int Dx, int Dy) shift)
        {
            if (psnr < fit.ImagePsnr)
            {
                list.Add($"{side} cell PSNR {psnr:F2} < {fit.ImagePsnr}");
            }

            if (ssim < fit.ImageSsim)
            {
                list.Add($"{side} cell SSIM {ssim:F4} < {fit.ImageSsim}");
            }

            if (mae > fit.ImageMae)
            {
                list.Add($"{side} cell MAE {mae:F2} > {fit.ImageMae}");
            }

            if (Math.Abs(shift.Dx) > PixelGoldenThresholds.GeometryPixels || Math.Abs(shift.Dy) > PixelGoldenThresholds.GeometryPixels)
            {
                list.Add($"{side} cell geometry: best shift {shift} exceeds {PixelGoldenThresholds.GeometryPixels} px");
            }
        }

        Cell("left", m.LeftPsnr, m.LeftSsim, m.LeftMae, m.LeftShift);
        Cell("right", m.RightPsnr, m.RightSsim, m.RightMae, m.RightShift);
        if (m.BorderMaxAbs != 0)
        {
            list.Add($"selection/unselected borders differ from WPF (max channel delta {m.BorderMaxAbs}, must be byte-identical)");
        }

        if (m.PanelFlatMaxAbs != 0)
        {
            list.Add($"panel canvas/gutters/empty cell differ from WPF: {m.PanelFlatDiffering} px, max delta {m.PanelFlatMaxAbs} (must be byte-identical)");
        }

        if (m.RingPsnr < fit.ImagePsnr)
        {
            list.Add($"main picture ring PSNR {m.RingPsnr:F2} < {fit.ImagePsnr}");
        }

        return list;
    }
}

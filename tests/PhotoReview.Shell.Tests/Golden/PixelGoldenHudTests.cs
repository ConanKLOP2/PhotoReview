using System.Globalization;
using PhotoReview.Shell.Rendering;
using PhotoReview.TestSupport;
using PhotoReview.TestSupport.PixelGolden;
using Xunit.Abstractions;

namespace PhotoReview.Shell.Tests.Golden;

/// <summary>
/// WP-28 (G-PIX, HUD on/off): the overlay panels of the shell against the WPF window, from pixels. Text is not compared (ClearType
/// vs DirectWrite differ, 7.3 (2)); instead
/// (1) the frame with the HUD hidden is held to the case's picture bar,
/// (2) the panels - found as the pixels the HUD changes - match the WPF panels' boxes within 2 DIP,
/// (3) outside the panels (inflated by 2 px) the HUD frame equals the WPF HUD frame to the picture bar, so the HUD does not disturb
///     the picture, scroll bars or canvas around it, and
/// (4) a panel's body colour (mean over its box) is within a few levels of WPF's, so the translucent overlay fill, not just its
///     box, is right.
/// DirectWrite with the vi-VN strings of the recorder: <c>Category=Native</c> like the G-OVL shell test (fonts of the machine).
/// </summary>
[Collection("OverlayLocalization")]
[Trait("Category", "Native")]
public sealed class PixelGoldenHudTests(ITestOutputHelper output)
{
    private const int ChangeTolerance = 6;
    private const int MeanColourTolerance = 14;

    public static TheoryData<string> HudCases()
    {
        var data = new TheoryData<string>();
        foreach (var c in PixelGoldenCatalog.All.Where(c => c.Hud))
        {
            data.Add(c.Name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(HudCases))]
    public void HudFrame_MatchesTheWpfPanelsByBoxAndTheSurroundingsByPixel(string caseName)
    {
        using var vietnamese = TestLocalization.Use(TestLocalization.Vietnamese);
        using var text = new DWriteTextRenderer("vi-VN", 512);
        var withHud = PixelGoldenCatalog.Get(caseName);
        var withoutHud = PixelGoldenCatalog.WithoutHud(withHud);
        var shellOn = PixelGoldenShellRenderer.Render(withHud, hudText: text);
        var shellOff = PixelGoldenShellRenderer.Render(withoutHud);
        var goldenOn = PixelGoldenTests.ReadGolden(withHud.Name);
        var goldenOff = PixelGoldenTests.ReadGolden(withoutHud.Name);

        // (1) the picture under the HUD.
        PixelGoldenTests.Verify(withoutHud, shellOff, output);

        // (2) panel boxes.
        var expected = PixelMetrics.ChangedRegions(goldenOff, goldenOn, ChangeTolerance);
        var actual = PixelMetrics.ChangedRegions(shellOff.Frame, shellOn.Frame, ChangeTolerance);
        output.WriteLine("WPF panels  : " + string.Join(" ", expected));
        output.WriteLine("shell panels: " + string.Join(" ", actual));
        Assert.True(expected.Count >= 4, $"The WPF HUD has {expected.Count} panels (toolbar, status, folder, zoom, badge expected).");
        Assert.Equal(expected.Count, actual.Count);
        var tolerance = (int)Math.Ceiling(PixelGoldenThresholds.PanelBoxPixels * withHud.Dpi);
        var worst = 0;
        for (var i = 0; i < expected.Count; i++)
        {
            var delta = Math.Max(
                Math.Max(Math.Abs(expected[i].X - actual[i].X), Math.Abs(expected[i].Y - actual[i].Y)),
                Math.Max(Math.Abs(expected[i].Right - actual[i].Right), Math.Abs(expected[i].Bottom - actual[i].Bottom)));
            worst = Math.Max(worst, delta);
            Assert.True(delta <= tolerance, $"panel {i}: WPF {expected[i]} vs shell {actual[i]} (delta {delta} px > {tolerance})");
        }

        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{caseName}: {expected.Count} panels, worst box delta {worst} px (limit {tolerance})"));

        // (3) outside the panels.
        var panels = expected.Concat(actual).ToList();
        var maskedGolden = Mask(goldenOn, panels);
        var maskedShell = Mask(shellOn.Frame, panels);
        var all = new FrameRect(0, 0, goldenOn.Width, goldenOn.Height);
        var bar = PixelGoldenThresholds.For(withHud.Expectation);
        var maxOutside = PixelMetrics.MaxAbsoluteError(maskedGolden, maskedShell, all);
        var psnrOutside = PixelMetrics.Psnr(maskedGolden, maskedShell, all);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"outside the panels: PSNR {psnrOutside:F1} dB, max delta {maxOutside}"));
        if (bar.MaxChannelDelta == 0)
        {
            // Whole-pixel 1:1: the only differences are the rounded scroll bar thumb ends.
            Assert.True(psnrOutside >= 50.0, $"outside the panels PSNR {psnrOutside:F1} dB < 50");
        }
        else
        {
            Assert.True(psnrOutside >= bar.FramePsnr, $"outside the panels PSNR {psnrOutside:F1} dB < {bar.FramePsnr}");
        }

        // (4) panel body colour.
        for (var i = 0; i < expected.Count; i++)
        {
            var (wb, wg, wr) = Mean(goldenOn, expected[i]);
            var (sb, sg, sr) = Mean(shellOn.Frame, expected[i]);
            var delta = Math.Max(Math.Abs(wb - sb), Math.Max(Math.Abs(wg - sg), Math.Abs(wr - sr)));
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"panel {i} mean BGR WPF ({wb:F0},{wg:F0},{wr:F0}) shell ({sb:F0},{sg:F0},{sr:F0}) delta {delta:F1}"));
            Assert.True(delta <= MeanColourTolerance, $"panel {i} body colour differs by {delta:F1} levels");
        }
    }

    private static PixelFrame Mask(PixelFrame frame, IReadOnlyList<FrameRect> panels)
    {
        var data = (byte[])frame.Bgra.Clone();
        foreach (var panel in panels)
        {
            var box = panel.Inflate(2).Intersect(new FrameRect(0, 0, frame.Width, frame.Height));
            for (var y = box.Y; y < box.Bottom; y++)
            {
                for (var x = box.X; x < box.Right; x++)
                {
                    var i = ((y * frame.Width) + x) * 4;
                    data[i] = 0;
                    data[i + 1] = 0;
                    data[i + 2] = 0;
                }
            }
        }

        return new PixelFrame(frame.Width, frame.Height, data);
    }

    private static (double B, double G, double R) Mean(PixelFrame frame, FrameRect box)
    {
        double b = 0, g = 0, r = 0;
        var count = 0;
        for (var y = box.Y; y < box.Bottom; y++)
        {
            for (var x = box.X; x < box.Right; x++)
            {
                var i = ((y * frame.Width) + x) * 4;
                b += frame.Bgra[i];
                g += frame.Bgra[i + 1];
                r += frame.Bgra[i + 2];
                count++;
            }
        }

        return count == 0 ? (0, 0, 0) : (b / count, g / count, r / count);
    }
}

using System.IO;
using PhotoReview.TestSupport;
using PhotoReview.TestSupport.Golden;
using PhotoReview.TestSupport.PixelGolden;

namespace PhotoReview.Integration.Tests.Golden;

/// <summary>
/// WP-28b: records the G-PIX extension frames from the real WPF <c>MainWindow</c> (never shown), the same way
/// <see cref="PixelGoldenRecorder"/> does: the F11/DPI and RAW-preview-size cases are ordinary frames (the window at the fake
/// screen's client size / DPI, a bitmap of another pixel size under the layout of the original); the compare cases lay the real
/// <c>ComparePanel</c> (two cells, <c>CompareBorderBrushConverter</c> borders) over a Fit main picture by setting the same
/// <c>CompareViewModel</c> state a comparison load sets.
/// </summary>
internal static class PixelGoldenExtRecorder
{
    internal static IReadOnlyList<string> Names => [.. PixelGoldenExtCatalog.GoldenNames];

    internal static async Task<IReadOnlyDictionary<string, PixelFrame>> RenderAllAsync()
    {
        var frames = new Dictionary<string, PixelFrame>(StringComparer.Ordinal);
        using var vietnamese = TestLocalization.Use(TestLocalization.Vietnamese);
        await GoldenWpfHost.RunEachAsync(PixelGoldenExtCatalog.Frames,
            async (view, c) => frames[c.Name] = await PixelGoldenRecorder.RenderAsync(view, c));
        await GoldenWpfHost.RunEachAsync(PixelGoldenExtCatalog.Compare,
            async (view, c) => frames[c.Name] = await RenderCompareAsync(view, c));
        return frames;
    }

    internal static async Task<PixelFrame> RenderCompareAsync(GoldenWpfView view, PixelGoldenCompareCase c)
    {
        var main = c.MainCase;
        await PixelGoldenRecorder.PrepareAsync(view, main);
        var compare = view.Window.ViewModel.Compare;
        compare.LeftPath = c.Left is null ? null : "left.jpg";
        compare.RightPath = c.Right is null ? null : "right.jpg";
        compare.LeftImage = c.Left is null ? null : PixelGoldenRecorder.CreateBitmap(AsCase(c.Left));
        compare.RightImage = c.Right is null ? null : PixelGoldenRecorder.CreateBitmap(AsCase(c.Right));
        compare.SelectedPath = c.Selected switch
        {
            PixelGoldenCompareSelection.Left => compare.LeftPath,
            PixelGoldenCompareSelection.Right => compare.RightPath,
            _ => null,
        };
        compare.IsVisible = true;
        view.Relayout();
        await view.SettleAsync();
        return PixelGoldenRecorder.Capture(view, main);
    }

    private static PixelGoldenCase AsCase(PixelGoldenCompareSide side) =>
        new("side", side.Scene, side.Width, side.Height, 1, 1, 1.0, 0, 0, 0, false, side.ExifOrientation, false, PixelGoldenExpectation.Fit);
}

/// <summary>
/// WP-28b: writes the extension frames (<c>tests/Fixtures/golden/pixel/{f11,rawsize,cmp}-*.png</c>) from the WPF build. Opt-in
/// (PHOTOREVIEW_GOLDEN_RECORD=1), run by <c>tools/diag/record-golden.ps1</c> (its filter matches the class name suffix).
/// </summary>
[Trait("Category", "Manual")]
[Trait("Category", "UI")]
[Collection("GlobalState")]
public sealed class PixelExtGoldenRecordTests
{
    [Fact]
    public async Task RecordAll_WritesTheExtensionPixelGoldenFromTheWpfBuild()
    {
        Assert.True(GoldenFile.IsRecordingEnabled,
            $"Recording rewrites tests/Fixtures/golden/pixel. Set {GoldenFile.RecordEnvironmentVariable}=1 on purpose (tools/diag/record-golden.ps1).");

        var frames = await PixelGoldenExtRecorder.RenderAllAsync();
        Assert.Equal(PixelGoldenExtRecorder.Names.Count, frames.Count);
        foreach (var name in PixelGoldenExtRecorder.Names)
        {
            PngFile.Write(PixelGoldenFiles.PathOf(name), frames[name]);
        }
    }
}

/// <summary>
/// WP-28b: the committed extension frames are true of the WPF build in the tree (as <c>PixelGoldenWpfConformanceTests</c> for the
/// base frames): each is rendered again on the real <c>MainWindow</c> and must match its PNG within renderer noise.
/// </summary>
[Trait("Category", "UI")]
[Collection("GlobalState")]
public sealed class PixelGoldenExtWpfConformanceTests
{
    private const double MinimumPsnr = 42.0;

    [Fact]
    public async Task CommittedFrames_ReplayOnTheWpfMainWindow()
    {
        var rendered = await PixelGoldenExtRecorder.RenderAllAsync();
        var failures = new List<string>();
        foreach (var name in PixelGoldenExtRecorder.Names)
        {
            var path = PixelGoldenFiles.PathOf(name);
            if (!File.Exists(path))
            {
                failures.Add($"{name}: golden file missing ({path})");
                continue;
            }

            var golden = PngFile.Read(path);
            var actual = rendered[name];
            if (golden.Width != actual.Width || golden.Height != actual.Height)
            {
                failures.Add($"{name}: size {actual.Width}x{actual.Height}, golden {golden.Width}x{golden.Height}");
                continue;
            }

            var psnr = PixelMetrics.Psnr(golden, actual, new FrameRect(0, 0, golden.Width, golden.Height));
            if (psnr < MinimumPsnr)
            {
                var diff = PixelDiffReport.Write(PixelGoldenFiles.ReportDirectory(), "wpf-" + name, golden, actual);
                failures.Add($"{name}: PSNR {psnr:F1} dB < {MinimumPsnr} (diff: {diff})");
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }
}

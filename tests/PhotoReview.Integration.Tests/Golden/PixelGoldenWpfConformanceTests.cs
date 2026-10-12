using System.IO;
using PhotoReview.TestSupport.PixelGolden;

namespace PhotoReview.Integration.Tests.Golden;

/// <summary>
/// WP-28: the committed G-PIX frames (<c>tests/Fixtures/golden/pixel/*.png</c>) are true of the WPF build in the tree. Each case is
/// rendered again on the real WPF content tree and compared with its PNG; a change of the WPF look (scaling mode, scroll bar template,
/// overlay style, layout) turns this red until the golden is re-recorded on purpose (<c>tools/diag/record-golden.ps1</c>) and the diff is
/// read. The tolerance only absorbs renderer noise between machines (software rasteriser), not a visible change.
/// </summary>
[Trait("Category", "UI")]
[Collection("GlobalState")]
public sealed class PixelGoldenWpfConformanceTests
{
    private const double MinimumPsnr = 42.0;

    [Fact]
    public void CommittedFiles_AreExactlyTheCatalogCases()
    {
        // WP-28b: the base frames (this recorder) + the extension frames (PixelGoldenExtRecorder); each group has its own 5 MB budget.
        var extension = PixelGoldenExtCatalog.GoldenNames.ToHashSet(StringComparer.Ordinal);
        var expected = PixelGoldenRecorder.RecordedCases.Select(c => c.Name).Concat(extension).Order(StringComparer.Ordinal).ToList();
        var committed = Directory.Exists(PixelGoldenFiles.Directory)
            ? Directory.EnumerateFiles(PixelGoldenFiles.Directory, "*.png").Select(f => Path.GetFileNameWithoutExtension(f)!).Order(StringComparer.Ordinal).ToList()
            : [];

        Assert.Equal(expected, committed);
        Assert.True(expected.Count >= 30, "G-PIX needs >= 30 reference frames.");
        long total = Directory.EnumerateFiles(PixelGoldenFiles.Directory, "*.png")
            .Where(f => !extension.Contains(Path.GetFileNameWithoutExtension(f)!)).Sum(f => new FileInfo(f).Length);
        Assert.True(total <= 5L * 1024 * 1024, $"Pixel golden is {total} bytes; the budget is 5 MB.");
        long added = Directory.EnumerateFiles(PixelGoldenFiles.Directory, "*.png")
            .Where(f => extension.Contains(Path.GetFileNameWithoutExtension(f)!)).Sum(f => new FileInfo(f).Length);
        Assert.True(added <= 5L * 1024 * 1024, $"The G-PIX extension golden is {added} bytes; the budget is 5 MB.");
    }

    [Fact]
    public async Task CommittedFrames_ReplayOnTheWpfMainWindow()
    {
        var rendered = await PixelGoldenRecorder.RenderAllAsync(PixelGoldenRecorder.RecordedCases);
        var failures = new List<string>();
        foreach (var c in PixelGoldenRecorder.RecordedCases)
        {
            var golden = PngFile.Read(PixelGoldenRecorder.PngPath(c));
            var actual = rendered[c.Name];
            if (golden.Width != actual.Width || golden.Height != actual.Height)
            {
                failures.Add($"{c.Name}: size {actual.Width}x{actual.Height}, golden {golden.Width}x{golden.Height}");
                continue;
            }

            var all = new FrameRect(0, 0, golden.Width, golden.Height);
            var psnr = PixelMetrics.Psnr(golden, actual, all);
            if (psnr < MinimumPsnr)
            {
                var diff = PixelDiffReport.Write(PixelGoldenFiles.ReportDirectory(), "wpf-" + c.Name, golden, actual);
                failures.Add($"{c.Name}: PSNR {psnr:F1} dB < {MinimumPsnr} (diff: {diff})");
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }
}

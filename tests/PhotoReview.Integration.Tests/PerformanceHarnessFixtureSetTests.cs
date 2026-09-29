using System.IO;
using PhotoReview.Benchmarking;

namespace PhotoReview.Integration.Tests;

/// <summary>
/// The benchmark fixture composition must stay comparable with earlier reports: the harness measures the
/// historical extension set, not whatever the app's viewer happens to support today (.gif was added there later).
/// </summary>
[Collection("GlobalState")]
public sealed class PerformanceHarnessFixtureSetTests : IDisposable
{
    private static readonly string[] HistoricalExtensions = [".bmp", ".jpeg", ".jpg", ".png", ".tif", ".tiff"];

    private readonly TempRoot _root = new("perf-fixture-set");

    public void Dispose() => _root.Dispose();

    [Fact(DisplayName = "The harness measures only the historical extension set: a .gif in the folder is not part of the fixture")]
    public async Task RunAsync_IgnoresGifAndRawFiles_ButKeepsTheHistoricalExtensions()
    {
        var folder = PerformanceTestHarness.CreateFixture(_root.Path, 2); // two fixture-nnn.png
        File.WriteAllBytes(Path.Combine(folder, "animation.gif"), [0x47, 0x49, 0x46, 0x38, 0x39, 0x61]);
        File.WriteAllBytes(Path.Combine(folder, "shot.cr3"), [1, 2, 3, 4]);
        var decoded = new List<string>();
        PerformanceTestHarness.DecodeObserver = (timed, path) => { if (timed) lock (decoded) decoded.Add(Path.GetFileName(path!)); };
        try
        {
            var report = await PerformanceTestHarness.RunAsync(folder, 10, workers: 1, reportPath: Path.Combine(_root.Path, "report.json"));

            Assert.Equal(2, report.FileCount);
            Assert.DoesNotContain("animation.gif", decoded);
            Assert.DoesNotContain("shot.cr3", decoded);
        }
        finally { PerformanceTestHarness.DecodeObserver = null; }
    }

    [Fact(DisplayName = "The harness's extension set is exactly the historical benchmark set")]
    public void SupportedExtensions_AreTheHistoricalBenchmarkSet()
    {
        Assert.Equal(HistoricalExtensions, PerformanceTestHarness.MeasuredExtensions.OrderBy(e => e, StringComparer.Ordinal).ToArray());
    }
}

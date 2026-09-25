using System.IO;
using PhotoReview.Benchmarking;

namespace PhotoReview.Integration.Tests;

/// <summary>TOOL-02: cold-read timing must not include first-call warm-up cost.</summary>
[Collection("GlobalState")]
public sealed class PerformanceHarnessWarmupTests : IDisposable
{
    private readonly TempRoot _root = new("perf-warmup");

    public void Dispose() => _root.Dispose();

    [Fact(DisplayName = "Cold-read runs exactly one untimed warm-up decode before the first timed sample")]
    public async Task ColdReadRunsExactlyOneUntimedWarmupDecodeBeforeFirstTimedSample()
    {
        var fixture = PerformanceTestHarness.CreateFixture(_root.Path, 3);
        var events = new List<(bool Timed, string? Path)>();
        PerformanceTestHarness.DecodeObserver = (timed, path) => { lock (events) events.Add((timed, path)); };
        try
        {
            await PerformanceTestHarness.RunAsync(fixture, 3, workers: 1,
                reportPath: Path.Combine(_root.Path, "report.json"));
        }
        finally { PerformanceTestHarness.DecodeObserver = null; }

        Assert.Equal([false, true, true, true], events.Select(e => e.Timed).ToArray());
        // R2-A-09: the warm-up must not read a measured file (it would pre-warm the first "cold" sample).
        Assert.Null(events[0].Path);
        Assert.Equal(Directory.EnumerateFiles(fixture).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray(),
            events.Skip(1).Select(e => e.Path).ToArray());
    }

    [Fact(DisplayName = "Default report goes to the temp folder, never into the measured photo folder (R2-F-31)")]
    public async Task DefaultReportIsNotWrittenIntoThePhotoFolder()
    {
        var fixture = PerformanceTestHarness.CreateFixture(_root.Path, 2);
        var reportDir = Path.Combine(_root.Path, "default-report-dir");
        var filesBefore = Directory.EnumerateFileSystemEntries(fixture).Order().ToArray();
        // TEST-01: redirect the "default" location into the owned temp root so the shared %TEMP% report is never touched.
        PerformanceTestHarness.DefaultReportDirectoryOverride = reportDir;
        try
        {
            var expected = PerformanceTestHarness.DefaultReportPath;
            Assert.StartsWith(reportDir, expected, StringComparison.OrdinalIgnoreCase);

            await PerformanceTestHarness.RunAsync(fixture, 2, workers: 1);

            Assert.Equal(filesBefore, Directory.EnumerateFileSystemEntries(fixture).Order().ToArray());
            Assert.True(File.Exists(expected));
            Assert.False(expected.StartsWith(fixture, StringComparison.OrdinalIgnoreCase));
        }
        finally { PerformanceTestHarness.DefaultReportDirectoryOverride = null; }
    }

    [Fact(DisplayName = "Without an override the default report path is the shared temp location, outside any photo folder")]
    public void DefaultReportPathWithoutOverrideIsUnderSystemTemp()
    {
        Assert.Null(PerformanceTestHarness.DefaultReportDirectoryOverride);
        Assert.StartsWith(Path.GetTempPath(), PerformanceTestHarness.DefaultReportPath, StringComparison.OrdinalIgnoreCase);
        Assert.False(PerformanceTestHarness.DefaultReportPath.StartsWith(_root.Path, StringComparison.OrdinalIgnoreCase));
    }
}

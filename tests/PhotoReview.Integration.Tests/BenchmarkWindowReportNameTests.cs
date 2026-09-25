using PhotoReview.App;
using PhotoReview.Benchmarking;

namespace PhotoReview.Integration.Tests;

public sealed class BenchmarkWindowReportNameTests
{
    [Fact]
    public void ReportFileName_ReportWithoutPhases_DoesNotThrow()
    {
        var report = new BenchmarkReport("run1", DateTimeOffset.UnixEpoch, "folder", []);
        Assert.Equal("report-run1.json", BenchmarkWindow.ReportFileName(report));
    }

    [Fact]
    public void ReportFileName_UsesFirstPhaseProfileId()
    {
        var phase = new BenchmarkPhaseResult("fast-sequential", BenchmarkWorkload.Sequential, [], BenchmarkResultStatus.Pass, null);
        var report = new BenchmarkReport("run2", DateTimeOffset.UnixEpoch, "folder", [phase]);
        Assert.Equal("fast-sequential-run2.json", BenchmarkWindow.ReportFileName(report));
    }
}

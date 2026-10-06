using System.Globalization;
using System.IO;
using PhotoReview.Benchmark.Cli;
using PhotoReview.PerfAnalysis;

namespace PhotoReview.Integration.Tests;

/// <summary>perf(harness) device-tuning bench: <c>--resource-sample</c> (resources.csv) and the per-navigation <c>navs.csv</c> of <c>--perf-analyze</c>.</summary>
public sealed class TuneBenchSidecarTests
{
    private static string[] Session(params string[] extra) => ["--perf-session", "scenario.json", "C:/photos", "C:/out", .. extra];

    [Fact(DisplayName = "--resource-sample is a value-less flag, off by default, rejected when repeated")]
    public void ResourceSample_Flag()
    {
        Assert.False(PerfSession.ParseArgs(Session()).ResourceSample);
        Assert.True(PerfSession.ParseArgs(Session("--resource-sample", "--alias", "F4")).ResourceSample);
        Assert.Throws<ArgumentException>(() => PerfSession.ParseArgs(Session("--resource-sample", "--resource-sample")));
    }

    [Theory(DisplayName = "ResourceSampler.Percent: busy share of wall x divisor, clamped, 0 for an empty interval")]
    [InlineData(250, 500, 1, 50)]
    [InlineData(500, 500, 4, 25)]
    [InlineData(5000, 500, 1, 100)]
    [InlineData(-5, 500, 1, 0)]
    [InlineData(10, 0, 1, 0)]
    [InlineData(10, 500, 0, 0)]
    public void Percent_Cases(double busy, double wall, int divisor, double expected) =>
        Assert.Equal(expected, ResourceSampler.Percent(busy, wall, divisor), 6);

    [Fact(DisplayName = "ResourceSampler.Rate: events per second, never negative")]
    public void Rate_Cases()
    {
        Assert.Equal(200, ResourceSampler.Rate(100, 500), 6);
        Assert.Equal(0, ResourceSampler.Rate(-3, 500), 6);
        Assert.Equal(0, ResourceSampler.Rate(100, 0), 6);
    }

    [Fact(DisplayName = "ResourceSampler writes the header, a start row and a final row on Dispose, with sane machine values")]
    public void Sampler_WritesStartAndFinalRows()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pr-sampler-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "resources.csv");
            using (var sampler = new ResourceSampler(path, TimeSpan.FromHours(1))) { }   // interval never elapses: only the start + final rows
            var lines = File.ReadAllLines(path);
            Assert.Equal(ResourceSampler.Header, lines[0]);
            Assert.Equal(3, lines.Length);
            var columns = ResourceSampler.Header.Split(',');
            foreach (var line in lines.Skip(1))
            {
                var cells = line.Split(',');
                Assert.Equal(columns.Length, cells.Length);
                Assert.True(double.Parse(cells[2], CultureInfo.InvariantCulture) > 0, "availPhysMb");
                Assert.True(double.Parse(cells[3], CultureInfo.InvariantCulture) > 0, "wsMb");
                Assert.True(double.Parse(cells[4], CultureInfo.InvariantCulture) >= double.Parse(cells[3], CultureInfo.InvariantCulture) - 1, "peakWsMb >= wsMb");
            }
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact(DisplayName = "navs.csv has one row per navigation with invariant-culture latencies and the completeness flag")]
    public void NavSamples_Rows()
    {
        var navs = new List<NavRecord>
        {
            new() { Nav = 2, Incomplete = false, FirstVisualMs = 1.5, FinalVisualMs = 2.25, Kind = "RamHit" },
            new() { Nav = 1, Incomplete = false, FirstVisualMs = 40, FinalVisualMs = 41.125, Kind = "Thumbnail+SourceMiss" },
            new() { Nav = 3, Incomplete = true, Kind = "Unknown" },
        };
        var group = GroupSummary.Build(new GroupKey("S2-next-slow@F4", "Preview", "unknown"), navs);
        var lines = NavSamplesCsv.Build([group]).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(NavSamplesCsv.Header, lines[0]);
        Assert.Equal(
            [
                "S2-next-slow@F4/Preview/unknown/workers=?,1,Thumbnail+SourceMiss,1,40,41.125",
                "S2-next-slow@F4/Preview/unknown/workers=?,2,RamHit,1,1.5,2.25",
                "S2-next-slow@F4/Preview/unknown/workers=?,3,Unknown,0,,",
            ],
            lines.Skip(1));
    }
}

using System.Diagnostics;
using PhotoReview.PerfAnalysis;
using Xunit.Abstractions;

namespace PhotoReview.Integration.Tests;

/// <summary>Manual micro-benchmark for <see cref="PerfAnalyzeNavBuilder.Build"/> on a synthetic long burst run (N navs, one KeyInput each).
/// Run: dotnet test -c Release --filter "Category=Manual&amp;FullyQualifiedName~PerfAnalyzeBuilderBenchmark". Prints the median of several runs.</summary>
[Trait("Category", "Manual")]
public sealed class PerfAnalyzeBuilderBenchmark(ITestOutputHelper output)
{
    private static PerfCsvFile Synthetic(int navs)
    {
        var rows = new List<PerfRow>(navs * 3);
        for (var n = 1; n <= navs; n++)
        {
            var q = n * 10_000L; // 1 MHz -> 10 ms apart
            rows.Add(new PerfRow(0, q, 1, "KeyInput", "", "", "1.5", "", "", "", ""));
            rows.Add(new PerfRow(0, q + 2_000, 1, "ShowStart", n.ToString(System.Globalization.CultureInfo.InvariantCulture), "", n.ToString(System.Globalization.CultureInfo.InvariantCulture), "", "", "", "Preview"));
            rows.Add(new PerfRow(0, q + 5_000, 1, "Presented", n.ToString(System.Globalization.CultureInfo.InvariantCulture), "", "1", "", "", "", "final"));
        }
        return new PerfCsvFile
        {
            Path = "synthetic", CommitVersion = "x", DiagFlags = new Dictionary<string, string>(),
            QpcFrequency = 1_000_000, DroppedRows = 0, Rows = rows,
        };
    }

    [Theory]
    [InlineData(2_000)]
    [InlineData(10_000)]
    public void BuildTimings(int navs)
    {
        var file = Synthetic(navs);
        PerfAnalyzeNavBuilder.Build(file); // warm-up
        var samples = new List<double>();
        for (var i = 0; i < 7; i++)
        {
            var t = Stopwatch.GetTimestamp();
            var analysis = PerfAnalyzeNavBuilder.Build(file);
            samples.Add(Stopwatch.GetElapsedTime(t).TotalMilliseconds);
            Assert.Equal(navs, analysis.Navs.Count);
            Assert.All(analysis.Navs, n => Assert.NotNull(n.TInputMs));
        }
        samples.Sort();
        output.WriteLine($"navs={navs} median={samples[samples.Count / 2]:F1} ms min={samples[0]:F1} max={samples[^1]:F1}");
    }
}

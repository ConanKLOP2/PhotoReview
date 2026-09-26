using System.Globalization;
using PhotoReview.Benchmark.Cli;

namespace PhotoReview.Integration.Tests;

/// <summary>The benchmark CLI writes summary.md files that are read by tools and humans across machines: decimals must not
/// follow the user's locale (app ships Vietnamese, "1,25x"), and a missing Wpf baseline must not read as "1.00x".</summary>
public sealed class BenchmarkReportFormattingTests
{
    private static void UnderVietnameseCulture(Action body)
    {
        var previous = CultureInfo.CurrentCulture;
        var vi = CultureInfo.GetCultureInfo("vi-VN");
        Assert.Equal(",", vi.NumberFormat.NumberDecimalSeparator); // guard: the check below is meaningless on an invariant-globalization host
        try { CultureInfo.CurrentCulture = vi; body(); }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    private static DecoderBenchmark.GroupStatistics Group(string backend, double p50, double speedup) => new()
    {
        Backend = backend, TargetWidth = 1920, TotalRuns = 4, SuccessCount = 4,
        P50Ms = p50, P95Ms = p50 * 1.5, MeanMs = p50, MaxMs = p50 * 2, ThroughputMegapixelsPerSec = 12.5,
        AvgAllocatedBytes = 1536, SpeedupVsWpf = speedup,
    };

    private static DecoderBenchmark.BenchmarkSummary Summary(params DecoderBenchmark.GroupStatistics[] groups) => new()
    {
        TestedWidths = [1920], Groups = [.. groups], TotalRuns = 8,
    };

    [Fact(DisplayName = "Decoder benchmark summary.md uses invariant decimals under a vi-VN culture")]
    public void DecoderMarkdown_IsCultureInvariant()
    {
        string md = "";
        UnderVietnameseCulture(() => md = DecoderBenchmark.GenerateMarkdownReport(Summary(Group("Wpf", 20, 1.0), Group("TurboJpeg", 8, 2.5))));

        Assert.Contains("2.50x", md, StringComparison.Ordinal);
        Assert.Contains("1.5 KB", md, StringComparison.Ordinal);
        Assert.DoesNotContain("2,50", md, StringComparison.Ordinal);
        Assert.DoesNotContain("1,5 KB", md, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Decoder benchmark shows n/a, not 1.00x, when a width has no Wpf baseline")]
    public void DecoderMarkdown_MissingBaselineIsNotAvailable()
    {
        var md = DecoderBenchmark.GenerateMarkdownReport(Summary(Group("WicDirect", 10, 0), Group("TurboJpeg", 5, 0)));

        Assert.Contains("**n/a**", md, StringComparison.Ordinal);
        Assert.DoesNotContain("1.00x", md, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Decoder benchmark 'improvement over X' is computed against X, not against Wpf")]
    public void DecoderMarkdown_ImprovementIsRelativeToTheBaselineActuallyUsed()
    {
        // No Wpf group: baseline falls back to the slowest backend (WicDirect 10 ms); TurboJpeg 5 ms is 2.00x faster.
        var md = DecoderBenchmark.GenerateMarkdownReport(Summary(Group("WicDirect", 10, 0), Group("TurboJpeg", 5, 0)));

        Assert.Contains("Improvement over WicDirect:** 50.0% faster (2.00x speedup)", md, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "io-decode-split summary.md uses invariant decimals under a vi-VN culture")]
    public void IoDecodeSplitSummary_IsCultureInvariant()
    {
        static IoDecodeSplit.Measurement M(double v) => new(v + 0.25, v + 0.25, v + 0.25, 1000);
        var result = new IoDecodeSplit.FileResult { Index = 0, SourceBytes = 3_500_000, OriginalWidth = 4000, OriginalHeight = 3000 };
        result.Read = result.HeaderOnly = result.PngEncode = result.PngDecode = result.Decode2560Mem = M(1);
        result.DecodeFromMem[0] = M(2); result.DecodeFromMem[1920] = M(2);
        result.DecodeFromFile[0] = M(3); result.DecodeFromFile[1920] = M(3);

        string md = "";
        UnderVietnameseCulture(() => md = IoDecodeSplit.BuildSummary([result], [0, 1920], 1, 60));

        Assert.Contains("| 1920 | 2.2 | 2.2 | 2.2 | 3.2 | 3.2 | 3.2 |", md, StringComparison.Ordinal);
        Assert.Contains("| read | 1.2 | 1.2 | 1.2 |", md, StringComparison.Ordinal);
        Assert.DoesNotContain("2,2", md, StringComparison.Ordinal);
    }

    [Theory(DisplayName = "--perf-analyze: a dangling --rules or an unknown option is an error, not a silent default")]
    [InlineData("--rules")]
    [InlineData("--rulez", "x.json")]
    [InlineData("--rules", "")]
    public void PerfAnalyzeArguments_BadRulesAreRejected(params string[] extra)
    {
        string[] args = ["--perf-analyze", "runs", .. extra];
        Assert.Throws<ArgumentException>(() => BenchmarkCliArguments.ParsePerfAnalyzeRules(args));
    }

    [Fact(DisplayName = "--perf-analyze parses an optional --rules path")]
    public void PerfAnalyzeArguments_RulesPathIsParsed()
    {
        Assert.Null(BenchmarkCliArguments.ParsePerfAnalyzeRules(["--perf-analyze", "runs"]));
        Assert.Equal("r.json", BenchmarkCliArguments.ParsePerfAnalyzeRules(["--perf-analyze", "runs", "--rules", "r.json"]));
    }
}

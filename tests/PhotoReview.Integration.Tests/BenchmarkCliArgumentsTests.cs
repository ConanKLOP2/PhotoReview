using PhotoReview.Benchmark.Cli;
using PhotoReview.Benchmarking;

namespace PhotoReview.Integration.Tests;

/// <summary>Argument edge cases of the benchmark CLI (free-form lists, culture, empty and negative values).</summary>
public sealed class BenchmarkCliArgumentsTests
{
    [Fact(DisplayName = "--benchmark without a profile list defaults to recommended-auto")]
    public void Benchmark_DefaultsToRecommendedAuto()
    {
        var profiles = BenchmarkCliArguments.ResolveProfiles("--benchmark", ["--benchmark", "C:/photos"]);
        Assert.Equal(["recommended-auto"], profiles.Select(p => p.Id));
    }

    [Theory(DisplayName = "--benchmark profile lists: trimming, case-insensitivity, empty segments and order")]
    [InlineData("fast-balanced", new[] { "fast-balanced" })]
    [InlineData(" fast-balanced , Instant-Review ", new[] { "fast-balanced", "instant-review" })]
    [InlineData("fast-balanced,,logging-on,", new[] { "fast-balanced", "logging-on" })]
    [InlineData("logging-on,logging-on", new[] { "logging-on", "logging-on" })]
    public void Benchmark_ProfileListParsing(string list, string[] expected)
    {
        var profiles = BenchmarkCliArguments.ResolveProfiles("--benchmark", ["--benchmark", "C:/photos", list]);
        Assert.Equal(expected, profiles.Select(p => p.Id));
    }

    [Theory(DisplayName = "--benchmark with an empty profile list is an error instead of a silent no-op batch")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(",")]
    [InlineData(" , ,")]
    public void Benchmark_EmptyProfileListIsRejected(string list)
    {
        var ex = Assert.Throws<ArgumentException>(() => BenchmarkCliArguments.ResolveProfiles("--benchmark", ["--benchmark", "C:/photos", list]));
        Assert.Contains("No benchmark profile", ex.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "--benchmark with an unknown profile names it in the error")]
    public void Benchmark_UnknownProfileIsNamed()
    {
        var ex = Assert.Throws<ArgumentException>(() => BenchmarkCliArguments.ResolveProfiles("--benchmark", ["--benchmark", "C:/photos", "fast-balanced,nope"]));
        Assert.Contains("nope", ex.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "--benchmark-all runs exactly the runnable profiles, --benchmark-actions exactly the file-action ones")]
    public void BatchModes_SelectProfiles()
    {
        Assert.Equal(BenchmarkProfiles.Runnable.Select(p => p.Id), BenchmarkCliArguments.ResolveProfiles("--benchmark-all", ["--benchmark-all", "C:/photos"]).Select(p => p.Id));
        var actions = BenchmarkCliArguments.ResolveProfiles("--benchmark-actions", ["--benchmark-actions", "C:/photos"]);
        Assert.NotEmpty(actions);
        Assert.All(actions, p => Assert.Equal(BenchmarkWorkload.FileAction, p.Workload));
    }

    [Fact(DisplayName = "A non-benchmark mode is rejected")]
    public void UnknownMode_IsRejected() =>
        Assert.Throws<ArgumentException>(() => BenchmarkCliArguments.ResolveProfiles("--nope", ["--nope", "x"]));

    [Theory(DisplayName = "Output folder: only the batch modes take args[2]; --benchmark's args[2] is the profile list")]
    [InlineData("--benchmark", "C:/out", null)]
    [InlineData("--benchmark-all", "C:/out", "C:/out")]
    [InlineData("--benchmark-actions", "C:/out", "C:/out")]
    [InlineData("--benchmark-all", " ", null)]
    public void Output_Resolution(string mode, string third, string? expected) =>
        Assert.Equal(expected, BenchmarkCliArguments.ResolveOutput(mode, [mode, "C:/photos", third]));

    [Fact(DisplayName = "Output folder is absent when no third argument is given")]
    public void Output_AbsentWhenNoThirdArgument() =>
        Assert.Null(BenchmarkCliArguments.ResolveOutput("--benchmark-all", ["--benchmark-all", "C:/photos"]));

    [Theory(DisplayName = "Widths parse as invariant non-negative integers")]
    [InlineData("0,1920,2560", new[] { 0, 1920, 2560 })]
    [InlineData(" 800 , 1600 ", new[] { 800, 1600 })]
    [InlineData("1920,,3840,", new[] { 1920, 3840 })]
    public void Widths_Valid(string text, int[] expected) => Assert.Equal(expected, BenchmarkCliArguments.ParseWidths(text));

    [Theory(DisplayName = "Widths reject empty, negative, decimal, thousands-separated, signed and overflowing input with an ArgumentException (not FormatException)")]
    [InlineData("")]
    [InlineData(",")]
    [InlineData("-1")]
    [InlineData("+5")]
    [InlineData("19.20")]
    [InlineData("1,920x")]
    [InlineData("abc")]
    [InlineData("99999999999")]
    [InlineData("\u0661\u0662")] // Arabic-Indic digits
    public void Widths_Invalid(string text) => Assert.Throws<ArgumentException>(() => BenchmarkCliArguments.ParseWidths(text));

    [Theory(DisplayName = "Positive integer options")]
    [InlineData("1", 1)]
    [InlineData("60", 60)]
    [InlineData("2147483647", int.MaxValue)]
    public void PositiveInt_Valid(string text, int expected) => Assert.Equal(expected, BenchmarkCliArguments.ParsePositiveInt(text, "n"));

    [Theory(DisplayName = "Positive integer options reject zero, negatives and garbage")]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("")]
    [InlineData("8.5")]
    [InlineData("2147483648")]
    public void PositiveInt_Invalid(string text)
    {
        var ex = Assert.Throws<ArgumentException>(() => BenchmarkCliArguments.ParsePositiveInt(text, "worker count"));
        Assert.Contains("worker count", ex.Message, StringComparison.Ordinal);
    }

    [Theory(DisplayName = "--perf-session --source-bytes-cache on|off overrides AppSettings.UseSourceBytesCache in-memory (AR15c)")]
    [InlineData("on", true)]
    [InlineData("On", true)]
    [InlineData("off", false)]
    [InlineData("OFF", false)]
    public void PerfSession_SourceBytesCache_Valid(string value, bool expected)
    {
        var options = PerfSession.ParseArgs(
            ["--perf-session", "scenario.json", "C:/photos", "C:/out", "--source-bytes-cache", value]);
        Assert.Equal(expected, options.SourceBytesCache);
    }

    [Fact(DisplayName = "--perf-session without --source-bytes-cache leaves the override unset (config.json wins)")]
    public void PerfSession_SourceBytesCache_AbsentByDefault()
    {
        var options = PerfSession.ParseArgs(["--perf-session", "scenario.json", "C:/photos", "C:/out"]);
        Assert.Null(options.SourceBytesCache);
    }

    [Theory(DisplayName = "--perf-session --source-bytes-cache rejects anything other than on|off")]
    [InlineData("true")]
    [InlineData("1")]
    [InlineData("")]
    [InlineData("yes")]
    public void PerfSession_SourceBytesCache_Invalid(string value)
    {
        var ex = Assert.Throws<ArgumentException>(() => PerfSession.ParseArgs(
            ["--perf-session", "scenario.json", "C:/photos", "C:/out", "--source-bytes-cache", value]));
        Assert.Contains("source-bytes-cache", ex.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Parsing does not depend on the current culture (Vietnamese)")]
    public void Parsing_IsCultureInvariant()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("vi-VN");
            Assert.Equal([1920, 0], BenchmarkCliArguments.ParseWidths("1920,0"));
            Assert.Equal(12, BenchmarkCliArguments.ParsePositiveInt("12", "n"));
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = previous; }
    }
}

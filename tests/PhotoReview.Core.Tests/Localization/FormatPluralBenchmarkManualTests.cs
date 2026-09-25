using System.Diagnostics;
using System.Globalization;
using PhotoReview.Core.Localization;
using Xunit.Abstractions;

namespace PhotoReview.Core.Tests.Localization;

/// <summary>
/// Manual micro-benchmark: <see cref="Localizer.FormatPlural"/> versus the original implementation (two concatenations plus
/// two dictionary lookups, reproduced here as the reference). Not part of the default gate.
/// Run: dotnet test tests/PhotoReview.Core.Tests -c Release --filter "Category=Manual&amp;FullyQualifiedName~FormatPluralBenchmark" --logger "console;verbosity=detailed"
/// </summary>
[Trait("Category", "Manual")]
public sealed class FormatPluralBenchmarkManualTests(ITestOutputHelper output)
{
    private static string Legacy(Localizer l, string baseKey, long count, ReadOnlySpan<LocArg> args)
    {
        var key = l.Plural == PluralRule.OneOther && count == 1 ? baseKey + ".one" : baseKey + ".other";
        if (!l.Contains(key)) key = baseKey + ".other";
        return l.Format(key, args);
    }

    [Fact(DisplayName = "Manual: FormatPlural vs original implementation")]
    public void Benchmark()
    {
        var localizer = Localizer.Create(LocTestCatalogs.English, []);
        var arg = new LocArg("count", 3);
        const int Iterations = 200_000;
        long sink = 0;

        double Run(bool legacy, long count)
        {
            var sw = Stopwatch.StartNew();
            for (var i = 0; i < Iterations; i++)
                sink += (legacy ? Legacy(localizer, "files", count, [arg]) : localizer.FormatPlural("files", count, arg)).Length;
            sw.Stop();
            return sw.Elapsed.TotalMilliseconds * 1_000_000 / Iterations; // ns per call
        }

        foreach (var count in new long[] { 1, 3 })
        {
            Run(true, count); Run(false, count); // warm-up
            var legacy = new List<double>(); var current = new List<double>();
            for (var r = 0; r < 11; r++) { legacy.Add(Run(true, count)); current.Add(Run(false, count)); }
            legacy.Sort(); current.Sort();
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"count={count}: original median {legacy[5]:F1} ns/call, current median {current[5]:F1} ns/call ({sink & 1})"));
        }

        var a0 = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++) sink += Legacy(localizer, "files", 3, [arg]).Length;
        var a1 = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++) sink += localizer.FormatPlural("files", 3, arg).Length;
        var a2 = GC.GetAllocatedBytesForCurrentThread();
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"allocated per call: original {(a1 - a0) / 1000} B, current {(a2 - a1) / 1000} B ({sink & 1})"));
    }
}

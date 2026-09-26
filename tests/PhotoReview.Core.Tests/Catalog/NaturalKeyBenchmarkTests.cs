using System.Diagnostics;
using PhotoReview.Core.Catalog;
using Xunit.Abstractions;

namespace PhotoReview.Core.Tests.Catalog;

/// <summary>
/// Manual micro-benchmark (never in the default gate): old StringBuilder-per-key implementation vs the shipped
/// <see cref="ManagedNaturalComparer"/>. Run: dotnet test tests/PhotoReview.Core.Tests -c Release --filter "FullyQualifiedName~NaturalKeyBenchmark".
/// </summary>
[Trait("Category", "Manual")]
public sealed class NaturalKeyBenchmarkTests(ITestOutputHelper output)
{
    private const int Runs = 7;

    private static string[] Names(int count)
    {
        var r = new Random(42);
        var names = new string[count];
        for (var i = 0; i < count; i++)
            names[i] = $"[XiuRen] No.{r.Next(1, 9999)} Model {r.Next(1, 300):D4} ({r.Next(1, 40)}).jpg";
        return names;
    }

    /// <summary>Warms both bodies (tiered JIT), then alternates old/new runs so drift and GC state hit both equally.</summary>
    private void Compare(string what, Action oldBody, Action newBody)
    {
        for (var i = 0; i < 6; i++) { oldBody(); newBody(); }
        var oldTimes = new double[Runs];
        var newTimes = new double[Runs];
        long oldBytes = 0, newBytes = 0;
        for (var i = 0; i < Runs; i++)
        {
            (oldTimes[i], oldBytes) = Once(oldBody);
            (newTimes[i], newBytes) = Once(newBody);
        }
        Array.Sort(oldTimes);
        Array.Sort(newTimes);
        var o = oldTimes[Runs / 2];
        var n = newTimes[Runs / 2];
        output.WriteLine($"{what}: old {o:F1} ms / {oldBytes / 1024} KiB  ->  new {n:F1} ms / {newBytes / 1024} KiB  (time x{o / n:F2}, alloc x{(double)oldBytes / Math.Max(1, newBytes):F2})");
    }

    private static (double Ms, long Bytes) Once(Action body)
    {
        GC.Collect();
        var before = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        body();
        sw.Stop();
        return (sw.Elapsed.TotalMilliseconds, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact(DisplayName = "Benchmark: BuildNaturalKey, Compare and a full sort of 50 000 names, old vs new")]
    public void Benchmark()
    {
        var names = Names(50_000);
        var sink = 0;

        Compare("BuildNaturalKey x50k",
            () => { foreach (var n in names) sink += NaturalKeyReference.BuildNaturalKey(n).Length; },
            () => { foreach (var n in names) sink += ManagedNaturalComparer.BuildNaturalKey(n).Length; });

        Compare("Compare x500k pairs",
            () => { for (var i = 0; i < 500_000; i++) sink += NaturalKeyReference.Compare(names[i % 50_000], names[(i * 7 + 1) % 50_000]); },
            () => { for (var i = 0; i < 500_000; i++) sink += ManagedNaturalComparer.Instance.Compare(names[i % 50_000], names[(i * 7 + 1) % 50_000]); });

        Compare("Array.Sort 50k names",
            () => { var a = (string[])names.Clone(); Array.Sort(a, NaturalKeyReference.Compare); sink += a.Length; },
            () => { var a = (string[])names.Clone(); Array.Sort(a, ManagedNaturalComparer.Instance); sink += a.Length; });

        Assert.NotEqual(int.MinValue, sink);
    }
}

using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace PhotoReview.App.Tests;

/// <summary>
/// Manual micro-benchmark for the list operations RecoveryWindow.Dismiss/RefreshEntries use on a large journal
/// (measures the collection pattern in isolation, not the WPF window): List.RemoveAll(other.Contains) versus a
/// HashSet lookup. Run: dotnet test -c Release --filter "Category=Manual&amp;FullyQualifiedName~RecoveryListOps".
/// </summary>
[Trait("Category", "Manual")]
public sealed class RecoveryListOpsBenchmarkManualTests(ITestOutputHelper output)
{
    private sealed class Row;

    private static double MedianMs(Func<int> action, int iterations)
    {
        action(); // warm-up
        var times = new double[iterations];
        for (var i = 0; i < iterations; i++)
        {
            var sw = Stopwatch.StartNew();
            var result = action();
            sw.Stop();
            GC.KeepAlive(result);
            times[i] = sw.Elapsed.TotalMilliseconds;
        }
        Array.Sort(times);
        return times[iterations / 2];
    }

    [Theory]
    [InlineData(2_000)]
    [InlineData(20_000)]
    public void RemoveAll_ListContainsVersusHashSet(int count)
    {
        var all = Enumerable.Range(0, count).Select(_ => new Row()).ToList();
        var removed = all.Where((_, i) => i % 2 == 0).ToList(); // "Clear selected": half the rows

        var listContains = MedianMs(() => all.ToList().RemoveAll(removed.Contains), 5);
        var hashSet = MedianMs(() =>
        {
            var set = new HashSet<Row>(removed);
            return all.ToList().RemoveAll(set.Contains);
        }, 5);

        output.WriteLine($"n={count}: List.Contains median {listContains:F2} ms, HashSet median {hashSet:F2} ms, speedup x{listContains / hashSet:F1}");
        Assert.True(hashSet <= listContains);
    }
}

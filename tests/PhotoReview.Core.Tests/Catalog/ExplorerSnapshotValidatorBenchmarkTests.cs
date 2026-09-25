using System.Diagnostics;
using PhotoReview.Core.Catalog;
using Xunit.Abstractions;

namespace PhotoReview.Core.Tests.Catalog;

/// <summary>
/// Manual micro-benchmark (never in the default gate): the original validator (<see cref="ExplorerSnapshotValidatorReference"/>) vs the shipped one.
/// Run: dotnet test tests/PhotoReview.Core.Tests -c Release --filter "FullyQualifiedName~ExplorerSnapshotValidatorBenchmark".
/// </summary>
[Trait("Category", "Manual")]
public sealed class ExplorerSnapshotValidatorBenchmarkTests(ITestOutputHelper output)
{
    private const int Runs = 7;

    [Fact(DisplayName = "Benchmark: validating a 50 000 file Explorer snapshot, old vs new")]
    public void Benchmark()
    {
        var folder = @"C:\Xiuren\[[WALLPAPER]";
        var scanned = Enumerable.Range(0, 50_000).Select(i => $@"{folder}\IMG_{i:D6}.jpg").ToList();
        var ordered = new List<string>(scanned);
        ordered.Reverse();
        var snapshot = new ExplorerViewSnapshot(folder, ordered, [], ExplorerGroupState.None, ExplorerOrderStatus.Available, null, DateTime.UtcNow);
        var sink = 0;

        void Old() { Assert.True(ExplorerSnapshotValidatorReference.TryValidate(snapshot, scanned, out var o, out _)); sink += o.Count; }
        void New() { Assert.True(ExplorerSnapshotValidator.TryValidate(snapshot, scanned, out var o, out _)); sink += o.Count; }

        for (var i = 0; i < 4; i++) { Old(); New(); }
        var oldT = new double[Runs]; var newT = new double[Runs];
        long oldB = 0, newB = 0;
        for (var i = 0; i < Runs; i++)
        {
            (oldT[i], oldB) = Once(Old);
            (newT[i], newB) = Once(New);
        }
        Array.Sort(oldT); Array.Sort(newT);
        output.WriteLine($"TryValidate 50k: old median {oldT[Runs / 2]:F1} ms (min {oldT[0]:F1}) / {oldB / 1024} KiB  ->  new median {newT[Runs / 2]:F1} ms (min {newT[0]:F1}) / {newB / 1024} KiB  (median x{oldT[Runs / 2] / newT[Runs / 2]:F2})");
        Assert.True(sink > 0);
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
}

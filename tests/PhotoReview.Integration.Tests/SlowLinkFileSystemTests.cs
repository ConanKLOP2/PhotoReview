using System.Diagnostics;
using PhotoReview.Benchmarking;
using PhotoReview.Core.IO;

namespace PhotoReview.Integration.Tests;

public sealed class SlowLinkFileSystemTests
{
    [Fact(DisplayName = "T1: EnumerateReadableFilesWithStat pays the metadata latency per entry like the sibling overloads")]
    public void EnumerateReadableFilesWithStat_AppliesMetadataLatencyPerEntry()
    {
        using var root = new TempRoot("slowlink");
        root.File("a.jpg", 1);
        root.File("b.jpg", 2);
        var latency = TimeSpan.FromMilliseconds(250);
        var slow = new SlowLinkFileSystem(new PhysicalFileSystem(), latency, bandwidth: null);

        var clock = Stopwatch.StartNew();
        var entries = slow.EnumerateReadableFilesWithStat(root.Path, _ => true, _ => { }).ToList();
        clock.Stop();

        Assert.Equal(2, entries.Count);
        // Two entries, 250 ms each: a lower bound with generous slack for timer granularity (never an upper bound).
        Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(400), $"elapsed {clock.Elapsed.TotalMilliseconds} ms");
    }
}

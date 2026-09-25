using System;
using System.Diagnostics;
using System.IO;
using PhotoReview.Imaging.Decoding;
using PhotoReview.Imaging.Decoding.Wic;
using PhotoReview.Imaging.Tests.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace PhotoReview.Imaging.Tests.Quality;

/// <summary>
/// Manual micro-benchmark for the header-only <c>ReadInfo</c> path: managed bytes allocated per call (a FileStream buffer is
/// allocated on the first read) and median wall time with a warm OS file cache. Run:
/// <c>dotnet test tests/PhotoReview.Imaging.Tests -c Release --filter "FullyQualifiedName~ReadInfoBufferBenchmark" --logger "console;verbosity=detailed"</c>.
/// </summary>
[Trait("Category", "Manual")]
public sealed class ReadInfoBufferBenchmarkTests(ITestOutputHelper output) : IDisposable
{
    private const int Iterations = 400;
    private readonly TempRoot _root = new("readinfo-bench");

    public void Dispose() => _root.Dispose();

    [Fact(DisplayName = "ReadInfo allocation and time per call (WPF and WicDirect)")]
    public void ReadInfoCost()
    {
        var path = FixtureGenerator.GenerateGradientJpeg(Path.Combine(_root.Path, "big.jpg"), 6000, 4000);
        output.WriteLine($"fixture: {new FileInfo(path).Length / 1024} KiB");
        foreach (var (name, decoder) in new (string, IImageDecoder)[] { ("Wpf", new WpfBitmapImageDecoder()), ("WicDirect", new WicDirectDecoder()) })
        {
            for (var i = 0; i < 50; i++) decoder.ReadInfo(path); // warm-up (JIT, file cache)
            var times = new double[Iterations];
            long allocated = 0;
            for (var i = 0; i < Iterations; i++)
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                var t0 = Stopwatch.GetTimestamp();
                decoder.ReadInfo(path);
                times[i] = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                allocated += GC.GetAllocatedBytesForCurrentThread() - before;
            }
            Array.Sort(times);
            output.WriteLine($"{name}: median {times[Iterations / 2]:F3} ms, p90 {times[Iterations * 9 / 10]:F3} ms, allocated {allocated / Iterations} B/call");
        }
    }
}

using System.Globalization;
using System.IO;

namespace PhotoReview.Integration.Tests;

/// <summary>Q-R26: the perf-session GC and page-fault recorders report what really happened in the process.</summary>
[Collection("GlobalState")] // forces GCs; must not overlap tests that measure allocations or timings
public sealed class PerfSessionDiagnosticsTests
{
    private static readonly TimeSpan EventTimeout = TimeSpan.FromSeconds(15);

    [Fact(DisplayName = "The GC recorder captures an induced gen2 GC and writes it with its reason")]
    public async Task GcRecorder_InducedCollection_IsRecordedWithReason()
    {
        using var recorder = new GcEventRecorder();
        recorder.Start();

        // Runtime events reach an in-process listener asynchronously (and the listener may attach only after the
        // first GC), so keep inducing a GC until one is seen instead of waiting a fixed time.
        await Wait.UntilAsync(() =>
        {
            if (recorder.Snapshot().Any(g => g.Gen == 2 && g.Reason == "Induced")) return true;
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
            return false;
        }, "an induced gen2 GC recorded by GcEventRecorder", EventTimeout);

        var path = Path.Combine(Path.GetTempPath(), $"PhotoReview-gc-events-{Guid.NewGuid():N}.csv");
        try
        {
            recorder.WriteCsv(path);
            var lines = File.ReadAllLines(path);
            Assert.Equal("startUtcTicks,endUtcTicks,gc,gen,reason,gcMs,pauseMs", lines[0]);
            var row = lines.Skip(1).Select(l => l.Split(',')).First(c => c[3] == "2" && c[4] == "Induced");
            Assert.True(long.Parse(row[1], CultureInfo.InvariantCulture) >= long.Parse(row[0], CultureInfo.InvariantCulture));
            Assert.True(double.Parse(row[6], CultureInfo.InvariantCulture) >= 0);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact(DisplayName = "The page-fault counter grows when the process touches fresh memory")]
    public void PageFaults_TouchingNewMemory_Increases()
    {
        const int Bytes = 64 * 1024 * 1024;
        var before = ProcessPageFaults.Read();
        Assert.True(before > 0, $"page-fault count unavailable ({before})");

        var buffer = GC.AllocateUninitializedArray<byte>(Bytes, pinned: true);
        for (var i = 0; i < buffer.Length; i += 4096) buffer[i] = 1;

        var after = ProcessPageFaults.Read();
        GC.KeepAlive(buffer);
        Assert.True(after - before >= Bytes / 4096 / 2, $"only {after - before} page faults for {Bytes / 4096} fresh pages");
    }
}

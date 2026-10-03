using System.IO;

namespace PhotoReview.Imaging.Tests.Caching;

/// <summary>A prune requested from inside the exit window (after the last pending check, before the scheduled flag clears) is still serviced.</summary>
[Trait("Category", "HotPath")]
public sealed class DiskCacheStorePruneRaceTests : IDisposable
{
    private readonly TempRoot _root = new("DiskCachePruneRace");

    public void Dispose() => _root.Dispose();

    [Fact(DisplayName = "A SchedulePrune made right before the prune loop clears its scheduled flag still runs a second pass")]
    public async Task SchedulePrune_RequestedInsideExitWindow_IsServiced()
    {
        var dir = _root.Dir("race");
        var late = Path.Combine(dir, "late.pv4");
        var store = new DiskCacheStore(dir, "*.pv4", maxBytes: 10, log: null);
        var hookCalls = 0;
        store.BeforeClearPruneScheduledForTests = () =>
        {
            if (Interlocked.Increment(ref hookCalls) != 1) return;
            // Over quota and noted, so the second pass cannot take the tracked-bytes shortcut.
            File.WriteAllBytes(late, new byte[100]);
            store.NoteWritten(late);
            store.SchedulePrune();
        };

        store.SchedulePrune();

        Assert.True(await store.WaitForPruneAsync(TimeSpan.FromSeconds(10)), "the scheduled flag never cleared (request made in the exit window was dropped)");
        Assert.Equal(2, store.FullScanCount);
        Assert.False(File.Exists(late));
    }
}
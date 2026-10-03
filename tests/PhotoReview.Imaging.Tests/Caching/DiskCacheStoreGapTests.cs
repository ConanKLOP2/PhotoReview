using System.IO;
using PhotoReview.Core.Abstractions;

namespace PhotoReview.Imaging.Tests.Caching;

/// <summary>RV-T46: DeleteStaleTempFiles and the "one extra pass" coalescing contract of SchedulePrune.</summary>
[Trait("Category", "HotPath")]
public sealed class DiskCacheStoreGapTests : IDisposable
{
    private readonly TempRoot _root = new("DiskCacheGap");

    public void Dispose() => _root.Dispose();

    private static string Temp(string dir, string name, TimeSpan age)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, [1, 2, 3]);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - age);
        return path;
    }

    [Fact(DisplayName = "DeleteStaleTempFiles deletes old .tmp files, keeps fresh ones and non-.tmp files")]
    public void DeleteStaleTempFiles_DeletesOnlyOldTempFiles()
    {
        var dir = _root.Dir("temp");
        var old = Temp(dir, "a.png.1.tmp", DiskCacheStore.StaleTempFileAge + TimeSpan.FromMinutes(5));
        var fresh = Temp(dir, "b.png.2.tmp", TimeSpan.FromMinutes(1));
        var oldNotTemp = Temp(dir, "c.pv4", DiskCacheStore.StaleTempFileAge + TimeSpan.FromHours(1));

        var removed = DiskCacheStore.DeleteStaleTempFiles(dir, DateTime.UtcNow, maxFiles: 100);

        Assert.Equal(1, removed);
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(fresh));
        Assert.True(File.Exists(oldNotTemp));
    }

    [Fact(DisplayName = "DeleteStaleTempFiles honours the maxFiles cap")]
    public void DeleteStaleTempFiles_HonoursMaxFiles()
    {
        var dir = _root.Dir("cap");
        for (var i = 0; i < 5; i++) Temp(dir, $"f{i}.tmp", TimeSpan.FromHours(1));

        var removed = DiskCacheStore.DeleteStaleTempFiles(dir, DateTime.UtcNow, maxFiles: 2);

        Assert.Equal(2, removed);
        Assert.Equal(3, Directory.GetFiles(dir, "*.tmp").Length);
    }

    [Fact(DisplayName = "DeleteStaleTempFiles ages against the supplied clock, not the wall clock")]
    public void DeleteStaleTempFiles_UsesTheSuppliedNow()
    {
        var dir = _root.Dir("clock");
        var path = Temp(dir, "a.tmp", TimeSpan.FromMinutes(1));

        Assert.Equal(0, DiskCacheStore.DeleteStaleTempFiles(dir, DateTime.UtcNow, maxFiles: 10));
        Assert.True(File.Exists(path));
        Assert.Equal(1, DiskCacheStore.DeleteStaleTempFiles(dir, DateTime.UtcNow + DiskCacheStore.StaleTempFileAge + TimeSpan.FromMinutes(2), maxFiles: 10));
        Assert.False(File.Exists(path));
    }

    [Fact(DisplayName = "DeleteStaleTempFiles on a missing directory returns 0 without throwing")]
    public void DeleteStaleTempFiles_MissingDirectory_ReturnsZero()
    {
        Assert.Equal(0, DiskCacheStore.DeleteStaleTempFiles(_root.Combine("nope"), DateTime.UtcNow, maxFiles: 10));
    }

    /// <summary>Blocks the first "Delete failed" log call (made from inside a prune pass) until released.</summary>
    private sealed class BlockingLog : ILog, IDisposable
    {
        public void Dispose() { _entered.Dispose(); _release.Dispose(); }
        private readonly ManualResetEventSlim _entered = new();
        private readonly ManualResetEventSlim _release = new();
        public bool Enabled => true;
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? ex = null)
        {
            if (!message.StartsWith("Delete failed", StringComparison.Ordinal)) return;
            _entered.Set();
            _release.Wait(TimeSpan.FromSeconds(10));
        }
        public bool WaitEntered() => _entered.Wait(TimeSpan.FromSeconds(10));
        public void Release() => _release.Set();
    }

    [Fact(DisplayName = "SchedulePrune requests made while a pass is running (however many) run exactly one extra pass")]
    public async Task SchedulePrune_DuringRunningPass_RunsExactlyOneExtraPass()
    {
        var dir = _root.Dir("coalesce");
        var locked = Path.Combine(dir, "locked.pv4");
        File.WriteAllBytes(locked, new byte[100]);
        using var log = new BlockingLog();
        var store = new DiskCacheStore(dir, "*.pv4", maxBytes: 10, log);
        // The over-quota file cannot be deleted while open exclusively: the failed delete logs from inside the pass, where the log blocks.
        using var exclusive = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None);

        store.SchedulePrune();
        Assert.True(log.WaitEntered(), "the first prune pass never started");
        for (var i = 0; i < 5; i++) store.SchedulePrune(); // all coalesce into one pending request
        log.Release();

        Assert.True(await store.WaitForPruneAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(2, store.FullScanCount);
    }
}

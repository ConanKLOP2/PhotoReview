using System.IO;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoReview.Core.Abstractions;

namespace PhotoReview.Imaging.Tests.Caching;

/// <summary>
/// Mutation-testing follow-ups for <see cref="DiskCacheStore"/> (docs/MUTATION-TESTING.md): the access-touch throttle,
/// the prune skip/accounting decisions, quota edges, delete-failure handling and the atomic PNG write. Prune passes are run
/// synchronously through the private <c>RunPrunePass</c> so no test depends on background timing.
/// </summary>
[Trait("Category", "HotPath")]
public sealed class DiskCacheStoreMutationTests : IDisposable
{
    private static readonly DateTime T0 = new(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    private static readonly DateTime Sentinel = new(2010, 6, 7, 8, 9, 10, DateTimeKind.Utc);

    private readonly TempRoot _root = new("DiskCacheMut");

    public void Dispose() => _root.Dispose();

    private sealed class HookLog(Action<string> onError) : ILog
    {
        private readonly List<string> _errors = [];
        public bool Enabled => true;
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? ex = null)
        {
            lock (_errors) _errors.Add(message + " :: " + ex?.GetType().Name);
            onError(message);
        }
        public string[] Errors { get { lock (_errors) return [.. _errors]; } }
    }

    private static readonly MethodInfo RunPrunePassMethod =
        typeof(DiskCacheStore).GetMethod("RunPrunePass", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("DiskCacheStore.RunPrunePass not found");

    private static void RunPass(DiskCacheStore store) => RunPrunePassMethod.Invoke(store, null);

    private static string WriteFile(string dir, string name, int bytes, TimeSpan accessedAgo)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, new byte[bytes]);
        var stamp = DateTime.UtcNow - accessedAgo;
        File.SetLastAccessTimeUtc(path, stamp);
        File.SetCreationTimeUtc(path, stamp);
        return path;
    }

    private static bool Touched(string path, DateTime expected) =>
        Math.Abs((File.GetLastAccessTimeUtc(path) - expected).TotalSeconds) < 5;

    // ---- NoteAccessed ---------------------------------------------------------------------------------------------

    [Fact(DisplayName = "NoteAccessed stamps the file with the injected clock's time, not the wall clock")]
    public void NoteAccessed_UsesInjectedClock()
    {
        var dir = _root.Dir("clock");
        var path = WriteFile(dir, "a.png", 10, TimeSpan.FromHours(5));
        var store = new DiskCacheStore(dir, "*.png", maxBytes: 1000, utcNow: () => T0);

        store.NoteAccessed(path);

        Assert.True(Touched(path, T0), "the entry was not stamped with the injected clock");
    }

    [Fact(DisplayName = "NoteAccessed skips a repeat inside the interval and touches again once the interval has elapsed (inclusive)")]
    public void NoteAccessed_ThrottleBoundaries()
    {
        var dir = _root.Dir("throttle");
        var path = WriteFile(dir, "a.png", 10, TimeSpan.FromHours(5));
        var now = T0;
        var store = new DiskCacheStore(dir, "*.png", maxBytes: 1000, utcNow: () => now);

        store.NoteAccessed(path);
        Assert.True(Touched(path, T0));

        File.SetLastAccessTimeUtc(path, Sentinel);
        now = T0 + DiskCacheStore.AccessTouchInterval - TimeSpan.FromSeconds(1);
        store.NoteAccessed(path);
        Assert.True(Touched(path, Sentinel), "touched again inside the throttle interval");

        now = T0 + DiskCacheStore.AccessTouchInterval; // exactly one interval after the last touch
        store.NoteAccessed(path);
        Assert.True(Touched(path, now), "not touched once a full interval has elapsed");
    }

    [Fact(DisplayName = "NoteAccessed forgets its throttle entries only once the tracked-entry cap is reached")]
    public void NoteAccessed_ThrottleTableIsClearedExactlyAtTheCap()
    {
        var dir = _root.Dir("cap");
        var a = WriteFile(dir, "a.png", 10, TimeSpan.FromHours(5));
        var store = new DiskCacheStore(dir, "*.png", maxBytes: 1000, utcNow: () => T0);

        store.NoteAccessed(a);
        File.SetLastAccessTimeUtc(a, Sentinel);
        store.NoteAccessed(a);
        Assert.True(Touched(a, Sentinel), "a throttled repeat must not clear the table or touch the file");
        store.NoteAccessed(Path.Combine(dir, "other.png")); // a second entry well below the cap must not make the table forget "a"
        store.NoteAccessed(a);
        Assert.True(Touched(a, Sentinel), "the table was cleared long before reaching its cap");

        // 8191 further distinct entries (missing files: the touch fails quietly, the entry is still tracked) -> exactly 8192 tracked.
        for (var i = 0; i < 8191; i++) store.NoteAccessed(Path.Combine(dir, $"f{i}.png"));
        store.NoteAccessed(Path.Combine(dir, "overflow.png")); // table full: cleared before this one is added

        store.NoteAccessed(a);
        Assert.True(Touched(a, T0), "the table was not cleared when it reached its cap");
    }

    // ---- NoteWritten / prune accounting ---------------------------------------------------------------------------

    [Fact(DisplayName = "A write noted after a scan of an empty directory is tracked, so the next pass scans and prunes it")]
    public void NoteWritten_AfterEmptyScan_IsTracked()
    {
        var dir = _root.Dir("empty-then-write");
        var store = new DiskCacheStore(dir, "*.png", maxBytes: 50);
        RunPass(store);
        Assert.Equal(1, store.FullScanCount);

        var big = WriteFile(dir, "big.png", 100, TimeSpan.Zero);
        store.NoteWritten(big);
        RunPass(store);

        Assert.Equal(2, store.FullScanCount);
        Assert.False(File.Exists(big));
    }

    [Fact(DisplayName = "A write noted while the directory size is still unknown does not fake a tracked size: the next pass scans and prunes")]
    public void NoteWritten_WhenSizeUnknown_StillForcesAFullScan()
    {
        var dir = _root.Dir("unknown-size");
        WriteFile(dir, "a.png", 100, TimeSpan.FromMinutes(30));
        WriteFile(dir, "b.png", 100, TimeSpan.FromMinutes(20));
        WriteFile(dir, "c.png", 100, TimeSpan.FromMinutes(10));
        var store = new DiskCacheStore(dir, "*.png", maxBytes: 150);
        store.NoteWritten(WriteFile(dir, "d.png", 10, TimeSpan.Zero));

        RunPass(store);

        Assert.Equal(1, store.FullScanCount);
        Assert.True(Directory.EnumerateFiles(dir, "*.png").Sum(p => new FileInfo(p).Length) <= 150);
    }

    [Fact(DisplayName = "A pass over an empty directory is tracked as 0 bytes: the next pass is skipped")]
    public void RunPrunePass_EmptyDirectory_NextPassSkipped()
    {
        var store = new DiskCacheStore(_root.Dir("empty"), "*.png", maxBytes: 100);

        RunPass(store);
        RunPass(store);

        Assert.Equal(1, store.FullScanCount);
    }

    [Fact(DisplayName = "A directory exactly at quota is under quota: the next pass is skipped and nothing is deleted")]
    public void RunPrunePass_ExactlyAtQuota_NextPassSkipped()
    {
        var dir = _root.Dir("at-quota");
        var file = WriteFile(dir, "a.png", 100, TimeSpan.FromMinutes(5));
        var store = new DiskCacheStore(dir, "*.png", maxBytes: 100);

        RunPass(store);
        RunPass(store);

        Assert.Equal(1, store.FullScanCount);
        Assert.True(File.Exists(file));
    }

    [Fact(DisplayName = "A missing directory leaves the size unknown, so every pass is a full scan")]
    public void RunPrunePass_MissingDirectory_EveryPassScans()
    {
        var store = new DiskCacheStore(_root.Combine("not-there"), "*.png", maxBytes: 100);

        RunPass(store);
        RunPass(store);
        RunPass(store);

        Assert.Equal(3, store.FullScanCount);
    }

    [Fact(DisplayName = "Under quota, a full scan is still forced after exactly 255 skipped passes (every 256th pass)")]
    public void RunPrunePass_ForcesAFullScanEvery256thPass()
    {
        var dir = _root.Dir("forced");
        WriteFile(dir, "a.png", 10, TimeSpan.FromMinutes(5));
        var store = new DiskCacheStore(dir, "*.png", maxBytes: 1000);
        RunPass(store);
        Assert.Equal(1, store.FullScanCount);

        for (var i = 1; i <= 255; i++) RunPass(store);
        Assert.Equal(1, store.FullScanCount);

        RunPass(store); // the 256th pass since the scan
        Assert.Equal(2, store.FullScanCount);

        for (var i = 1; i <= 255; i++) RunPass(store);
        Assert.Equal(2, store.FullScanCount);
        RunPass(store);
        Assert.Equal(3, store.FullScanCount);
    }

    [Fact(DisplayName = "Writes noted while a scan runs are added on top of what the scan found, so they cannot hide an over-quota cache")]
    public void RunPrunePass_WritesNotedDuringScan_AreAddedToTheTrackedSize()
    {
        var dir = _root.Dir("noted-during");
        var extraDir = _root.Dir("extra");
        var locked = WriteFile(dir, "a-locked.png", 60, TimeSpan.FromMinutes(30)); // oldest: pruned first, but cannot be deleted
        WriteFile(dir, "b.png", 60, TimeSpan.FromMinutes(20));
        var extra = WriteFile(extraDir, "x.png", 50, TimeSpan.Zero);
        DiskCacheStore? store = null;
        var log = new HookLog(_ => store!.NoteWritten(extra)); // runs inside the scan, when the locked delete fails
        store = new DiskCacheStore(dir, "*.png", maxBytes: 100, log);

        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            RunPass(store);
        }

        // scan left 60 (b deleted, a locked) + 50 noted during it = 110 > 100: the next pass must scan again.
        Assert.Contains(log.Errors, e => e.Contains("Delete failed", StringComparison.Ordinal));
        Assert.Equal(1, store.FullScanCount);
        RunPass(store);
        Assert.Equal(2, store.FullScanCount);
    }

    [Fact(DisplayName = "ClearDirectory forgets the tracked size, so the next pass scans again")]
    public void ClearDirectory_ForgetsTrackedSize()
    {
        var dir = _root.Dir("clear-forget");
        WriteFile(dir, "a.png", 100, TimeSpan.FromMinutes(5));
        var store = new DiskCacheStore(dir, "*.png", maxBytes: 1000);
        RunPass(store);
        RunPass(store);
        Assert.Equal(1, store.FullScanCount);

        store.ClearDirectory();
        RunPass(store);

        Assert.Equal(2, store.FullScanCount);
    }

    // ---- PruneDirectory / TryDelete -------------------------------------------------------------------------------

    [Fact(DisplayName = "PruneDirectory deletes nothing when the total is exactly the quota and returns that total")]
    public void PruneDirectory_TotalEqualsQuota_DeletesNothing()
    {
        var dir = _root.Dir("equal");
        var a = WriteFile(dir, "a.png", 100, TimeSpan.FromMinutes(20));
        var b = WriteFile(dir, "b.png", 100, TimeSpan.FromMinutes(10));

        var remaining = DiskCacheStore.PruneDirectory(dir, "*.png", maxBytes: 200);

        Assert.Equal(200, remaining);
        Assert.True(File.Exists(a) && File.Exists(b));
    }

    [Fact(DisplayName = "PruneDirectory returns the bytes left after deleting, and counts a file it could not delete as still there")]
    public void PruneDirectory_ReturnsRemainingBytes()
    {
        var dir = _root.Dir("remaining");
        var oldest = WriteFile(dir, "oldest.png", 100, TimeSpan.FromMinutes(30));
        WriteFile(dir, "middle.png", 100, TimeSpan.FromMinutes(20));
        WriteFile(dir, "newest.png", 100, TimeSpan.FromMinutes(10));

        Assert.Equal(100, DiskCacheStore.PruneDirectory(dir, "*.png", maxBytes: 150));

        var dir2 = _root.Dir("remaining-locked");
        var locked = WriteFile(dir2, "locked.png", 100, TimeSpan.FromMinutes(30));
        WriteFile(dir2, "other.png", 100, TimeSpan.FromMinutes(10));
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Equal(100, DiskCacheStore.PruneDirectory(dir2, "*.png", maxBytes: 150));
            Assert.Equal(100, DiskCacheStore.PruneDirectory(dir2, "*.png", maxBytes: 10)); // locked one stays, counted
        }
        Assert.False(File.Exists(oldest));
    }

    [Fact(DisplayName = "TryDelete reports false and logs when the file is in use, and true when it is gone")]
    public void TryDelete_LockedFile_ReturnsFalseAndLogs()
    {
        var path = WriteFile(_root.Dir("trydel"), "a.png", 10, TimeSpan.Zero);
        var log = new HookLog(_ => { });

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.False(DiskCacheStore.TryDelete(path, log));
        }
        Assert.Single(log.Errors);
        Assert.StartsWith("Delete failed", log.Errors[0], StringComparison.Ordinal);
        Assert.True(File.Exists(path));

        Assert.True(DiskCacheStore.TryDelete(path, log));
        Assert.False(File.Exists(path));
        Assert.False(DiskCacheStore.TryDelete(path, log)); // already gone: false, no new error
        Assert.Single(log.Errors);
    }

    [Fact(DisplayName = "TryDelete of a read-only file reports false and logs the access error instead of throwing")]
    public void TryDelete_ReadOnlyFile_ReturnsFalseAndLogs()
    {
        var path = WriteFile(_root.Dir("readonly"), "a.png", 10, TimeSpan.Zero);
        File.SetAttributes(path, FileAttributes.ReadOnly);
        var log = new HookLog(_ => { });
        try
        {
            Assert.False(DiskCacheStore.TryDelete(path, log));

            Assert.Single(log.Errors);
            Assert.Contains("UnauthorizedAccessException", log.Errors[0], StringComparison.Ordinal);
            Assert.True(File.Exists(path));
        }
        finally { File.SetAttributes(path, FileAttributes.Normal); }
    }

    // ---- DeleteStaleTempFiles -------------------------------------------------------------------------------------

    [Fact(DisplayName = "DeleteStaleTempFiles on a directory that cannot be listed logs the failure and returns 0 instead of throwing")]
    [Trait("Category", "Native")] // changes a real ACL (Deny ListDirectory): a killed test host could leave the Deny ACE behind, the finally block restores it otherwise
    public void DeleteStaleTempFiles_UnlistableDirectory_LogsAndReturnsZero()
    {
        var dir = _root.Dir("denied");
        var info = new DirectoryInfo(dir);
        var user = WindowsIdentity.GetCurrent().User!;
        var deny = new FileSystemAccessRule(user, FileSystemRights.ListDirectory, AccessControlType.Deny);
        var security = info.GetAccessControl();
        security.AddAccessRule(deny);
        info.SetAccessControl(security);
        var log = new HookLog(_ => { });
        try
        {
            var removed = Record.Exception(() => DiskCacheStore.DeleteStaleTempFiles(dir, DateTime.UtcNow, 10, log));

            Assert.Null(removed);
            Assert.Contains(log.Errors, e => e.Contains("Stale temp file cleanup failed", StringComparison.Ordinal));
        }
        finally
        {
            security = info.GetAccessControl();
            security.RemoveAccessRule(deny);
            info.SetAccessControl(security);
        }
    }

    // ---- atomic PNG write -----------------------------------------------------------------------------------------

    private sealed class NonBitmapImage : IDecodedImage
    {
        public int PixelWidth => 1;
        public int PixelHeight => 1;
        public bool Downscaled => false;
        public int Orientation => 1;
        public long EstimatedBytes => 4;
        public object PlatformImage => new object();
    }

    private static BitmapSource Bitmap(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i++) pixels[i] = (byte)(i * 7);
        var bmp = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bmp.Freeze();
        return bmp;
    }

    [Fact(DisplayName = "WriteAtomicallyAsync(IDecodedImage) validates its arguments before touching the disk")]
    public async Task WriteAtomicallyAsync_InvalidImage_Throws()
    {
        var path = _root.Combine("x", "out.png");

        await Assert.ThrowsAsync<ArgumentNullException>(() => DiskCacheStore.WriteAtomicallyAsync((IDecodedImage)null!, path));
        await Assert.ThrowsAsync<ArgumentException>(() => DiskCacheStore.WriteAtomicallyAsync(new NonBitmapImage(), path));

        Assert.False(File.Exists(path));
    }

    [Fact(DisplayName = "WriteAtomicallyAsync writes a PNG that decodes back to the same size, with no temp file left")]
    public async Task WriteAtomicallyAsync_WritesADecodablePng()
    {
        var dir = _root.Dir("png");
        var path = Path.Combine(dir, "out.png");

        await DiskCacheStore.WriteAtomicallyAsync(new WpfDecodedImage(Bitmap(5, 3)), path);

        var decoder = new PngBitmapDecoder(new Uri(path), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        Assert.Equal(5, decoder.Frames[0].PixelWidth);
        Assert.Equal(3, decoder.Frames[0].PixelHeight);
        Assert.Equal([path], Directory.GetFiles(dir));
    }
}

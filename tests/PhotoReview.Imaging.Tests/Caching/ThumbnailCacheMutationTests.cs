using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using PhotoReview.Core.Abstractions;
using PhotoReview.TestSupport.Windows.Fixtures;

namespace PhotoReview.Imaging.Tests.Caching;

/// <summary>
/// Mutation-testing follow-ups for <see cref="ThumbnailCache"/> (docs/MUTATION-TESTING.md): a load that finishes after
/// ClearMemory is not cached, a persist that goes stale mid-write is removed, a fresh persist feeds the quota prune, and
/// ClearDisk empties the directory (and survives a directory it cannot list).
/// </summary>
[Trait("Category", "HotPath")]
public sealed class ThumbnailCacheMutationTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private readonly TempRoot _root = new("thumb-mut");

    public void Dispose() => _root.Dispose();

    private sealed class RecordingLog : ILog
    {
        private readonly List<string> _errors = [];
        public bool Enabled => true;
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? ex = null) { lock (_errors) _errors.Add(message + " :: " + ex?.GetType().Name); }
        public string[] Errors { get { lock (_errors) return [.. _errors]; } }
    }

    private string Source(string name) => _root.File(name, EmbeddedThumbnailJpegFixture.CreateWithThumbnail(48, 16));

    [Fact(DisplayName = "A load that finishes after ClearMemory is returned but not cached in RAM, so the next request loads again")]
    public async Task ClearMemoryDuringLoad_ResultIsNotCached()
    {
        var source = Source("a.jpg");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        using var cache = new ThumbnailCache(_root.Dir("disk"), persistNewThumbnails: false, embeddedThumbnailReader: async (path, token) =>
        {
            if (Interlocked.Increment(ref reads) == 1)
            {
                entered.SetResult();
                await release.Task;
            }
            return EmbeddedThumbnailReader.TryRead(path);
        });

        var pending = cache.GetAsync(source);
        await entered.Task.WaitAsync(Timeout);
        cache.ClearMemory();
        release.SetResult();
        Assert.NotNull(await pending.WaitAsync(Timeout));

        Assert.NotNull(await cache.GetAsync(source).WaitAsync(Timeout));
        Assert.Equal(2, reads);
        Assert.NotNull(await cache.GetAsync(source).WaitAsync(Timeout)); // now cached
        Assert.Equal(2, reads);
    }

    [Fact(DisplayName = "A thumbnail whose disk write went stale mid-write (ClearDisk ran) is returned but its file is removed")]
    public async Task PersistGoesStaleMidWrite_FileIsRemoved()
    {
        var source = Source("a.jpg");
        var disk = _root.Dir("disk-stale");
        using var cache = new ThumbnailCache(disk, persistNewThumbnails: true);
        cache.PersistForTests = (bitmap, path, token) =>
        {
            cache.ClearDisk(); // the user clears the cache while this write is in progress
            File.WriteAllBytes(path, new byte[100]);
            return Task.CompletedTask;
        };

        var image = await cache.GetAsync(source).WaitAsync(Timeout);

        Assert.NotNull(image);
        Assert.Empty(Directory.GetFiles(disk, "*.png"));
    }

    [Fact(DisplayName = "A freshly persisted thumbnail counts toward the quota: the prune it schedules removes it when it exceeds the limit")]
    public async Task FreshPersist_SchedulesAPrune()
    {
        var source = Source("a.jpg");
        var disk = _root.Dir("disk-prune");
        using var cache = new ThumbnailCache(disk, maxDiskBytes: 1, persistNewThumbnails: true);
        cache.PersistForTests = (bitmap, path, token) =>
        {
            File.WriteAllBytes(path, new byte[100]);
            return Task.CompletedTask;
        };

        Assert.NotNull(await cache.GetAsync(source).WaitAsync(Timeout));
        Assert.True(await cache.WaitForPruneAsync(Timeout));

        Assert.Empty(Directory.GetFiles(disk, "*.png"));
        Assert.Equal(1, cache.DiskStore.FullScanCount);
    }

    [Fact(DisplayName = "ClearDisk removes the cached thumbnail files")]
    public void ClearDisk_RemovesThumbnailFiles()
    {
        var disk = _root.Dir("disk-clear");
        var png = _root.File(Path.Combine("disk-clear", "a.png"), 1, 2, 3);
        var other = _root.File(Path.Combine("disk-clear", "b.txt"), 1);
        using var cache = new ThumbnailCache(disk, persistNewThumbnails: true);

        cache.ClearDisk();

        Assert.False(File.Exists(png));
        Assert.True(File.Exists(other));
    }

    [Fact(DisplayName = "A load that was in flight when ClearDisk ran does not recreate a thumbnail file afterwards")]
    public async Task ClearDiskDuringLoad_DoesNotRecreateTheFile()
    {
        var source = Source("a.jpg");
        var disk = _root.Dir("disk-inflight");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cache = new ThumbnailCache(disk, persistNewThumbnails: true, embeddedThumbnailReader: async (path, token) =>
        {
            entered.SetResult();
            await release.Task;
            return EmbeddedThumbnailReader.TryRead(path);
        });

        var pending = cache.GetAsync(source);
        await entered.Task.WaitAsync(Timeout);
        cache.ClearDisk();
        release.SetResult();

        Assert.NotNull(await pending.WaitAsync(Timeout));
        Assert.Empty(Directory.GetFiles(disk, "*.png"));
    }

    [Fact(DisplayName = "ClearDisk on a directory that cannot be listed logs the failure instead of throwing")]
    public async Task ClearDisk_UnlistableDirectory_LogsAndDoesNotThrow()
    {
        var disk = _root.Dir("disk-denied");
        var log = new RecordingLog();
        using var cache = new ThumbnailCache(disk, persistNewThumbnails: true, log: log);
        await cache.StartupCleanup.WaitAsync(Timeout);
        var info = new DirectoryInfo(disk);
        var deny = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory, AccessControlType.Deny);
        var security = info.GetAccessControl();
        security.AddAccessRule(deny);
        info.SetAccessControl(security);
        try
        {
            var thrown = Record.Exception(cache.ClearDisk);

            Assert.Null(thrown);
            Assert.Contains(log.Errors, e => e.Contains("Thumbnail disk cache clear failed", StringComparison.Ordinal));
        }
        finally
        {
            security = info.GetAccessControl();
            security.RemoveAccessRule(deny);
            info.SetAccessControl(security);
        }
    }
}

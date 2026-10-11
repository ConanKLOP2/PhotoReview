using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using PhotoReview.Core.Abstractions;
using PhotoReview.TestSupport.Windows.Fixtures;

namespace PhotoReview.Imaging.Tests.Caching;

/// <summary>
/// Mutation-testing follow-ups for <see cref="ThumbnailCache"/> (docs/MUTATION-TESTING.md): a load that finishes after
/// ClearMemory is not cached, and ClearDisk empties the directory (and survives a directory it cannot list).
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
        using var cache = new ThumbnailCache(WpfBitmapSourceCodec.Instance, _root.Dir("disk"), embeddedThumbnailReader: async (path, token) =>
        {
            if (Interlocked.Increment(ref reads) == 1)
            {
                entered.SetResult();
                await release.Task;
            }
            return EmbeddedThumbnailReader.TryRead(path, WpfBitmapSourceCodec.Instance);
        });

        var pending = cache.GetAsync(source, null);
        await entered.Task.WaitAsync(Timeout);
        cache.ClearMemory();
        release.SetResult();
        Assert.NotNull(await pending.WaitAsync(Timeout));

        Assert.NotNull(await cache.GetAsync(source, null).WaitAsync(Timeout));
        Assert.Equal(2, reads);
        Assert.NotNull(await cache.GetAsync(source, null).WaitAsync(Timeout)); // now cached
        Assert.Equal(2, reads);
    }

    [Fact(DisplayName = "ClearDisk removes the cached thumbnail files")]
    public void ClearDisk_RemovesThumbnailFiles()
    {
        var disk = _root.Dir("disk-clear");
        var png = _root.File(Path.Combine("disk-clear", "a.png"), 1, 2, 3);
        var other = _root.File(Path.Combine("disk-clear", "b.txt"), 1);
        using var cache = new ThumbnailCache(WpfBitmapSourceCodec.Instance, disk);

        cache.ClearDisk();

        Assert.False(File.Exists(png));
        Assert.True(File.Exists(other));
    }

    [Fact(DisplayName = "ClearDisk on a directory that cannot be listed logs the failure instead of throwing")]
    [Trait("Category", "Native")] // changes a real ACL (Deny ListDirectory): a killed test host could leave the Deny ACE behind, the finally block restores it otherwise
    public async Task ClearDisk_UnlistableDirectory_LogsAndDoesNotThrow()
    {
        var disk = _root.Dir("disk-denied");
        var log = new RecordingLog();
        using var cache = new ThumbnailCache(WpfBitmapSourceCodec.Instance, disk, log: log);
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

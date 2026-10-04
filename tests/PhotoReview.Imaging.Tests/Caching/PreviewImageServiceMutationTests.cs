using System.Collections.Concurrent;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.Model;
using PhotoReview.Imaging.Decoding;

namespace PhotoReview.Imaging.Tests.Caching;

/// <summary>
/// Mutation-testing follow-ups for <see cref="PreviewImageService"/> (docs/MUTATION-TESTING.md): the disk-cache on/off
/// decision, legacy-file cleanup and its failure handling, start-delay handling, the dedicated decode threads, when a decode
/// is persisted, ClearDisk during an in-flight decode and the key built from a catalog entry.
/// </summary>
[Trait("Category", "HotPath")]
public sealed class PreviewImageServiceMutationTests : IAsyncLifetime, IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private readonly TempRoot _root = new("preview-mut");
    private readonly List<PreviewImageService> _services = [];
    private readonly List<Action> _cleanups = [];

    public void Dispose() { } // _root is disposed in DisposeAsync, after the persist workers stop

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var cleanup in _cleanups) cleanup();
        await Task.WhenAll(_services.Select(s => s.ShutdownPersistWorkersAsync()));
        await Task.WhenAll(_services.Select(s => s.WaitForPruneAsync(TimeSpan.FromSeconds(5))));
        _root.Dispose();
    }

    private sealed class RecordingLog : ILog
    {
        private readonly List<string> _errors = [];
        public bool Enabled => true;
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? ex = null) { lock (_errors) _errors.Add(message + " :: " + ex?.GetType().Name); }
        public string[] Errors { get { lock (_errors) return [.. _errors]; } }
    }

    private static BitmapSource Bitmap()
    {
        var pixels = new byte[8 * 6 * 4];
        Array.Fill(pixels, (byte)90);
        var bitmap = BitmapSource.Create(8, 6, 96, 96, PixelFormats.Bgr32, null, pixels, 8 * 4);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Decodes a fixed downscaled bitmap, records the decoding thread, and blocks on <see cref="Gate"/> when it is set.</summary>
    private sealed class RecordingDecoder : IImageDecoder
    {
        private int _calls;
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? Gate { get; set; }
        public int Calls => Volatile.Read(ref _calls);
        public Task Started => _started.Task;
        public ConcurrentQueue<(bool IsPool, ThreadPriority Priority, int ThreadId, SourceReadPriority Request)> Threads { get; } = new();

        public IDecodedImage Decode(DecodeRequest request)
        {
            Interlocked.Increment(ref _calls);
            Threads.Enqueue((Thread.CurrentThread.IsThreadPoolThread, Thread.CurrentThread.Priority, Environment.CurrentManagedThreadId, request.Priority));
            _started.TrySetResult();
            Gate?.Task.Wait(Timeout);
            return new WpfDecodedImage(Bitmap(), downscaled: true, originalWidth: 800, originalHeight: 600);
        }

        public ImageInfo ReadInfo(string path) => new(800, 600);
    }

    private PreviewImageService Create(RecordingDecoder decoder, string? disk, long diskCapacity = 1L << 30, RecordingLog? log = null,
        Func<DecoderBackend>? backend = null, Func<bool>? original = null)
    {
        var service = new PreviewImageService(new ReviewMetrics(), original ?? (() => false), () => 256, capacityBytes: 64L * 1024 * 1024,
            diskCacheDirectory: disk ?? _root.Dir("unused-cache"), diskCacheCapacityBytes: diskCapacity,
            disableDiskCacheOverride: disk is null ? true : null, decoder: decoder, log: log, currentBackend: backend);
        _services.Add(service);
        return service;
    }

    private string Source(string name) => _root.File(name, 1, 2, 3, 4);

    private Restorer DenyListing(string directory)
    {
        var info = new DirectoryInfo(directory);
        var deny = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory, AccessControlType.Deny);
        var security = info.GetAccessControl();
        security.AddAccessRule(deny);
        info.SetAccessControl(security);
        var restored = false;
        void Restore()
        {
            if (restored) return;
            restored = true;
            var s = info.GetAccessControl();
            s.RemoveAccessRule(deny);
            info.SetAccessControl(s);
        }
        _cleanups.Add(Restore);
        return new Restorer(Restore);
    }

    private sealed class Restorer(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }

    // ---- key building ---------------------------------------------------------------------------------------------

    [Theory(DisplayName = "The key built from a catalog entry equals the one built from its path, with the configured backend, box and source kind")]
    [InlineData("photo.jpg", false)]
    [InlineData("photo.dng", false)]
    [InlineData("photo.jpg", true)]
    public void GetCurrentCacheKey_Entry_MatchesPathKey(string name, bool original)
    {
        var path = Source(name);
        var service = Create(new RecordingDecoder(), null, backend: () => DecoderBackend.TurboJpeg, original: () => original);

        var fromPath = service.GetCurrentCacheKey(path);
        var fromEntry = service.GetCurrentCacheKey(new CatalogEntry(path));

        Assert.Equal(fromPath, fromEntry);
        Assert.Equal(DecoderBackend.TurboJpeg, fromEntry.Backend);
        Assert.Equal(original, fromEntry.IsOriginal);
        Assert.Equal(name.EndsWith(".dng", StringComparison.Ordinal) ? ImageSourceKind.RawPreview : ImageSourceKind.Standard, fromEntry.SourceKind);
        Assert.False(string.IsNullOrEmpty(fromEntry.Path));
    }

    [Fact(DisplayName = "Without a backend provider the key uses the WPF backend")]
    public void GetCurrentCacheKey_DefaultBackend_IsWpf()
    {
        var service = Create(new RecordingDecoder(), null);

        Assert.Equal(DecoderBackend.Wpf, service.GetCurrentCacheKey(Source("a.jpg")).Backend);
    }

    // ---- disk cache on/off ----------------------------------------------------------------------------------------

    [Theory(DisplayName = "A disk-cache capacity of zero or less turns the preview disk cache off (an existing entry is not looked up); a positive one keeps it on")]
    [InlineData(0L)]
    [InlineData(-1L)]
    public async Task Ctor_NonPositiveDiskCapacity_DisablesDiskCache(long capacity)
    {
        var disk = _root.Dir("disk");
        var path = Source("a.jpg");
        var seeding = Create(new RecordingDecoder(), disk);
        var key = seeding.GetCurrentCacheKey(path);
        await seeding.GetPreviewAsync(path, key).WaitAsync(Timeout);
        await seeding.ShutdownPersistWorkersAsync();
        Assert.Single(Directory.GetFiles(disk, "*.pv4"));
        Assert.True(seeding.HasDiskCachedPreview(key));

        var disabled = Create(new RecordingDecoder(), disk, diskCapacity: capacity);

        Assert.False(disabled.HasDiskCachedPreview(key));
    }

    [Fact(DisplayName = "With the disk cache disabled a decode is never queued for persistence (no write attempt, no error, no cache directory)")]
    public async Task DiskCacheDisabled_DecodeIsNotPersisted()
    {
        var log = new RecordingLog();
        var decoder = new RecordingDecoder();
        var disk = _root.Combine("never-created");
        var service = new PreviewImageService(new ReviewMetrics(), () => false, () => 256, capacityBytes: 64L * 1024 * 1024,
            diskCacheDirectory: disk, disableDiskCacheOverride: true, decoder: decoder, log: log);
        _services.Add(service);
        var path = Source("a.jpg");

        await service.GetPreviewAsync(path).WaitAsync(Timeout);
        await service.ShutdownPersistWorkersAsync();

        Assert.Equal(1, decoder.Calls);
        Assert.Empty(log.Errors);
        Assert.False(Directory.Exists(disk));
    }

    [Fact(DisplayName = "A preview served from the disk cache is not written back (the entry file is left untouched)")]
    public async Task DiskCacheHit_DoesNotRewriteTheEntry()
    {
        var disk = _root.Dir("disk-hit");
        var path = Source("a.jpg");
        var first = Create(new RecordingDecoder(), disk);
        var key = first.GetCurrentCacheKey(path);
        await first.GetPreviewAsync(path, key).WaitAsync(Timeout);
        await first.ShutdownPersistWorkersAsync();
        var entry = Assert.Single(Directory.GetFiles(disk, "*.pv4"));
        var sentinel = new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(entry, sentinel);

        var decoder = new RecordingDecoder();
        var second = Create(decoder, disk);
        await second.GetPreviewAsync(path, second.GetCurrentCacheKey(path)).WaitAsync(Timeout);
        await second.ShutdownPersistWorkersAsync();

        Assert.Equal(0, decoder.Calls); // served from disk
        Assert.Equal(sentinel, File.GetLastWriteTimeUtc(entry));
        Assert.Single(Directory.GetFiles(disk, "*.pv4"));
    }

    // ---- legacy cleanup -------------------------------------------------------------------------------------------

    [Fact(DisplayName = "Startup removes leftover pre-v4 .png and .png.meta cache files and keeps current .pv4 entries and unrelated files")]
    public async Task StartupCleanup_RemovesLegacyFilesOnly()
    {
        var disk = _root.Dir("legacy");
        var png = _root.File(Path.Combine("legacy", "a.png"), 1);
        var meta = _root.File(Path.Combine("legacy", "b.png.meta"), 1);
        var pv4 = _root.File(Path.Combine("legacy", "c.pv4"), 1);
        var other = _root.File(Path.Combine("legacy", "d.txt"), 1);

        var service = Create(new RecordingDecoder(), disk);
        await service.StartupCleanup.WaitAsync(Timeout);

        Assert.False(File.Exists(png));
        Assert.False(File.Exists(meta));
        Assert.True(File.Exists(pv4));
        Assert.True(File.Exists(other));
    }

    [Fact(DisplayName = "ClearDisk removes the current entries and any leftover pre-v4 files, but not unrelated files")]
    public async Task ClearDisk_RemovesEntriesAndLegacyFiles()
    {
        var disk = _root.Dir("clear");
        var service = Create(new RecordingDecoder(), disk);
        await service.StartupCleanup.WaitAsync(Timeout);
        var png = _root.File(Path.Combine("clear", "a.png"), 1);
        var meta = _root.File(Path.Combine("clear", "b.png.meta"), 1);
        var pv4 = _root.File(Path.Combine("clear", "c.pv4"), 1);
        var other = _root.File(Path.Combine("clear", "d.txt"), 1);

        service.ClearDisk();

        Assert.False(File.Exists(png));
        Assert.False(File.Exists(meta));
        Assert.False(File.Exists(pv4));
        Assert.True(File.Exists(other));
    }

    [Fact(DisplayName = "A cache directory that cannot be listed makes the startup cleanup log the failure and finish, not fault")]
    [Trait("Category", "Native")] // changes a real ACL (Deny ListDirectory): a killed test host could leave the Deny ACE behind, the finally block restores it otherwise
    public async Task StartupCleanup_UnlistableDirectory_LogsAndCompletes()
    {
        var disk = _root.Dir("denied-startup");
        var log = new RecordingLog();
        using (DenyListing(disk))
        {
            var service = Create(new RecordingDecoder(), disk, log: log);

            await service.StartupCleanup.WaitAsync(Timeout); // must not throw
        }

        Assert.Contains(log.Errors, e => e.Contains("Legacy preview cache cleanup failed", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "ClearDisk on a cache directory that cannot be listed logs the failure instead of throwing")]
    [Trait("Category", "Native")] // changes a real ACL (Deny ListDirectory): a killed test host could leave the Deny ACE behind, the finally block restores it otherwise
    public async Task ClearDisk_UnlistableDirectory_LogsAndDoesNotThrow()
    {
        var disk = _root.Dir("denied-clear");
        var log = new RecordingLog();
        var service = Create(new RecordingDecoder(), disk, log: log);
        await service.StartupCleanup.WaitAsync(Timeout);
        using (DenyListing(disk))
        {
            var thrown = Record.Exception(service.ClearDisk);

            Assert.Null(thrown);
        }

        Assert.Contains(log.Errors, e => e.Contains("Preview disk cache clear failed", StringComparison.Ordinal));
    }

    // ---- start delay ----------------------------------------------------------------------------------------------

    [Fact(DisplayName = "A viewer decode with a start delay waits for it: cancelled during the delay it never reaches the decoder")]
    public async Task ViewerPreview_PositiveStartDelay_IsHonoured()
    {
        var decoder = new RecordingDecoder();
        var service = Create(decoder, null);
        var path = Source("a.jpg");
        var key = service.GetCurrentCacheKey(path);
        using var cts = new CancellationTokenSource();

        var pending = service.GetViewerPreviewAsync(path, key, cts.Token, TimeSpan.FromHours(1));
        Assert.True(service.HasInflightPreview(key));
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(Timeout));
        Assert.Equal(0, decoder.Calls);
        Assert.False(service.HasInflightPreview(key));
    }

    [Theory(DisplayName = "A zero or negative start delay does not wait at all (a negative one must not become an infinite Task.Delay)")]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-5)]
    public async Task ViewerPreview_NonPositiveStartDelay_DecodesImmediately(int milliseconds)
    {
        var decoder = new RecordingDecoder();
        var service = Create(decoder, null);
        var path = Source("a.jpg");

        var image = await service.GetViewerPreviewAsync(path, service.GetCurrentCacheKey(path), CancellationToken.None,
            TimeSpan.FromMilliseconds(milliseconds)).WaitAsync(Timeout);

        Assert.NotNull(image);
        Assert.Equal(1, decoder.Calls);
    }

    // ---- dedicated decode threads ---------------------------------------------------------------------------------

    [Fact(DisplayName = "A viewer decode runs on a dedicated above-normal-priority thread; a preload decode runs on a thread-pool thread")]
    public async Task ViewerDecode_RunsOnDedicatedThread_PreloadOnPool()
    {
        var decoder = new RecordingDecoder();
        var service = Create(decoder, null);
        var viewerPath = Source("viewer.jpg");
        var preloadPath = Source("preload.jpg");

        await service.GetViewerPreviewAsync(viewerPath, service.GetCurrentCacheKey(viewerPath), CancellationToken.None).WaitAsync(Timeout);
        await service.GetPreviewAsync(preloadPath).WaitAsync(Timeout);

        var decodes = decoder.Threads.ToArray();
        Assert.Equal(2, decodes.Length);
        var viewer = Assert.Single(decodes, d => d.Request == SourceReadPriority.Viewer);
        var preload = Assert.Single(decodes, d => d.Request == SourceReadPriority.Preload);
        Assert.False(viewer.IsPool, "the viewer decode must run on a dedicated thread, not a pool thread");
        Assert.Equal(ThreadPriority.AboveNormal, viewer.Priority);
        Assert.True(preload.IsPool);
    }

    [Fact(DisplayName = "A full-resolution original decode runs on a dedicated above-normal thread and hands its result back off that thread")]
    public async Task DecodeOriginal_RunsOnDedicatedThread_AndContinuesElsewhere()
    {
        var decoder = new RecordingDecoder();
        var service = Create(decoder, null);
        var path = Source("a.jpg");
        var key = service.GetCurrentCacheKey(path);

        var image = await service.DecodeOriginalAsync(path, key, CancellationToken.None).WaitAsync(Timeout);
        var continuationThread = Environment.CurrentManagedThreadId;

        Assert.NotNull(image);
        var decode = Assert.Single(decoder.Threads);
        Assert.False(decode.IsPool, "an original decode must run on a dedicated thread");
        Assert.Equal(ThreadPriority.AboveNormal, decode.Priority);
        Assert.NotEqual(decode.ThreadId, continuationThread);
    }

    // ---- ClearDisk during a decode --------------------------------------------------------------------------------

    [Fact(DisplayName = "ClearDisk during an in-flight decode: the caller still gets the image, but it is neither cached in RAM nor persisted")]
    public async Task ClearDisk_DuringInflightDecode_ResultNotCachedOrPersisted()
    {
        var disk = _root.Dir("disk-clear-inflight");
        var decoder = new RecordingDecoder { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var service = Create(decoder, disk);
        await service.StartupCleanup.WaitAsync(Timeout);
        var path = Source("a.jpg");
        var key = service.GetCurrentCacheKey(path);
        var pending = service.GetPreviewAsync(path, key);
        await decoder.Started.WaitAsync(Timeout);

        service.ClearDisk(); // the cache generation changes while the decode runs
        decoder.Gate.TrySetResult();
        var image = await pending.WaitAsync(Timeout);
        await service.ShutdownPersistWorkersAsync();

        Assert.NotNull(image);
        Assert.False(service.TryGetCachedPreview(key, out _));
        Assert.Empty(Directory.GetFiles(disk, "*.pv4"));
    }

    // ---- lookups of missing files ---------------------------------------------------------------------------------

    [Fact(DisplayName = "Cache lookups by a path that no longer exists are a quiet miss, not an exception")]
    public void MissingPath_LookupsAreAMiss()
    {
        var service = Create(new RecordingDecoder(), null);
        var missing = _root.Combine("gone.jpg");

        Assert.False(service.TryGetCachedPreview(missing, out var image));
        Assert.Null(image);
    }
}

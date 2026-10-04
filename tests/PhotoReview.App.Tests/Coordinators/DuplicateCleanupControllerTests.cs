using System.IO;
using PhotoReview.App.Coordinators;
using PhotoReview.App.ViewModels;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core;
using PhotoReview.Core.IO;
using PhotoReview.Core.Localization;
using PhotoReview.Imaging.Caching;

namespace PhotoReview.App.Tests.Coordinators;

public sealed class DuplicateCleanupControllerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PhotoReview_DupCtl_" + Guid.NewGuid().ToString("N"));

    public DuplicateCleanupControllerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact(DisplayName = "Clear cache empties the source-bytes RAM cache and the preview disk cache (R2-F-16)")]
    public async Task ClearCacheAsync_ClearsSourceBytesCacheAndPreviewDisk()
    {
        var photo = Path.Combine(_root, "photo.bin");
        File.WriteAllBytes(photo, new byte[4096]);
        var sourceBytes = new SourceBytesCache(1024 * 1024);
        _ = sourceBytes.GetOrRead(photo);
        Assert.Equal(1, sourceBytes.Count);

        var previewDir = Path.Combine(_root, "preview");
        Directory.CreateDirectory(previewDir);
        var cached = Path.Combine(previewDir, "entry.pv4");
        File.WriteAllBytes(cached, new byte[128]);

        var metrics = new ReviewMetrics();
        var preview = new PreviewImageService(
            metrics, () => false, () => new DecodeBox(1920, 0), capacityBytes: 16 * 1024 * 1024,
            diskCacheDirectory: previewDir, sourceBytesCache: sourceBytes);
        var controller = new DuplicateCleanupController(
            new GenerationClock(), new ReviewCatalog(), fileActionService: null, hashService: null,
            new PhysicalFileSystem(), dialogService: null, new InlineUiScheduler(),
            preloadController: null, thumbnailCache: null, previewService: preview, new NullSink());

        await controller.ClearCacheAsync();

        Assert.Equal(0, sourceBytes.Count);
        Assert.False(File.Exists(cached));
    }

    [Fact(DisplayName = "Duplicate scan stops hashing once the folder changed (minimize disk reads) and says why")]
    public async Task RemoveDuplicatesAsync_FolderChangesBeforeHashing_HashesNothing_AndReportsFolderChanged()
    {
        var folder = Path.Combine(_root, "album");
        Directory.CreateDirectory(folder);
        var files = new[] { Path.Combine(folder, "a.jpg"), Path.Combine(folder, "b.jpg"), Path.Combine(folder, "c.jpg") };
        foreach (var file in files) File.WriteAllBytes(file, new byte[2048]); // same size: every file is hashed

        var clock = new GenerationClock();
        var catalog = new ReviewCatalog();
        catalog.Reset(files);
        var sourceBytes = new SourceBytesCache(1024 * 1024); // a hash reads through it: Count = files hashed
        var fs = new FolderSwitchingFileSystem(new PhysicalFileSystem(), () => clock.NextFolder());
        var appPaths = new AppPaths(_root);
        var fileActions = new FileActionService(new OperationJournal(appPaths, fs, new SystemClock()), fs, new SystemClock(), new UnusedRecycleBin());
        var sink = new StatusSink();
        var controller = new DuplicateCleanupController(
            clock, catalog, fileActions, new FileHashService(sourceBytes),
            fs, dialogService: null, new InlineUiScheduler(),
            preloadController: null, thumbnailCache: null, previewService: null, sink);

        await controller.RemoveDuplicatesAsync(removeNumbered: false);

        Assert.Equal(0, sourceBytes.Count);
        Assert.Equal(Tr.StatusDuplicateCheckCanceledFolderChanged, sink.Statuses[^1]); // a "checking... Esc cancels" status precedes it
    }

    /// <summary>a.jpg and "a (1).jpg" are identical: "a (1).jpg" is the numbered copy the batch recycles.</summary>
    private (DuplicateCleanupController Controller, ReloadingSink Sink, RecordingPreload Preload, string Removed) NewBatch()
    {
        var folder = Path.Combine(_root, "album");
        Directory.CreateDirectory(folder);
        var keep = Path.Combine(folder, "a.jpg");
        var removed = Path.Combine(folder, "a (1).jpg");
        File.WriteAllBytes(keep, new byte[2048]);
        File.WriteAllBytes(removed, new byte[2048]);
        var catalog = new ReviewCatalog();
        catalog.Reset([keep, removed]);
        var fs = new PhysicalFileSystem();
        var fileActions = new FileActionService(new OperationJournal(new AppPaths(_root), fs, new SystemClock()), fs, new SystemClock(), new DeletingRecycleBin());
        var preview = new PreviewImageService(new ReviewMetrics(), () => false, () => new DecodeBox(1920, 0), capacityBytes: 16 * 1024 * 1024);
        var sink = new ReloadingSink();
        var preload = new RecordingPreload();
        var controller = new DuplicateCleanupController(
            new GenerationClock(), catalog, fileActions, new FileHashService(new SourceBytesCache(1024 * 1024)),
            fs, dialogService: null, new InlineUiScheduler(),
            preload, thumbnailCache: null, previewService: preview, sink);
        return (controller, sink, preload, removed);
    }

    [Fact(DisplayName = "Batch duplicate cleanup: the 'Batch done' status is set after the folder reload, which clears the status line")]
    public async Task RemoveDuplicatesAsync_BatchDoneStatus_SurvivesFolderReload()
    {
        var (controller, sink, _, removed) = NewBatch();

        await controller.RemoveDuplicatesAsync(removeNumbered: true);

        Assert.False(File.Exists(removed));
        Assert.Equal(1, sink.Reloads);
        Assert.Equal(StatusFormatter.BatchDone(1, 0), sink.Current);
    }

    [Fact(DisplayName = "Batch duplicate cleanup evicts each recycled file from the preload-key set")]
    public async Task RemoveDuplicatesAsync_EvictsRecycledFilesFromPreloadKeys()
    {
        var (controller, _, preload, removed) = NewBatch();

        await controller.RemoveDuplicatesAsync(removeNumbered: true);

        Assert.Equal([Path.GetFullPath(removed).ToUpperInvariant()], preload.RemovedKeys);
    }

    /// <summary>Like MainViewModel: opening the folder clears the status line.</summary>
    private sealed class ReloadingSink : IDuplicateCleanupSink
    {
        public string? Current { get; private set; }
        public int Reloads { get; private set; }
        public void SetStatusText(string status) => Current = status;
        public Task OpenFolderAsync(string folder, string? initialPath = null)
        {
            Reloads++;
            Current = null;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingPreload : IPreloadController
    {
        public List<string> RemovedKeys { get; } = [];
        public Task PreloadAroundAsync(int center) => Task.CompletedTask;
        public bool TryConsumePreloadedKey(ImageCacheKey key) => false;
        public void Cancel() { }
        public void RemovePreloadedKeysForPath(string normalizedPath) => RemovedKeys.Add(normalizedPath);
        public void ClearPreloadedKeys() { }
    }

    /// <summary>Four identical files: a.jpg is kept, the three numbered copies are the batch.</summary>
    private static readonly string[] BatchOfThreeNames = ["a.jpg", "a (1).jpg", "a (2).jpg", "a (3).jpg"];

    private (DuplicateCleanupController Controller, LateStatusSink Sink, GenerationClock Clock, HookedRecycleBin Bin) NewBatchOfThree()
    {
        var folder = Path.Combine(_root, "album");
        Directory.CreateDirectory(folder);
        var files = BatchOfThreeNames.Select(name => Path.Combine(folder, name)).ToArray();
        foreach (var file in files) File.WriteAllBytes(file, new byte[2048]);
        var catalog = new ReviewCatalog();
        catalog.Reset(files);
        var fs = new PhysicalFileSystem();
        var clock = new GenerationClock();
        var bin = new HookedRecycleBin();
        var fileActions = new FileActionService(new OperationJournal(new AppPaths(_root), fs, new SystemClock()), fs, new SystemClock(), bin);
        var sink = new LateStatusSink();
        var controller = new DuplicateCleanupController(
            clock, catalog, fileActions, new FileHashService(new SourceBytesCache(1024 * 1024)),
            fs, dialogService: null, new InlineUiScheduler(),
            preloadController: null, thumbnailCache: null, previewService: null, sink);
        return (controller, sink, clock, bin);
    }

    [Fact(DisplayName = "RV-A10 / RV-D5: a batch that finishes after the folder changed reports a late-completion status with the counts and reloads nothing")]
    public async Task RemoveDuplicates_FolderChangedDuringBatch_ReportsLateCompletion()
    {
        var (controller, sink, clock, bin) = NewBatchOfThree();
        bin.OnRecycle = count =>
        {
            if (count == 1) clock.NextFolder(); // the user opens another folder after the first of three recycles
        };

        await controller.RemoveDuplicatesAsync(removeNumbered: true);

        Assert.Equal(3, bin.Recycled);
        Assert.Equal([Tr.StatusLateBatchRecycled(3, 0)], sink.LateStatuses);
        Assert.Equal(0, sink.Reloads);
        Assert.DoesNotContain(sink.Statuses, status => status == StatusFormatter.BatchDone(3, 0));
    }

    [Fact(DisplayName = "RV-A10: per-file failures are aggregated (2 recycled, 1 IOException -> 1 failure in the status)")]
    public async Task RemoveDuplicates_OneFileFails_StatusCountsTheFailure()
    {
        var (controller, sink, _, bin) = NewBatchOfThree();
        bin.OnRecycle = count =>
        {
            if (count == 2) throw new IOException("locked by another process");
        };

        await controller.RemoveDuplicatesAsync(removeNumbered: true);

        Assert.Equal(StatusFormatter.BatchDone(2, 1), sink.Statuses[^1]);
    }

    private sealed class LateStatusSink : IDuplicateCleanupSink
    {
        public List<string> Statuses { get; } = [];
        public List<string> LateStatuses { get; } = [];
        public int Reloads { get; private set; }
        public void SetStatusText(string status) => Statuses.Add(status);
        public void ShowLateActionStatus(string status) => LateStatuses.Add(status);
        public Task OpenFolderAsync(string folder, string? initialPath = null)
        {
            Reloads++;
            return Task.CompletedTask;
        }
    }

    /// <summary>Fake: deletes the file instead of touching the user's real Recycle Bin (AGENTS.md); a hook can fail or react per call.</summary>
    private sealed class HookedRecycleBin : IRecycleBin
    {
        private int _count;
        public int Recycled => Volatile.Read(ref _count);
        public Action<int>? OnRecycle { get; set; }
        public void SendToRecycleBin(string path)
        {
            OnRecycle?.Invoke(Interlocked.Increment(ref _count));
            File.Delete(path);
        }
        public bool TryRestore(string originalPath, long expectedSize, DateTime expectedLastWriteUtc) => false;
    }

    /// <summary>Fake: deletes the file instead of touching the user's real Recycle Bin (AGENTS.md).</summary>
    private sealed class DeletingRecycleBin : IRecycleBin
    {
        public void SendToRecycleBin(string path) => File.Delete(path);
        public bool TryRestore(string originalPath, long expectedSize, DateTime expectedLastWriteUtc) => false;
    }

    /// <summary>Forwards to the real file system; the first size lookup (the scan's stat pass) simulates a folder switch.</summary>
    private sealed class FolderSwitchingFileSystem(IFileSystem inner, Action onFirstStat) : IFileSystem
    {
        private int _statted;
        public bool FileExists(string path) => inner.FileExists(path);
        public bool DirectoryExists(string path) => inner.DirectoryExists(path);
        public FileStat? GetFileStat(string path)
        {
            if (Interlocked.Increment(ref _statted) == 1) onFirstStat();
            return inner.GetFileStat(path);
        }
        public void Move(string source, string destination) => inner.Move(source, destination);
        public void Copy(string source, string destination) => inner.Copy(source, destination);
        public void Delete(string path) => inner.Delete(path);
        public Stream OpenReadShared(string path, int bufferSize = 65536) => inner.OpenReadShared(path, bufferSize);
        public Stream OpenAppend(string path, bool durable) => inner.OpenAppend(path, durable);
        public void WriteAllTextAtomic(string path, string text, bool durable = true) => inner.WriteAllTextAtomic(path, text, durable);
        public string ReadAllText(string path) => inner.ReadAllText(path);
        public IEnumerable<string> EnumerateFiles(string directory, string pattern = "*") => inner.EnumerateFiles(directory, pattern);
        public IEnumerable<(string Path, FileStat? Stat)> EnumerateFilesWithStat(string directory, Func<string, bool> include, Action<SkippedEntry> onSkipped) =>
            inner.EnumerateFilesWithStat(directory, include, onSkipped);
        public bool TryProbeReadable(string path, out string? failure) => inner.TryProbeReadable(path, out failure);
        public IEnumerable<(string Path, FileStat? Stat)> EnumerateReadableFilesWithStat(string directory, Func<string, bool> include, Action<SkippedEntry> onSkipped) =>
            inner.EnumerateReadableFilesWithStat(directory, include, onSkipped);
        public IEnumerable<string> EnumerateDirectories(string directory) => inner.EnumerateDirectories(directory);
        public void CreateDirectory(string path) => inner.CreateDirectory(path);
    }

    /// <summary>Never reached in this scenario (the scan ends before any file is recycled); refuses if it is.</summary>
    private sealed class UnusedRecycleBin : IRecycleBin
    {
        public void SendToRecycleBin(string path) => throw new NotSupportedException();
        public bool TryRestore(string originalPath, long expectedSize, DateTime expectedLastWriteUtc) => throw new NotSupportedException();
    }

    private sealed class StatusSink : IDuplicateCleanupSink
    {
        public List<string> Statuses { get; } = [];
        public void SetStatusText(string status) => Statuses.Add(status);
        public Task OpenFolderAsync(string folder, string? initialPath = null) => Task.CompletedTask;
    }

    private sealed class InlineUiScheduler : IUiScheduler
    {
        public void Post(Action action) => action();
        public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
        public ValueTask YieldAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private sealed class NullSink : IDuplicateCleanupSink
    {
        public void SetStatusText(string status) { }
        public Task OpenFolderAsync(string folder, string? initialPath = null) => Task.CompletedTask;
    }
}

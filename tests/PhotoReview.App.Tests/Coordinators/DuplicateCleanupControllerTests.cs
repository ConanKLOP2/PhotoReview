using System.IO;
using PhotoReview.Imaging;
using PhotoReview.App.Coordinators;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core;
using PhotoReview.Core.FileActions;
using PhotoReview.Core.IO;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Model;
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
        Assert.Equal([Tr.StatusDuplicateCheckCanceledFolderChanged], sink.Statuses);
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
        public Stream OpenAppendDurable(string path) => inner.OpenAppendDurable(path);
        public Stream OpenAppend(string path, bool durable) => inner.OpenAppend(path, durable);
        public void WriteAllTextAtomic(string path, string text, bool durable = true) => inner.WriteAllTextAtomic(path, text, durable);
        public string ReadAllText(string path) => inner.ReadAllText(path);
        public IEnumerable<string> ReadLines(string path) => inner.ReadLines(path);
        public IEnumerable<string> EnumerateFiles(string directory, string pattern = "*") => inner.EnumerateFiles(directory, pattern);
        public IEnumerable<(string Path, FileStat? Stat)> EnumerateFilesWithStat(string directory, string pattern = "*") => inner.EnumerateFilesWithStat(directory, pattern);
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

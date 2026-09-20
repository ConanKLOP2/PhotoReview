using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PhotoReview.App.Coordinators;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Model;
using PhotoReview.Core.Session;
using PhotoReview.Core.Settings;
using Xunit;

namespace PhotoReview.App.Tests.Coordinators;

public sealed class FolderLoadCoordinatorTests
{
    private sealed class FakeFileSystem : IFileSystem
    {
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Directories { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int StatCount { get; private set; }

        public bool DirectoryExists(string path) => Directories.Contains(Path.GetFullPath(path));
        public bool FileExists(string path) => Files.ContainsKey(Path.GetFullPath(path));
        public FileStat? GetFileStat(string path)
        {
            StatCount++;
            return Files.TryGetValue(Path.GetFullPath(path), out var b) ? new FileStat(b.Length, DateTime.UtcNow) : null;
        }

        public void CreateDirectory(string path) => Directories.Add(Path.GetFullPath(path));
        public void Delete(string path) => Files.Remove(Path.GetFullPath(path));
        public void Copy(string source, string destination) =>
            Files[Path.GetFullPath(destination)] = Files[Path.GetFullPath(source)];
        public void Move(string source, string destination)
        {
            Files[Path.GetFullPath(destination)] = Files[Path.GetFullPath(source)];
            Files.Remove(Path.GetFullPath(source));
        }

        public Stream OpenReadShared(string path, int bufferSize = 65536) =>
            new MemoryStream(Files[Path.GetFullPath(path)], writable: false);
        public Stream OpenAppendDurable(string path) =>
            throw new NotImplementedException();
        public void WriteAllTextAtomic(string path, string text) =>
            Files[Path.GetFullPath(path)] = System.Text.Encoding.UTF8.GetBytes(text);
        public string ReadAllText(string path) =>
            System.Text.Encoding.UTF8.GetString(Files[Path.GetFullPath(path)]);
        public IEnumerable<string> ReadLines(string path) =>
            ReadAllText(path).Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

        public IEnumerable<string> EnumerateFiles(string directory, string pattern = "*")
        {
            var fullDir = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return Files.Keys.Where(k => k.StartsWith(fullDir, StringComparison.OrdinalIgnoreCase) &&
                                         !k.Substring(fullDir.Length).Contains(Path.DirectorySeparatorChar));
        }

        public IEnumerable<string> EnumerateDirectories(string directory) => Enumerable.Empty<string>();
    }

    private sealed class FakeAppPaths : IAppPaths
    {
        public string ConfigFile => @"C:\data\config.json";
        public string JournalFile => @"C:\data\operations.jsonl";
        public string SessionsDir => @"C:\data\Sessions";
        public string LogFile => @"C:\data\logs\app.log";
        public string PreviewCacheDir => @"C:\data\cache";
        public string ThumbnailCacheDir => @"C:\data\thumbnails";
        public string WindowPlacementFile => @"C:\data\placement.json";
    }

    private sealed class FakeClock : IClock
    {
        public DateTime UtcNow => new(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc);
        public long Timestamp => 0;
    }

    private sealed class FakeExplorerOrderProvider : IExplorerOrderProvider
    {
        public Func<string, Task<ExplorerViewSnapshot>>? SnapshotHook { get; set; }

        public Task<ExplorerViewSnapshot> TryGetSnapshotAsync(
            string folder,
            TimeSpan timeout,
            CancellationToken cancellationToken) =>
            TryGetSnapshotProgressiveAsync(folder, timeout, cancellationToken);

        public Task<ExplorerViewSnapshot> TryGetSnapshotProgressiveAsync(
            string folder,
            TimeSpan timeout,
            CancellationToken cancellationToken,
            IProgress<ExplorerQueryProgress>? progress = null,
            int progressiveBatchSize = 16)
        {
            if (SnapshotHook is not null)
            {
                return SnapshotHook(folder);
            }

            return Task.FromResult(MakeSnapshot(folder, [], ExplorerOrderStatus.NativeViewUnavailable));
        }

        public void Dispose() { }
    }

    private sealed class FakeFolderLoadSink : IFolderLoadSink
    {
        public int ResetCachesCount { get; private set; }
        public int CatalogReadyCount { get; private set; }
        public List<(int Index, long PresentationGen)> Presented { get; } = [];
        public int EmptyCount { get; private set; }
        public int OrderAppliedCount { get; private set; }
        public List<(string Folder, Exception Ex)> Failures { get; } = [];

        public void ResetCaches() => ResetCachesCount++;
        public void OnCatalogReady(string folder, int count) => CatalogReadyCount++;
        public Task PresentAsync(int index, long presentationGeneration)
        {
            Presented.Add((index, presentationGeneration));
            return Task.CompletedTask;
        }
        public void OnEmpty(string folder) => EmptyCount++;
        public void OnOrderApplied(int count, int currentIndex) => OrderAppliedCount++;
        public void OnFailed(string folder, Exception exception) => Failures.Add((folder, exception));
    }

    private readonly FakeFileSystem _fs = new();
    private readonly FakeAppPaths _paths = new();
    private readonly FakeClock _clock = new();
    private readonly ReviewCatalog _catalog = new();
    private readonly GenerationClock _genClock = new();
    private readonly FakeExplorerOrderProvider _explorerOrder = new();
    private readonly FakeFolderLoadSink _sink = new();
    private readonly SettingsStore _settingsStore;
    private readonly SessionStore _sessionStore;

    public FolderLoadCoordinatorTests()
    {
        _settingsStore = new SettingsStore(_paths, _fs, new Core.Diagnostics.NullLog());
        _sessionStore = new SessionStore(_paths, _fs);
    }

    private static ExplorerViewSnapshot MakeSnapshot(string folder, IReadOnlyList<string> paths, ExplorerOrderStatus status = ExplorerOrderStatus.Available) =>
        new(folder, paths, [], ExplorerGroupState.None, status, null, DateTime.UtcNow);

    private FolderLoadCoordinator CreateCoordinator() =>
        new(_catalog, _genClock, _explorerOrder, _fs, _sessionStore, _settingsStore, _sink);

    [Fact]
    public async Task LoadAsync_NormalFolder_PopulatesCatalogAndPresentsFirstImage()
    {
        var folder = @"C:\photos";
        _fs.CreateDirectory(folder);
        _fs.WriteAllTextAtomic(@"C:\photos\a.jpg", "img1");
        _fs.WriteAllTextAtomic(@"C:\photos\b.jpg", "img2");

        using var coordinator = CreateCoordinator();
        await coordinator.LoadAsync(folder);

        Assert.Equal(2, _catalog.Count);
        Assert.Equal(1, _sink.ResetCachesCount);
        Assert.Equal(1, _sink.CatalogReadyCount);
        Assert.Equal(2, _fs.StatCount);
        Assert.Single(_sink.Presented);
        Assert.Equal(0, _sink.Presented[0].Index);
        Assert.Empty(_sink.Failures);
    }

    [Fact]
    public async Task LoadAsync_EmptyFolder_CallsOnEmptyAndInvalidatesNavigation()
    {
        var folder = @"C:\empty";
        _fs.CreateDirectory(folder);

        var prevNav = _genClock.CurrentNavigation;

        using var coordinator = CreateCoordinator();
        await coordinator.LoadAsync(folder);

        Assert.Equal(0, _catalog.Count);
        Assert.Equal(1, _sink.EmptyCount);
        Assert.Empty(_sink.Presented);
        Assert.True(_genClock.CurrentNavigation > prevNav);
    }

    [Fact]
    public async Task LoadAsync_DirectoryNotFound_CallsOnFailed()
    {
        using var coordinator = CreateCoordinator();
        await coordinator.LoadAsync(@"C:\non_existent_folder");

        Assert.Single(_sink.Failures);
        Assert.IsType<DirectoryNotFoundException>(_sink.Failures[0].Ex);
    }

    [Fact]
    public async Task LoadAsync_WithExplorerOrder_INV7_IgnoredAfterUserInteraction()
    {
        var folder = @"C:\photos";
        _fs.CreateDirectory(folder);
        var f1 = @"C:\photos\a.jpg";
        var f2 = @"C:\photos\b.jpg";
        _fs.WriteAllTextAtomic(f1, "1");
        _fs.WriteAllTextAtomic(f2, "2");

        var snapshotTcs = new TaskCompletionSource<ExplorerViewSnapshot>();
        _explorerOrder.SnapshotHook = _ => snapshotTcs.Task;

        using var coordinator = CreateCoordinator();
        var loadTask = coordinator.LoadAsync(folder);

        // Chờ catalog sẵn sàng
        while (_sink.CatalogReadyCount == 0)
        {
            await Task.Delay(10);
        }

        // Người dùng tương tác (INV-7): chuyển ảnh -> tăng InteractionGeneration
        _genClock.NextInteraction();

        // Snapshot Explorer trả về thứ tự đảo ngược
        snapshotTcs.SetResult(MakeSnapshot(folder, [f2, f1]));
        await loadTask;

        // INV-7: Explorer order phải bị bỏ qua vì có tương tác
        Assert.Equal(0, _sink.OrderAppliedCount);
        Assert.Equal(f1, _catalog.Paths[0]);
    }

    [Fact]
    public async Task LoadAsync_WithExplorerOrder_AppliedWhenNoInteraction()
    {
        var folder = @"C:\photos";
        _fs.CreateDirectory(folder);
        var f1 = @"C:\photos\a.jpg";
        var f2 = @"C:\photos\b.jpg";
        _fs.WriteAllTextAtomic(f1, "1");
        _fs.WriteAllTextAtomic(f2, "2");

        // Snapshot Explorer đảo ngược thứ tự: b.jpg đứng trước a.jpg
        _explorerOrder.SnapshotHook = _ => Task.FromResult(MakeSnapshot(folder, [f2, f1]));

        using var coordinator = CreateCoordinator();
        await coordinator.LoadAsync(folder);

        // Không có tương tác: thứ tự Explorer được áp dụng
        Assert.Equal(1, _sink.OrderAppliedCount);
        Assert.Equal(f2, _catalog.Paths[0]);
        Assert.Equal(f1, _catalog.Paths[1]);
    }

    [Fact]
    public async Task LoadAsync_DirectFileOpen_MovesInitialToFront()
    {
        var folder = @"C:\photos";
        _fs.CreateDirectory(folder);
        var f1 = @"C:\photos\a.jpg";
        var f2 = @"C:\photos\b.jpg";
        _fs.WriteAllTextAtomic(f1, "1");
        _fs.WriteAllTextAtomic(f2, "2");

        using var coordinator = CreateCoordinator();
        // Mở trực tiếp b.jpg
        await coordinator.LoadAsync(folder, initialPath: f2);

        // b.jpg được đưa lên đầu catalog
        Assert.Equal(f2, _catalog.Paths[0]);
    }

    [Fact]
    public async Task LoadAsync_SizeSort_PreservesScannedMetadataWithSortedEntries()
    {
        var folder = @"C:\photos";
        _fs.CreateDirectory(folder);
        var small = @"C:\photos\small.jpg";
        var large = @"C:\photos\large.jpg";
        _fs.WriteAllTextAtomic(small, "1");
        _fs.WriteAllTextAtomic(large, "12345");
        _settingsStore.Current.ImageSortMode = ImageSortMode.SizeDescending;

        using var coordinator = CreateCoordinator();
        await coordinator.LoadAsync(folder);

        Assert.Equal(large, _catalog.Current?.Path);
        Assert.Equal(5, _catalog.Current?.Length);
        _catalog.SetCurrent(1);
        Assert.Equal(small, _catalog.Current?.Path);
        Assert.Equal(1, _catalog.Current?.Length);
    }
}

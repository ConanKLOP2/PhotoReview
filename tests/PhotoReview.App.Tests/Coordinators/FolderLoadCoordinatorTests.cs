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
using PhotoReview.TestSupport;
using Xunit;

namespace PhotoReview.App.Tests.Coordinators;

[Trait("Category", "HotPath")]
#pragma warning disable CA1001 // _explorerOrder is FakeExplorerOrderProvider, a test double with an empty Dispose() (no real resource) implemented only to satisfy IExplorerOrderProvider
public sealed partial class FolderLoadCoordinatorTests
#pragma warning restore CA1001
{
    private sealed class FakeFileSystem : IFileSystem
    {
        private static readonly string[] LineSeparators = ["\r\n", "\n"];

        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Directories { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int StatCount { get; private set; }
        /// <summary>Times a session file (under <see cref="FakeAppPaths.SessionsDir"/>) was read.</summary>
        public int SessionReadCount { get; private set; }
        /// <summary>Runs on the scan's background thread at the start of a directory listing (used to block a scan).</summary>
        public Action? OnEnumerateFiles { get; set; }

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
        public void WriteAllTextAtomic(string path, string text, bool durable = true) =>
            Files[Path.GetFullPath(path)] = System.Text.Encoding.UTF8.GetBytes(text);
        public string ReadAllText(string path)
        {
            if (path.StartsWith(@"C:\data\Sessions", StringComparison.OrdinalIgnoreCase)) SessionReadCount++;
            return System.Text.Encoding.UTF8.GetString(Files[Path.GetFullPath(path)]);
        }
        public IEnumerable<string> ReadLines(string path) =>
            ReadAllText(path).Split(LineSeparators, StringSplitOptions.None);

        public IEnumerable<string> EnumerateFiles(string directory, string pattern = "*")
        {
            OnEnumerateFiles?.Invoke();
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
            TryGetSnapshotProgressiveAsync(folder, timeout, cancellationToken: cancellationToken);

        public Task<ExplorerViewSnapshot> TryGetSnapshotProgressiveAsync(
            string folder,
            TimeSpan timeout,
            IProgress<ExplorerQueryProgress>? progress = null,
            int progressiveBatchSize = 16,
            CancellationToken cancellationToken = default)
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
        public List<PhotoReview.Core.Session.SessionState> SessionsReceived { get; } = [];
        public void OnCatalogReady(string folder, int count, PhotoReview.Core.Session.SessionState session)
        {
            CatalogReadyCount++;
            SessionsReceived.Add(session);
        }
        public Task PresentAsync(int index, long presentationGeneration)
        {
            Presented.Add((index, presentationGeneration));
            return Task.CompletedTask;
        }
        public void OnEmpty(string folder, PhotoReview.Core.Session.SessionState session)
        {
            EmptyCount++;
            SessionsReceived.Add(session);
        }
        public bool? LastOrderCurrentKept { get; private set; }
        public void OnOrderApplied(int count, int currentIndex, bool currentKept)
        {
            OrderAppliedCount++;
            LastOrderCurrentKept = currentKept;
        }
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
    public async Task LoadAsync_ReadsSessionOnce_AndPassesItToSink()
    {
        var folder = @"C:\photos";
        _fs.CreateDirectory(folder);
        _fs.WriteAllTextAtomic(@"C:\photos\a.jpg", "img1");
        _sessionStore.Save(new SessionState { Folder = folder, CurrentPath = @"C:\photos\a.jpg" });
        var readsBefore = _fs.SessionReadCount;

        using var coordinator = CreateCoordinator();
        await coordinator.LoadAsync(folder);

        Assert.Equal(1, _fs.SessionReadCount - readsBefore);
        var received = Assert.Single(_sink.SessionsReceived);
        Assert.Equal(@"C:\photos\a.jpg", received.CurrentPath);
    }

    [Fact]
    public async Task LoadAsync_EmptyFolder_ReadsSessionOnce_AndPassesItToSink()
    {
        var folder = @"C:\empty";
        _fs.CreateDirectory(folder);
        _sessionStore.Save(new SessionState { Folder = folder, CurrentPath = @"C:\empty\gone.jpg" });
        var readsBefore = _fs.SessionReadCount;

        using var coordinator = CreateCoordinator();
        await coordinator.LoadAsync(folder);

        Assert.Equal(1, _fs.SessionReadCount - readsBefore);
        // An empty folder raises OnCatalogReady(0) then OnEmpty: both carry the one loaded session.
        Assert.Equal(2, _sink.SessionsReceived.Count);
        Assert.Same(_sink.SessionsReceived[0], _sink.SessionsReceived[1]);
        Assert.Equal(@"C:\empty\gone.jpg", _sink.SessionsReceived[0].CurrentPath);
    }

    [Fact]
    public async Task Dispose_DuringBlockedScan_CancelsLoad_AndNoSinkUpdateFollows()
    {
        var folder = @"C:\photos";
        _fs.CreateDirectory(folder);
        _fs.WriteAllTextAtomic(@"C:\photos\a.jpg", "img1");
        var scanEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseScan = new ManualResetEventSlim(false);
        _fs.OnEnumerateFiles = () =>
        {
            scanEntered.TrySetResult();
            releaseScan.Wait(); // the scan is blocked until the test lets it go
        };

        var coordinator = CreateCoordinator();
        var loadTask = coordinator.LoadAsync(folder);
        await scanEntered.Task.WaitAsync(Wait.DefaultTimeout);

        coordinator.Dispose(); // what MainWindow.Window_Closed does via MainViewModel.CloseSession
        releaseScan.Set();
        await loadTask.WaitAsync(Wait.DefaultTimeout);

        Assert.Equal(0, _sink.ResetCachesCount);
        Assert.Equal(0, _sink.CatalogReadyCount);
        Assert.Empty(_sink.Presented);
        Assert.Empty(_sink.Failures);
        Assert.Equal(0, _catalog.Count);
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
        var catalogReadyBarrier = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _explorerOrder.SnapshotHook = _ => snapshotTcs.Task;

        using var coordinator = CreateCoordinator();
        var loadTask = coordinator.LoadAsync(folder);

        var prevCatalogCount = _sink.CatalogReadyCount;
        await Wait.UntilAsync(() => _sink.CatalogReadyCount != prevCatalogCount, "catalog ready event");

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

    private static Task WaitUntilAsync(Func<bool> condition, string what) => Wait.UntilAsync(condition, what);

    private (string A, string B, string C) CreateThreeImages(string folder)
    {
        _fs.CreateDirectory(folder);
        var a = Path.Combine(folder, "a.jpg");
        var b = Path.Combine(folder, "b.jpg");
        var c = Path.Combine(folder, "c.jpg");
        _fs.WriteAllTextAtomic(a, "1");
        _fs.WriteAllTextAtomic(b, "2");
        _fs.WriteAllTextAtomic(c, "3");
        return (a, b, c);
    }

    [Fact]
    public async Task LoadAsync_DirectFileOpen_PresentsRequestedFileBeforeSnapshot_ThenAppliesOrderKeepingIt()
    {
        var folder = @"C:\photos";
        var (a, b, c) = CreateThreeImages(folder);
        var snapshotTcs = new TaskCompletionSource<ExplorerViewSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        _explorerOrder.SnapshotHook = _ => snapshotTcs.Task;

        using var coordinator = CreateCoordinator();
        var loadTask = coordinator.LoadAsync(folder, initialPath: b);

        // INV-9 (perf/startup): the opened file is presented while the snapshot is still pending...
        await WaitUntilAsync(() => _sink.Presented.Count == 1, "first presentation");
        Assert.False(snapshotTcs.Task.IsCompleted);
        Assert.Equal(b, _catalog.Current?.Path);
        // ...and navigation is gated until the order is settled.
        var pendingOrder = coordinator.PendingOrder;
        Assert.False(pendingOrder.IsCompleted);
        Assert.False(loadTask.IsCompleted);

        snapshotTcs.SetResult(MakeSnapshot(folder, [c, b, a]));
        await loadTask;

        Assert.True(pendingOrder.IsCompleted);
        Assert.True(coordinator.PendingOrder.IsCompleted);
        Assert.Equal(1, _sink.OrderAppliedCount);
        Assert.True(_sink.LastOrderCurrentKept);
        Assert.Equal(new[] { c, b, a }, _catalog.Paths);
        // Same image, new index; nothing re-presented.
        Assert.Equal(b, _catalog.Current?.Path);
        Assert.Equal(1, _catalog.CurrentIndex);
        Assert.Single(_sink.Presented);
    }

    [Fact]
    public async Task LoadAsync_DirectFileOpen_UnavailableSnapshot_ReleasesGateAndKeepsFallback()
    {
        var folder = @"C:\photos";
        var (a, b, c) = CreateThreeImages(folder);
        var snapshotTcs = new TaskCompletionSource<ExplorerViewSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        _explorerOrder.SnapshotHook = _ => snapshotTcs.Task;

        using var coordinator = CreateCoordinator();
        var loadTask = coordinator.LoadAsync(folder, initialPath: c);
        await WaitUntilAsync(() => _sink.Presented.Count == 1, "first presentation");
        var pendingOrder = coordinator.PendingOrder;
        Assert.False(pendingOrder.IsCompleted);

        snapshotTcs.SetResult(MakeSnapshot(folder, [], ExplorerOrderStatus.TimedOut));
        await loadTask;

        Assert.True(pendingOrder.IsCompleted);
        Assert.Equal(0, _sink.OrderAppliedCount);
        // Fallback order is unchanged: opened file first, then the natural sort.
        Assert.Equal(new[] { c, a, b }, _catalog.Paths);
        Assert.Equal(c, _catalog.Current?.Path);
    }

    [Fact]
    public async Task LoadAsync_SupersedingLoad_ReleasesPendingOrderGate()
    {
        var folder = @"C:\photos";
        var (_, b, _) = CreateThreeImages(folder);
        var other = @"C:\other";
        _fs.CreateDirectory(other);
        _fs.WriteAllTextAtomic(@"C:\other\x.jpg", "x");
        var snapshotTcs = new TaskCompletionSource<ExplorerViewSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        _explorerOrder.SnapshotHook = f => f.StartsWith(folder, StringComparison.OrdinalIgnoreCase)
            ? snapshotTcs.Task
            : Task.FromResult(MakeSnapshot(f, [], ExplorerOrderStatus.NoMatchingWindow));

        using var coordinator = CreateCoordinator();
        var firstLoad = coordinator.LoadAsync(folder, initialPath: b);
        await WaitUntilAsync(() => _sink.Presented.Count == 1, "first presentation");
        var pendingOrder = coordinator.PendingOrder;
        Assert.False(pendingOrder.IsCompleted);

        await coordinator.LoadAsync(other);
        Assert.True(pendingOrder.IsCompleted, "A superseded file-open load must not keep navigation gated.");
        Assert.True(coordinator.PendingOrder.IsCompleted);

        snapshotTcs.SetResult(MakeSnapshot(folder, []));
        await firstLoad;
        Assert.Equal(0, _sink.OrderAppliedCount);
    }

    [Fact]
    public async Task LoadAsync_FolderOpen_NeverGatesNavigation_AndReportsCurrentReplaced()
    {
        var folder = @"C:\photos";
        var (a, b, c) = CreateThreeImages(folder);
        var snapshotTcs = new TaskCompletionSource<ExplorerViewSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        _explorerOrder.SnapshotHook = _ => snapshotTcs.Task;

        using var coordinator = CreateCoordinator();
        var loadTask = coordinator.LoadAsync(folder);
        await WaitUntilAsync(() => _sink.Presented.Count == 1, "fallback presentation");
        // INV-9b unchanged: a folder open presents the fallback and does not gate navigation.
        Assert.True(coordinator.PendingOrder.IsCompleted);

        snapshotTcs.SetResult(MakeSnapshot(folder, [c, b, a]));
        await loadTask;

        Assert.Equal(1, _sink.OrderAppliedCount);
        Assert.False(_sink.LastOrderCurrentKept);
        Assert.Equal(2, _sink.Presented.Count);
        Assert.Equal(c, _catalog.Current?.Path);
    }

    [Fact]
    public async Task LoadAsync_DirectFileOpen_SnapshotAlreadyIn_AppliesOrderBeforeTheOnlyFrame()
    {
        var folder = @"C:\photos";
        var (a, b, c) = CreateThreeImages(folder);
        _explorerOrder.SnapshotHook = f => Task.FromResult(MakeSnapshot(f, [c, b, a]));

        using var coordinator = CreateCoordinator();
        await coordinator.LoadAsync(folder, initialPath: b);

        // Same end state as when the order arrives after the frame, minus the transient fallback
        // index: the opened file is presented once, already at its Explorer position.
        Assert.Equal(new[] { c, b, a }, _catalog.Paths);
        Assert.Equal(1, Assert.Single(_sink.Presented).Index);
        Assert.Equal(b, _catalog.Current?.Path);
        Assert.Equal(1, _sink.OrderAppliedCount);
        Assert.False(_sink.LastOrderCurrentKept);
        Assert.True(coordinator.PendingOrder.IsCompleted);
    }

    [Fact]
    public async Task LoadAsync_FolderOpen_SnapshotAlreadyIn_PresentsExplorerFirstImageOnly()
    {
        var folder = @"C:\photos";
        var (a, b, c) = CreateThreeImages(folder);
        _explorerOrder.SnapshotHook = f => Task.FromResult(MakeSnapshot(f, [c, b, a]));

        using var coordinator = CreateCoordinator();
        await coordinator.LoadAsync(folder);

        // INV-9b end state (first image of the Explorer order) without the fallback frame first.
        Assert.Equal(0, Assert.Single(_sink.Presented).Index);
        Assert.Equal(c, _catalog.Current?.Path);
        Assert.Equal(1, _sink.OrderAppliedCount);
    }

    [Fact]
    public async Task LoadAsync_StartsExplorerQueryBeforeScanning()
    {
        var folder = @"C:\photos";
        CreateThreeImages(folder);
        var statCountAtQuery = -1;
        _explorerOrder.SnapshotHook = f =>
        {
            statCountAtQuery = _fs.StatCount;
            return Task.FromResult(MakeSnapshot(f, [], ExplorerOrderStatus.NoMatchingWindow));
        };

        using var coordinator = CreateCoordinator();
        await coordinator.LoadAsync(folder);

        // perf(startup): the query (the slowest part of a load) runs in parallel with the scan.
        Assert.Equal(0, statCountAtQuery);
        Assert.Equal(3, _fs.StatCount);
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

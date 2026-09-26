using System.IO;
using PhotoReview.App.Coordinators;
using PhotoReview.Core;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.FileActions;
using PhotoReview.Core.IO;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Model;

namespace PhotoReview.App.Tests.Coordinators;

/// <summary>Q-R25 (option A): Esc cancels the duplicate-check HASHING phase; nothing is applied and nothing is recycled.</summary>
public sealed class DuplicateCheckCancelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PhotoReview_DupCancel_" + Guid.NewGuid().ToString("N"));
    /// <summary>Failure guard only (a broken cancel would hang the test); never a timing assumption.</summary>
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);
    private readonly string[] _files;
    private readonly GenerationClock _clock = new();
    private readonly ReviewCatalog _catalog = new();
    private readonly RecordingRecycleBin _bin = new();
    private readonly StatusSink _sink = new();
    private readonly PhysicalFileSystem _fs = new();

    public DuplicateCheckCancelTests()
    {
        Directory.CreateDirectory(_root);
        _files = [Path.Combine(_root, "a.jpg"), Path.Combine(_root, "a (1).jpg"), Path.Combine(_root, "a (2).jpg")];
        foreach (var file in _files) File.WriteAllBytes(file, new byte[2048]); // same size, hashed as identical => 2 numbered copies
        _catalog.Reset(_files);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private DuplicateCleanupController Create(IFileHasher hasher, IDialogService? dialog = null)
    {
        var fileActions = new FileActionService(new OperationJournal(new AppPaths(_root), _fs, new SystemClock()), _fs, new SystemClock(), _bin);
        return new DuplicateCleanupController(
            _clock, _catalog, fileActions, hasher, _fs, dialog, new InlineUiScheduler(),
            preloadController: null, thumbnailCache: null, previewService: null, _sink);
    }

    [Fact(DisplayName = "Esc mid-hash stops the scan, releases the file handle, applies nothing and says the check was canceled")]
    public async Task CancelDuplicateCheck_MidHash_StopsWithoutRecycling()
    {
        var hasher = new BlockingHasher();
        var controller = Create(hasher);

        var run = controller.RemoveDuplicatesAsync(removeNumbered: true);
        await hasher.FirstStarted.Task.WaitAsync(Guard);
        Assert.Equal(1, hasher.OpenHandles);

        Assert.True(controller.CancelDuplicateCheck());
        await run.WaitAsync(Guard);

        Assert.Equal(0, hasher.OpenHandles);                 // the handle was released, not left running
        Assert.True(hasher.FirstToken.IsCancellationRequested);
        Assert.Equal(1, hasher.Calls);                       // stopped: the remaining files were never hashed
        Assert.Empty(_bin.Recycled);
        Assert.All(_files, file => Assert.True(File.Exists(file)));
        Assert.Equal(Tr.StatusDuplicateCheckCanceled, _sink.Statuses[^1]);
        Assert.False(controller.CancelDuplicateCheck());     // hashing is over: Esc falls through to its normal meaning
    }

    [Fact(DisplayName = "The running status tells the user that Esc cancels")]
    public async Task RemoveDuplicatesAsync_WhileHashing_StatusMentionsEsc()
    {
        var hasher = new BlockingHasher();
        var controller = Create(hasher);

        var run = controller.RemoveDuplicatesAsync(removeNumbered: true);
        await hasher.FirstStarted.Task.WaitAsync(Guard);

        Assert.Equal(Tr.StatusDuplicateCheckRunning, _sink.Statuses[^1]);
        Assert.Contains("Esc", Tr.StatusDuplicateCheckRunning);
        controller.CancelDuplicateCheck();
        await run.WaitAsync(Guard);
    }

    [Fact(DisplayName = "Cancel before any check is a no-op (false, no exception) and does not poison the next check")]
    public async Task CancelDuplicateCheck_BeforeStart_IsNoOp()
    {
        var controller = Create(new InstantHasher());

        Assert.False(controller.CancelDuplicateCheck());
        await controller.RemoveDuplicatesAsync(removeNumbered: true);

        Assert.Equal(2, _bin.Recycled.Count);
        Assert.Equal(Tr.StatusBatchDone(2, 0), _sink.Statuses[^1]);
    }

    [Fact(DisplayName = "Cancel after the check completed is a no-op: it neither throws nor changes the outcome")]
    public async Task CancelDuplicateCheck_AfterCompletion_IsNoOp()
    {
        var controller = Create(new InstantHasher());
        await controller.RemoveDuplicatesAsync(removeNumbered: true);
        var statuses = _sink.Statuses.Count;

        Assert.False(controller.CancelDuplicateCheck());
        Assert.False(controller.CancelDuplicateCheck());

        Assert.Equal(statuses, _sink.Statuses.Count);
        Assert.Equal(2, _bin.Recycled.Count);
    }

    [Fact(DisplayName = "Repeated Esc while hashing is consumed each time and never throws")]
    public async Task CancelDuplicateCheck_Twice_BothConsumeAndDoNotThrow()
    {
        // The hasher ignores the token until released: a cooperative hasher could let the whole check finish between the
        // two Esc presses (then the second one correctly reports "nothing running"), which made this test racy.
        var hasher = new BlockingHasher { IgnoreCancellationUntilReleased = true };
        var controller = Create(hasher);
        var run = controller.RemoveDuplicatesAsync(removeNumbered: true);
        await hasher.FirstStarted.Task.WaitAsync(Guard);

        Assert.True(controller.CancelDuplicateCheck());
        Assert.True(controller.CancelDuplicateCheck());
        hasher.Release();
        await run.WaitAsync(Guard);

        Assert.Empty(_bin.Recycled);
        Assert.Equal(Tr.StatusDuplicateCheckCanceled, _sink.Statuses[^1]);
    }

    [Fact(DisplayName = "Esc pressed as the very last hash finishes still wins: the finished result is discarded, nothing is recycled")]
    public async Task CancelDuplicateCheck_RacesWithLastHash_DiscardsResult()
    {
        DuplicateCleanupController? controller = null;
        var hasher = new InstantHasher { OnCall = calls => { if (calls == 3) controller!.CancelDuplicateCheck(); } };
        controller = Create(hasher);

        await controller.RemoveDuplicatesAsync(removeNumbered: true);

        Assert.Equal(3, hasher.Calls);
        Assert.Empty(_bin.Recycled);
        Assert.Equal(Tr.StatusDuplicateCheckCanceled, _sink.Statuses[^1]);
    }

    [Fact(DisplayName = "Folder switch during the hash still cancels, with the folder-changed message, and recycles nothing")]
    public async Task RemoveDuplicatesAsync_FolderSwitchDuringHash_StillCancels()
    {
        var hasher = new BlockingHasher();
        var controller = Create(hasher);
        var run = controller.RemoveDuplicatesAsync(removeNumbered: true);
        await hasher.FirstStarted.Task.WaitAsync(Guard);

        _clock.NextFolder();
        hasher.Release();
        await run.WaitAsync(Guard);

        Assert.Equal(1, hasher.Calls); // the second same-size file is not read once the folder changed
        Assert.Empty(_bin.Recycled);
        Assert.Equal(Tr.StatusDuplicateCheckCanceledFolderChanged, _sink.Statuses[^1]);
        Assert.False(controller.CancelDuplicateCheck());
    }

    [Fact(DisplayName = "A fresh check supersedes a hashing one: the old token is canceled silently, the new one completes")]
    public async Task RemoveDuplicatesAsync_FreshCheckWhileHashing_CancelsPreviousSilently()
    {
        var hasher = new BlockingHasher { BlockOnlyFirstCall = true };
        var controller = Create(hasher);
        var first = controller.RemoveDuplicatesAsync(removeNumbered: true);
        await hasher.FirstStarted.Task.WaitAsync(Guard);

        await controller.RemoveDuplicatesAsync(removeNumbered: true); // second one hashes instantly
        await first.WaitAsync(Guard);

        Assert.True(hasher.FirstToken.IsCancellationRequested);
        Assert.Equal(2, _bin.Recycled.Count);                        // only the fresh check acted
        Assert.DoesNotContain(Tr.StatusDuplicateCheckCanceled, _sink.Statuses);
        Assert.Equal(Tr.StatusBatchDone(2, 0), _sink.Statuses[^1]);
        Assert.Equal(0, hasher.OpenHandles);
        Assert.False(controller.CancelDuplicateCheck());
    }

    [Fact(DisplayName = "Esc never reaches the review dialog / recycle batch: after hashing, cancel is false and the batch runs unchanged")]
    public async Task CancelDuplicateCheck_DuringBatchReview_IsFalseAndBatchProceeds()
    {
        var dialog = new ReviewDialog();
        var controller = Create(new InstantHasher(), dialog);
        dialog.OnReview = () => dialog.CancelAccepted = controller.CancelDuplicateCheck();

        await controller.RemoveDuplicatesAsync(removeNumbered: true);

        Assert.Equal(1, dialog.Reviews);
        Assert.False(dialog.CancelAccepted);
        Assert.Equal(2, _bin.Recycled.Count);
    }

    [Fact(DisplayName = "Status texts for the cancel gesture exist in English and Vietnamese")]
    public void CancelStatusTexts_AreLocalized()
    {
        Assert.Contains("Esc", Tr.StatusDuplicateCheckRunning);
        Assert.False(string.IsNullOrWhiteSpace(Tr.StatusDuplicateCheckCanceled));
        var vi = File.ReadAllText(FindLanguageFile("vi.json"));
        Assert.Contains("\"status.duplicateCheckRunning\": \"Đang kiểm tra bản trùng lặp... nhấn Esc để hủy.\"", vi);
        Assert.Contains("\"status.duplicateCheckCanceled\": \"Đã hủy kiểm tra bản trùng lặp. Không có tệp nào bị xóa.\"", vi);
    }

    private static string FindLanguageFile(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "PhotoReview.Core", "Localization", "Languages", name);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException(name);
    }

    private sealed class RecordingRecycleBin : IRecycleBin
    {
        public List<string> Recycled { get; } = [];
        public void SendToRecycleBin(string path) { Recycled.Add(path); File.Delete(path); }
        public bool TryRestore(string originalPath, long expectedSize, DateTime expectedLastWriteUtc) => throw new NotSupportedException();
    }

    private sealed class InstantHasher : IFileHasher
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public Action<int>? OnCall { get; init; }
        public Task<string> GetAsync(string path, CancellationToken cancellationToken = default)
        {
            var n = Interlocked.Increment(ref _calls);
            OnCall?.Invoke(n);
            return Task.FromResult("same");
        }
    }

    /// <summary>Models a file handle: opened on entry, released in finally; blocks until released or canceled.</summary>
    private sealed class BlockingHasher : IFileHasher
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;
        private int _open;
        public TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool BlockOnlyFirstCall { get; init; }
        /// <summary>Keeps the hash "running" after a cancel request until <see cref="Release"/>, so a test can call Cancel again while the check is provably still in progress.</summary>
        public bool IgnoreCancellationUntilReleased { get; init; }
        public int Calls => Volatile.Read(ref _calls);
        public int OpenHandles => Volatile.Read(ref _open);
        public CancellationToken FirstToken { get; private set; }
        public void Release() => _gate.TrySetResult();

        public async Task<string> GetAsync(string path, CancellationToken cancellationToken = default)
        {
            var n = Interlocked.Increment(ref _calls);
            if (n == 1) FirstToken = cancellationToken;
            Interlocked.Increment(ref _open);
            try
            {
                if (n == 1) FirstStarted.TrySetResult();
                if (!BlockOnlyFirstCall || n == 1) await (IgnoreCancellationUntilReleased ? _gate.Task : _gate.Task.WaitAsync(cancellationToken));
                return "same";
            }
            finally { Interlocked.Decrement(ref _open); }
        }
    }

    private sealed class ReviewDialog : IDialogService
    {
        public int Reviews { get; private set; }
        public bool CancelAccepted { get; set; }
        public Action? OnReview { get; set; }
        public bool ShowBatchReview(IReadOnlyList<string> paths) { Reviews++; OnReview?.Invoke(); return true; }
        public bool ShowConfirmation(string title, string message) => true;
        public void ShowMessage(string title, string message) { }
        public void ShowError(string title, string message) { }
        public string? PickFolder(string? initialFolder = null) => null;
        public void ShowRecovery() { }
        public void ShowDiagnostics() { }
        public bool ShowSettings() => false;
        public void ShowBenchmark(string? folder = null) { }
        public void ShowSkippedFiles(IReadOnlyList<SkippedEntry> entries) { }
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
}

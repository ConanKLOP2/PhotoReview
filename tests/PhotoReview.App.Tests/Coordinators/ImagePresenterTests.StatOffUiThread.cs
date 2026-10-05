using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PhotoReview.App.Coordinators;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.IO;

namespace PhotoReview.App.Tests.Coordinators;

/// <summary>
/// Q-R29 option C: the navigation stat runs on <see cref="NavigationStatWorker"/>, never on the thread that calls
/// <see cref="ImagePresenter.PresentAsync"/> (the UI thread in the app), and a file that changes or disappears while
/// that stat is pending is still presented fresh. Every wait is a gate the test opens (no fixed delays); each test
/// has its own worker so a held stat never stalls another test's presenter.
/// </summary>
public sealed partial class ImagePresenterTests
{
    /// <summary>
    /// Forwards to the real file system. <see cref="GetFileStat"/> of a held path blocks until released (only off the
    /// caller's thread: a stat on <see cref="CallerThreadId"/> is recorded and never blocks, so a regression fails the
    /// test instead of deadlocking it), and can be overridden per path (missing / throwing).
    /// </summary>
    private sealed class DelayedStatFileSystem(IFileSystem inner) : IFileSystem
    {
        private readonly ConcurrentDictionary<string, ManualResetEventSlim> _held = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _started = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, Func<FileStat?>> _overrides = new(StringComparer.OrdinalIgnoreCase);

        public int CallerThreadId { get; set; } = -1;
        public ConcurrentQueue<string> StatsOnCallerThread { get; } = new();
        public ConcurrentQueue<(string Path, int ThreadId)> Stats { get; } = new();

        public void Hold(string path) => _held[path] = new ManualResetEventSlim(false);
        public void Release(string path) { if (_held.TryGetValue(path, out var gate)) gate.Set(); }
        public void ReleaseAll() { foreach (var gate in _held.Values) gate.Set(); }
        public void Override(string path, Func<FileStat?> result) => _overrides[path] = result;
        public Task StatStarted(string path) => _started.GetOrAdd(path, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;

        public FileStat? GetFileStat(string path)
        {
            var thread = Environment.CurrentManagedThreadId;
            Stats.Enqueue((path, thread));
            _started.GetOrAdd(path, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
            if (thread == CallerThreadId) StatsOnCallerThread.Enqueue(path);
            else if (_held.TryGetValue(path, out var gate)) gate.Wait(TimeSpan.FromSeconds(30)); // safety net only; the test releases it
            return _overrides.TryGetValue(path, out var result) ? result() : inner.GetFileStat(path);
        }

        public bool FileExists(string path) => inner.FileExists(path);
        public bool DirectoryExists(string path) => inner.DirectoryExists(path);
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

    private static readonly TimeSpan GateTimeout = TimeSpan.FromSeconds(20);

    private ImagePresenter CreatePresenterWithDelayedStat(DelayedStatFileSystem fileSystem) => new(
        _catalog,
        _clock,
        _previewService,
        _thumbnailCache,
        _preloadController,
        _compareViewModel,
        _hashService,
        _metrics,
        () => _settings,
        _sessionStore,
        _sink,
        fileSystem: fileSystem,
        getSession: () => null)
    {
        StatWorker = new NavigationStatWorker(),
    };

    [Fact(DisplayName = "Q-R29 C: PresentAsync returns to its caller while the stat is still pending; the stat never runs on the caller's thread")]
    public async Task PresentAsync_WhileStatIsHeld_DoesNotBlockTheCallingThread()
    {
        var photo = CreateFakeImageFile("slow-share.png");
        _catalog.Reset([photo]);
        var fs = new DelayedStatFileSystem(new PhysicalFileSystem());
        fs.Hold(photo);
        var presenter = CreatePresenterWithDelayedStat(fs);
        try
        {
            var callerThread = Environment.CurrentManagedThreadId;
            fs.CallerThreadId = callerThread;
            var present = presenter.PresentAsync(0);
            fs.CallerThreadId = -1;

            // The call came back although the stat has not finished: the calling (UI) thread was never blocked on it.
            await fs.StatStarted(photo).WaitAsync(GateTimeout);
            Assert.False(present.IsCompleted);
            Assert.Empty(_sink.PresentedPaths);
            Assert.Empty(fs.StatsOnCallerThread);

            fs.Release(photo);
            await present.WaitAsync(GateTimeout);

            Assert.Equal([photo], _sink.PresentedPaths);
            Assert.Empty(fs.StatsOnCallerThread);
            Assert.DoesNotContain(fs.Stats, s => s.ThreadId == callerThread);
        }
        finally { fs.ReleaseAll(); }
    }

    [Fact(DisplayName = "Q-R29 C: a file rewritten while its stat is pending is presented as it is now, never from the stale RAM preview")]
    public async Task PresentAsync_FileRewrittenWhileStatPending_NeverShowsTheStaleRamPreview()
    {
        var path = Path.Combine(_tempDir, "edited-on-share.png");
        File.WriteAllBytes(path, CreatePng(1, 1));
        var scanTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, scanTime);
        var scanned = new FileInfo(path);
        _catalog.Reset([new CatalogEntry(path).WithMetadata(scanned.Length, scanned.LastWriteTimeUtc)]);
        var fs = new DelayedStatFileSystem(new PhysicalFileSystem());
        var presenter = CreatePresenterWithDelayedStat(fs);
        await presenter.PresentAsync(0);
        var stale = presenter.CurrentImage;
        Assert.NotNull(stale);
        Assert.Equal(1, presenter.CurrentOriginalWidth);
        _sink.Images.Clear();
        _sink.PresentedPaths.Clear();

        fs.Hold(path);
        try
        {
            var present = presenter.PresentAsync(0);
            await fs.StatStarted(path).WaitAsync(GateTimeout);
            // Edited in another app while the (slow) stat is in flight; the catalog still has the scan's metadata.
            File.WriteAllBytes(path, CreatePng(3, 2));
            File.SetLastWriteTimeUtc(path, scanTime.AddHours(1));
            fs.Release(path);
            await present.WaitAsync(GateTimeout);
        }
        finally { fs.ReleaseAll(); }

        Assert.Equal(3, presenter.CurrentOriginalWidth);
        Assert.Equal(2, presenter.CurrentOriginalHeight);
        Assert.DoesNotContain(_sink.Images, image => ReferenceEquals(image, stale));
        Assert.Equal(new FileInfo(path).Length, _catalog.Find(path)!.Length);
    }

    [Fact(DisplayName = "Q-R29 C: a file deleted while its stat is pending is dropped and the next photo is shown -- its cached preview never is")]
    public async Task PresentAsync_FileDeletedWhileStatPending_SkipsItWithoutShowingItsCachedPreview()
    {
        var doomed = CreateFakeImageFile("doomed.png");
        var survivor = Path.Combine(_tempDir, "survivor.png");
        File.WriteAllBytes(survivor, CreatePng(2, 2));
        _catalog.Reset([doomed, survivor]);
        var fs = new DelayedStatFileSystem(new PhysicalFileSystem());
        var presenter = CreatePresenterWithDelayedStat(fs);
        await presenter.PresentAsync(0);
        var doomedImage = presenter.CurrentImage;
        Assert.NotNull(doomedImage);
        await presenter.PresentAsync(1);
        _sink.Images.Clear();
        _sink.PresentedPaths.Clear();

        fs.Hold(doomed);
        try
        {
            var present = presenter.PresentAsync(0);
            await fs.StatStarted(doomed).WaitAsync(GateTimeout);
            File.Delete(doomed);
            fs.Release(doomed);
            await present.WaitAsync(GateTimeout);
        }
        finally { fs.ReleaseAll(); }

        Assert.Equal([survivor], _catalog.Paths);
        Assert.Equal([survivor], _sink.PresentedPaths);
        Assert.DoesNotContain(_sink.Images, image => ReferenceEquals(image, doomedImage));
        Assert.Equal(2, presenter.CurrentOriginalWidth);
    }

    [Fact(DisplayName = "Q-R29 C: a navigation superseded while its stat is pending touches neither the screen nor the catalog when the stat fails")]
    public async Task PresentAsync_SupersededWhileStatPending_StaleStatErrorIsIgnored()
    {
        var flaky = CreateFakeImageFile("flaky-share.png");
        var next = CreateFakeImageFile("next.png");
        _catalog.Reset([flaky, next]);
        var fs = new DelayedStatFileSystem(new PhysicalFileSystem());
        fs.Override(flaky, () => throw new IOException("share hiccup"));
        fs.Hold(flaky);
        var presenter = CreatePresenterWithDelayedStat(fs);
        Task stale, current;
        try
        {
            stale = presenter.PresentAsync(0);
            await fs.StatStarted(flaky).WaitAsync(GateTimeout);
            current = presenter.PresentAsync(1); // the user moved on while the share was slow
            fs.Release(flaky);
            await Task.WhenAll(stale, current).WaitAsync(GateTimeout);
        }
        finally { fs.ReleaseAll(); }

        Assert.Equal([next], _sink.PresentedPaths);
        Assert.NotNull(_sink.CurrentImage);
        Assert.DoesNotContain(_sink.Images, image => image is null);
        Assert.DoesNotContain(_sink.Statuses, s => s.Contains("share hiccup", StringComparison.Ordinal));
        Assert.Equal([flaky, next], _catalog.Paths);
        Assert.Equal(1, _catalog.CurrentIndex);
    }

    [Fact(DisplayName = "APP-P01: a navigation whose SUCCESSFUL stat resumes after a newer navigation disposed its decode source completes quietly and shows nothing")]
    public async Task PresentAsync_SupersededWhileStatPending_SuccessfulStatResumesWithoutTouchingDisposedSource()
    {
        var first = CreateFakeImageFile("first.png");
        var second = CreateFakeImageFile("second.png");
        _catalog.Reset([first, second]);
        var fs = new DelayedStatFileSystem(new PhysicalFileSystem());
        fs.Hold(first);
        var presenter = CreatePresenterWithDelayedStat(fs);
        Task stale, current;
        try
        {
            stale = presenter.PresentAsync(0);
            await fs.StatStarted(first).WaitAsync(GateTimeout);
            current = presenter.PresentAsync(1); // disposes the first navigation's decode source while its stat is pending
            fs.Release(first); // the first stat now resumes with a Found result, after its source was disposed
            await Task.WhenAll(stale, current).WaitAsync(GateTimeout);
        }
        finally { fs.ReleaseAll(); }

        Assert.True(stale.IsCompletedSuccessfully);
        Assert.Equal([second], _sink.PresentedPaths);
        Assert.Equal(1, _catalog.CurrentIndex);
    }

    [Fact(DisplayName = "Q-R29 C: stats of navigations superseded while queued behind a slow stat are dropped without touching the disk")]
    public async Task PresentAsync_BurstBehindSlowStat_SkipsTheSupersededStats()
    {
        var slow = CreateFakeImageFile("slow.png");
        var skipped = CreateFakeImageFile("skipped.png");
        var last = CreateFakeImageFile("last.png");
        _catalog.Reset([slow, skipped, last]);
        var fs = new DelayedStatFileSystem(new PhysicalFileSystem());
        fs.Hold(slow);
        var presenter = CreatePresenterWithDelayedStat(fs);
        try
        {
            var presents = new List<Task> { presenter.PresentAsync(0) };
            await fs.StatStarted(slow).WaitAsync(GateTimeout);
            presents.Add(presenter.PresentAsync(1)); // queued behind the held stat, then superseded
            presents.Add(presenter.PresentAsync(2));
            fs.Release(slow);
            await Task.WhenAll(presents).WaitAsync(GateTimeout);
        }
        finally { fs.ReleaseAll(); }

        Assert.Equal([last], _sink.PresentedPaths);
        Assert.DoesNotContain(fs.Stats, s => string.Equals(s.Path, skipped, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, _catalog.CurrentIndex);
    }
}

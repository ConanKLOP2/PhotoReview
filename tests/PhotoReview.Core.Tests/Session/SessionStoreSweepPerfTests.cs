using System.Diagnostics;
using System.Globalization;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.IO;
using PhotoReview.Core.Session;
using Xunit.Abstractions;

namespace PhotoReview.Core.Tests.Session;

/// <summary>
/// R14 (whole-project review 2026-09-27): measures the real-disk cost of
/// <see cref="SessionStore.SweepStaleTempFiles"/> against a large population of leftover *.tmp marker
/// files. The initial measurement (real disk, this box): construction blocking on the sweep synchronously
/// cost ~0.25-0.3 ms per stale file -- 100 files ~24 ms, 500 ~120 ms, 1000 ~282 ms, 3000 ~700-1300 ms,
/// material against the ~1.8 s app-start-to-first-image budget (PERF-STATUS.md) since SessionStore is
/// resolved on the UI thread at startup (MainViewModelCompositionRoot). Fix: the sweep now runs on a
/// background <see cref="SessionStore.StartupSweepTask"/> instead of blocking the constructor (see
/// SessionStore.cs for the correctness argument -- Load/Save never touch *.tmp files, and the 1-day age
/// guard means the background sweep can never race a concurrent Save()'s own temp file). These tests
/// pin: (1) construction itself is now near-instant regardless of stale-file count, and (2) the
/// background sweep still completes and removes exactly the stale files. Uses a real temp directory
/// (never the user's actual %LocalAppData%\PhotoReview session folder) and cleans up after itself.
/// </summary>
[Trait("Category", "Native")]
public sealed class SessionStoreSweepPerfTests
{
    private readonly ITestOutputHelper _output;

    public SessionStoreSweepPerfTests(ITestOutputHelper output) => _output = output;

    private sealed class FakeAppPaths : IAppPaths
    {
        public FakeAppPaths(string sessionsDir) => SessionsDir = sessionsDir;
        public string ConfigFile => @"C:\data\config.json";
        public string JournalFile => @"C:\data\operations.jsonl";
        public string SessionsDir { get; }
        public string LogFile => @"C:\data\logs\app.log";
        public string PreviewCacheDir => @"C:\data\cache";
        public string ThumbnailCacheDir => @"C:\data\thumbnails";
        public string WindowPlacementFile => @"C:\data\window-placement.json";
    }

    [Theory(DisplayName = "SessionStore construction stays fast regardless of stale real temp file count (R14 fix)")]
    [InlineData(100)]
    [InlineData(1000)]
    [InlineData(3000)]
    public async Task Constructor_WithManyStaleRealTempFiles_DoesNotBlockOnTheSweep(int staleCount)
    {
        var root = Path.Combine(Path.GetTempPath(), "PhotoReview-R14-Sweep-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var old = DateTime.UtcNow.AddDays(-3);
            for (var i = 0; i < staleCount; i++)
            {
                var path = Path.Combine(root, $"abc{i.ToString(CultureInfo.InvariantCulture)}.json.tmp");
                File.WriteAllText(path, "x");
                File.SetLastWriteTimeUtc(path, old);
            }
            // A handful of fresh temp files and one real (non-.tmp) session file must survive the sweep.
            for (var i = 0; i < 5; i++)
                File.WriteAllText(Path.Combine(root, $"fresh{i.ToString(CultureInfo.InvariantCulture)}.json.tmp"), "x");
            File.WriteAllText(Path.Combine(root, "real-session.json"), "{}");

            var fs = new PhysicalFileSystem();
            var stopwatch = Stopwatch.StartNew();
            var store = new SessionStore(new FakeAppPaths(root), fs);
            stopwatch.Stop();

            _output.WriteLine(
                $"SessionStore construction with {staleCount} stale .tmp files: {stopwatch.Elapsed.TotalMilliseconds:F1} ms " +
                "(sweep now runs in the background; this should stay near-instant at every count)");

            // The whole point of the fix: construction time must not scale with the stale-file count any
            // more. 50 ms is generous headroom above noise (measured well under 5 ms on this box) while
            // still being far below the 100+ ms a synchronous sweep of even 1000 files used to cost.
            Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(50),
                $"SessionStore construction took {stopwatch.Elapsed.TotalMilliseconds:F1} ms with {staleCount} stale temp files -- looks like it is blocking on the sweep again.");

            // The background sweep must still actually run and finish correctly.
            await store.StartupSweepTask;
            Assert.Equal(5, Directory.EnumerateFiles(root, "*.tmp").Count());
            Assert.True(File.Exists(Path.Combine(root, "real-session.json")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact(DisplayName = "SessionStore construction with an empty session directory is near-instant (R14 baseline)")]
    public async Task Constructor_WithEmptyDirectory_IsNearInstant()
    {
        var root = Path.Combine(Path.GetTempPath(), "PhotoReview-R14-Empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var fs = new PhysicalFileSystem();
            var stopwatch = Stopwatch.StartNew();
            var store = new SessionStore(new FakeAppPaths(root), fs);
            stopwatch.Stop();

            _output.WriteLine($"SessionStore construction with empty directory: {stopwatch.Elapsed.TotalMilliseconds:F1} ms");
            Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(50));
            await store.StartupSweepTask;
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}

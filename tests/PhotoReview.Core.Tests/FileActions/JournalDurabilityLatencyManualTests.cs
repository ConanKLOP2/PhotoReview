using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using PhotoReview.Core;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.FileActions;
using PhotoReview.Core.IO;
using PhotoReview.Core.Model;
using Xunit.Abstractions;

namespace PhotoReview.Core.Tests.FileActions;

/// <summary>
/// IO03 measurement (manual): single-file Move latency per journal mode on synthetic files under %TEMP%.
/// "Blocked" = time ExecuteAsync holds the calling (UI) thread before its first real await; "Total" = to completion.
/// Run: dotnet test tests/PhotoReview.Core.Tests -c Release --filter "Category=Manual&amp;FullyQualifiedName~JournalDurabilityLatency" --logger "console;verbosity=detailed"
/// </summary>
[Trait("Category", "Manual")]
public sealed class JournalDurabilityLatencyManualTests(ITestOutputHelper output)
{
    private sealed class NoopRecycleBin : IRecycleBin
    {
        public void SendToRecycleBin(string path) { }
        public bool TryRestore(string originalPath, long expectedSize, DateTime expectedLastWriteUtc) => true;
    }

    private sealed class TempPaths(string root) : IAppPaths
    {
        public string ConfigFile => Path.Combine(root, "config.json");
        public string JournalFile => Path.Combine(root, "operations.jsonl");
        public string SessionsDir => Path.Combine(root, "Sessions");
        public string LogFile => Path.Combine(root, "app.log");
        public string PreviewCacheDir => Path.Combine(root, "cache");
        public string ThumbnailCacheDir => Path.Combine(root, "thumbs");
        public string WindowPlacementFile => Path.Combine(root, "window.json");
    }

    [Theory]
    [InlineData(JournalDurability.Fast)]
    [InlineData(JournalDurability.PowerLossSafe)]
    public async Task MoveLatency(JournalDurability mode)
    {
        const int Count = 300;
        var root = Path.Combine(Path.GetTempPath(), "pr-io03-latency-" + Guid.NewGuid().ToString("N"));
        var photos = Path.Combine(root, "photos");
        Directory.CreateDirectory(photos);
        try
        {
            var fs = new PhysicalFileSystem();
            var clock = new SystemClock();
            var journal = new OperationJournal(new TempPaths(Path.Combine(root, "data")), fs, clock, () => mode);
            var service = new FileActionService(journal, fs, clock, new NoopRecycleBin());
            for (var i = 0; i < Count; i++) File.WriteAllBytes(Path.Combine(photos, $"{i:D4}.jpg"), new byte[64 * 1024]);

            var blocked = new List<double>();
            var total = new List<double>();
            for (var i = 0; i < Count; i++)
            {
                var sw = Stopwatch.StartNew();
                var task = service.ExecuteAsync(new FileActionRequest(Path.Combine(photos, $"{i:D4}.jpg"), FileOperationType.Move, Path.Combine(root, "sel")));
                var blockedMs = sw.Elapsed.TotalMilliseconds;
                var result = await task;
                Assert.True(result.Succeeded);
                if (i >= 20) { blocked.Add(blockedMs); total.Add(sw.Elapsed.TotalMilliseconds); } // skip warm-up
            }

            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{mode}: blocked P50={Percentile(blocked, 50):F2} P95={Percentile(blocked, 95):F2} ms | total P50={Percentile(total, 50):F2} P95={Percentile(total, 95):F2} ms (n={total.Count})"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static double Percentile(List<double> values, int p)
    {
        var sorted = values.Order().ToList();
        return sorted[Math.Min(sorted.Count - 1, (int)Math.Ceiling(p / 100.0 * sorted.Count) - 1)];
    }
}

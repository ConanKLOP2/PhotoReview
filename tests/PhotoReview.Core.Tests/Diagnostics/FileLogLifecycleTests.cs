using System.IO;
using PhotoReview.Core.Diagnostics;

namespace PhotoReview.Core.Tests.Diagnostics;

/// <summary>Lifecycle/robustness of <see cref="FileLog"/>: use after Dispose, dispose under load, odd messages.</summary>
[Trait("Category", "HotPath")]
[Collection("GlobalState")]
public sealed class FileLogLifecycleTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "PhotoReview-FileLogLifecycle-" + Guid.NewGuid().ToString("N"));

    private string LogFile => Path.Combine(_tempDir, "logs", "test.log");

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch (IOException) { /* best effort temp cleanup */ }
    }

    [Fact(DisplayName = "Every public member is a safe no-op after Dispose (no ObjectDisposedException)")]
    public void UseAfterDispose_NeverThrows()
    {
        var log = new FileLog(LogFile) { Enabled = true };
        log.Info("before");
        log.Dispose();

        var ex = Record.Exception(() =>
        {
            log.Info("after");
            log.Warn("after");
            log.Error("after", new InvalidOperationException("x"));
            log.Flush();
            log.Enabled = false;
            log.Shutdown();
            log.Dispose();
        });

        Assert.Null(ex);
    }

    [Fact(DisplayName = "Enabling a disposed log starts no writer thread (it would crash on the released handles)")]
    public void EnableAfterDispose_DoesNotStartWriter()
    {
        var log = new FileLog(LogFile);
        log.Dispose();

        log.Enabled = true;
        log.Info("late");

        Assert.False(log.IsWriterAlive);
        Assert.False(File.Exists(LogFile));
    }

    [Fact(DisplayName = "Dispose drains every entry queued before it to disk")]
    public void Dispose_DrainsEveryQueuedEntryToDisk()
    {
        for (var round = 0; round < 5; round++)
        {
            var path = Path.Combine(_tempDir, "drain" + round, "drain.log");
            var log = new FileLog(path) { Enabled = true };
            for (var i = 0; i < 2_000; i++) log.Info("t0-" + i);

            log.Dispose();

            Assert.Equal(0, log.DroppedCount);
            Assert.Equal(2_000, File.ReadAllLines(path).Length);
        }
    }

    [Fact(DisplayName = "Writers racing Dispose never see an exception and every entry accepted before Dispose is on disk")]
    public void DisposeUnderLoad_WritersNeverThrow()
    {
        for (var round = 0; round < 6; round++)
        {
            var path = Path.Combine(_tempDir, "round" + round, "load.log");
            var log = new FileLog(path) { Enabled = true };
            var errors = new List<Exception>();
            using var start = new ManualResetEventSlim(false);
            var threads = Enumerable.Range(0, 6).Select(t => new Thread(() =>
            {
                start.Wait();
                try
                {
                    for (var i = 0; i < 1_500; i++) log.Info("t" + t + "-" + i);
                }
                catch (Exception e)
                {
                    lock (errors) errors.Add(e);
                }
            })).ToArray();
            foreach (var thread in threads) thread.Start();
            start.Set();
            // Deterministically land Dispose inside the writers' active window instead of a fixed-delay
            // sleep: wait for real evidence that at least one entry has actually been queued (round varies
            // how many, so different rounds race Dispose against different amounts of in-flight work),
            // then race Dispose against the still-running writer threads immediately.
            var queuedThreshold = round * 25;
            Assert.True(
                SpinWait.SpinUntil(() => log.PendingCount > queuedThreshold || threads.All(t => !t.IsAlive), TimeSpan.FromSeconds(10)),
                $"writers never queued more than {queuedThreshold} entries within the timeout (round {round}).");
            log.Dispose();
            foreach (var thread in threads) Assert.True(thread.Join(30_000), "writer thread hung");
            Assert.Empty(errors);
            AssertOnDiskIsPerThreadPrefix(path, log.DroppedCount);
        }
    }

    /// <summary>
    /// Entries are dropped only once the log stops accepting, so per writer thread what reached the disk is the contiguous
    /// prefix 0..k-1 (no gaps, no duplicates, no torn lines) when nothing was dropped. When the shared queue overflowed
    /// (<paramref name="dropped"/> != 0) gaps are legitimate, but the retained entries for a given thread must still be
    /// in strictly increasing, duplicate-free order (no reordering/duplication introduced by the race with Dispose), and
    /// any gap below the last retained index must be small enough to be explained by the documented overflow-drop path.
    /// The deterministic drain-on-Dispose guard is <see cref="Dispose_DrainsEveryQueuedEntryToDisk"/>.
    /// </summary>
    private static void AssertOnDiskIsPerThreadPrefix(string path, long dropped)
    {
        var seen = new Dictionary<int, List<int>>();
        if (File.Exists(path))
        {
            foreach (var line in File.ReadAllLines(path))
            {
                var m = System.Text.RegularExpressions.Regex.Match(line, @"^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3} \[INFO\] \[T\d+\] t(\d+)-(\d+)$");
                Assert.True(m.Success, "torn or malformed log line: " + line);
                var t = int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                if (!seen.TryGetValue(t, out var list)) seen[t] = list = [];
                list.Add(int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        foreach (var (t, list) in seen)
        {
            // Holds regardless of drops: a writer thread's own entries are enqueued in order, and the shared
            // queue only ever drops from the front, so whatever survives for this thread must still appear in
            // the order it was produced -- no reordering, no duplicate replay of the same entry.
            for (var i = 1; i < list.Count; i++)
                Assert.True(list[i] > list[i - 1],
                    $"thread {t}: retained entry {list[i]} is not strictly after {list[i - 1]} (reordered or duplicated retained entry)");

            if (dropped == 0)
            {
                // No drops anywhere: every writer's retained sequence must be the full contiguous prefix 0..k-1.
                Assert.Equal(Enumerable.Range(0, list.Count), list);
            }
            else if (list.Count > 0)
            {
                // Drops happened somewhere in the shared queue, so gaps in this thread's sequence are legitimate --
                // but the number of missing indices below the highest retained one can never exceed the total
                // dropped count (a bug losing entries some other way, e.g. unrelated to the documented overflow
                // path, would produce a gap the drop count cannot account for).
                var missingBelowMax = list[^1] + 1 - list.Count;
                Assert.True(missingBelowMax <= dropped,
                    $"thread {t}: {missingBelowMax} entries missing below index {list[^1]} exceeds total dropped count {dropped}");
            }
        }
    }

    [Fact(DisplayName = "Multi-line messages, unicode and embedded braces are written verbatim")]
    public void OddMessages_WrittenVerbatim()
    {
        using var log = new FileLog(LogFile) { Enabled = true };

        log.Info("line1\nline2 {0} {name} {{x}} İı 日本語 مرحبا");
        log.Info(string.Empty);
        log.Flush();

        var text = File.ReadAllText(LogFile);
        Assert.Contains("line1\nline2 {0} {name} {{x}} İı 日本語 مرحبا", text.Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    [Theory(DisplayName = "Timestamps and thread ids are culture independent (log is machine read)")]
    [InlineData("ar-SA")]
    [InlineData("th-TH")]
    [InlineData("fa-IR")]
    public void Timestamp_IsCultureIndependent(string cultureName)
    {
        var culture = new System.Globalization.CultureInfo(cultureName);
        var previous = System.Globalization.CultureInfo.DefaultThreadCurrentCulture;
        try
        {
            // The writer thread does not inherit the caller's CurrentCulture, it takes the process default.
            System.Globalization.CultureInfo.DefaultThreadCurrentCulture = culture;
            using var log = new FileLog(LogFile) { Enabled = true };
            log.Info("marker");
            log.Flush();
        }
        finally
        {
            System.Globalization.CultureInfo.DefaultThreadCurrentCulture = previous;
        }

        var line = File.ReadAllLines(LogFile).Single(l => l.Contains("marker", StringComparison.Ordinal));
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} \[INFO\] \[T\d+\] marker$", line);
        var year = int.Parse(line[..4], System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(year, DateTime.Now.Year - 1, DateTime.Now.Year + 1); // Gregorian, not Buddhist/Hijri
    }
}

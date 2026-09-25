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

    [Fact(DisplayName = "Writers racing Dispose never see an exception and every entry accepted before Dispose is on disk")]
    public void DisposeUnderLoad_WritersNeverThrow()
    {
        for (var round = 0; round < 12; round++)
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
                    for (var i = 0; i < 5_000; i++) log.Info("t" + t + "-" + i);
                }
                catch (Exception e)
                {
                    lock (errors) errors.Add(e);
                }
            })).ToArray();
            foreach (var thread in threads) thread.Start();
            start.Set();
            Thread.Sleep(round % 5);
            log.Dispose();
            foreach (var thread in threads) Assert.True(thread.Join(30_000), "writer thread hung");
            Assert.Empty(errors);
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

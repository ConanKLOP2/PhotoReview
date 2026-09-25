using System.IO;
using PhotoReview.Core.Diagnostics;
using Xunit;

namespace PhotoReview.Core.Tests.Diagnostics;

[Trait("Category", "HotPath")]
public sealed class FileLogTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _logFile;

    public FileLogTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "PhotoReview-FileLogTests-" + Guid.NewGuid().ToString("N"));
        _logFile = Path.Combine(_tempDir, "logs", "test.log");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch { }
    }

    // A log path whose parent is an existing regular file can never be created: every drain fails.
    private string UnwritableLogPath()
    {
        Directory.CreateDirectory(_tempDir);
        var blocker = Path.Combine(_tempDir, "blocker");
        File.WriteAllText(blocker, "x");
        return Path.Combine(blocker, "logs", "test.log");
    }

    [Fact(DisplayName = "R2-F-23: queue is bounded when the log file cannot be written")]
    public void QueueIsBoundedWhenLogFileCannotBeWritten()
    {
        using var log = new FileLog(UnwritableLogPath()) { Enabled = true };

        for (var i = 0; i < FileLog.MaxQueuedEntries * 3; i++) log.Info("entry " + i);

        Assert.True(log.PendingCount <= FileLog.MaxQueuedEntries, $"pending={log.PendingCount}");
        Assert.True(log.DroppedCount >= FileLog.MaxQueuedEntries * 2);
    }

    [Fact(DisplayName = "R2-F-23: Shutdown does not stall on an unwritable log file")]
    public void ShutdownDoesNotStallOnUnwritableLogFile()
    {
        var log = new FileLog(UnwritableLogPath()) { Enabled = true };
        log.Info("lost");

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        log.Shutdown();
        stopwatch.Stop();
        log.Dispose();

        // Before the fix Flush waited its whole 2 s budget (Join + Flush up to 4 s) for a queue that could never drain.
        Assert.True(stopwatch.ElapsedMilliseconds < 1500, $"Shutdown took {stopwatch.ElapsedMilliseconds} ms");
    }

    [Fact(DisplayName = "INV-10: Disabled logging creates no directory or file")]
    public void DisabledLoggingCreatesNoDirectoryOrFile()
    {
        using var log = new FileLog(_logFile);
        Assert.False(log.Enabled);

        log.Info("disabled-info");
        log.Warn("disabled-warn");
        log.Error("disabled-error", new InvalidOperationException("disabled-exception"));
        log.Flush();

        Assert.False(Directory.Exists(Path.GetDirectoryName(_logFile)));
        Assert.False(File.Exists(_logFile));
    }

    [Fact(DisplayName = "Explicitly enabled logging writes info, warn, and error with exceptions")]
    public void ExplicitlyEnabledLoggingWritesFormattedEntries()
    {
        using var log = new FileLog(_logFile)
        {
            Enabled = true
        };

        log.Info("info-message");
        log.Warn("warn-message");
        log.Error("error-message", new InvalidOperationException("boom-error"));
        log.Flush();

        Assert.True(File.Exists(_logFile));
        var content = File.ReadAllText(_logFile);
        Assert.Contains("[INFO]", content);
        Assert.Contains("info-message", content);
        Assert.Contains("[WARN]", content);
        Assert.Contains("warn-message", content);
        Assert.Contains("[ERROR]", content);
        Assert.Contains("error-message", content);
        Assert.Contains("System.InvalidOperationException: boom-error", content);
    }

    [Fact(DisplayName = "Disabling logging stops all subsequent writes")]
    public void DisablingLoggingStopsWrites()
    {
        using var log = new FileLog(_logFile)
        {
            Enabled = true
        };

        log.Info("enabled-message");
        log.Flush();
        var initialContent = File.ReadAllText(_logFile);

        log.Enabled = false;
        log.Info("after-disabled-message");
        log.Flush();

        var finalContent = File.ReadAllText(_logFile);
        Assert.Equal(initialContent, finalContent);
        Assert.DoesNotContain("after-disabled-message", finalContent);
    }

    [Fact(DisplayName = "Concurrent logging preserves all entries")]
    public void ConcurrentLoggingPreservesAllEntries()
    {
        using var log = new FileLog(_logFile)
        {
            Enabled = true
        };

        var markers = Enumerable.Range(0, 100).Select(i => $"concurrent-log-{i:D3}").ToArray();
        Parallel.ForEach(markers, marker => log.Info(marker));
        log.Flush();

        var content = File.ReadAllText(_logFile);
        Assert.All(markers, m => Assert.Contains(m, content));
    }

    [Fact(DisplayName = "CORE-02: Dispose after a writer join timeout leaves the handles usable so the writer exits cleanly")]
    public void DisposeWithBlockedWriterDoesNotDisposeHandlesUnderTheWriter()
    {
        using var gate = new ManualResetEventSlim(false);
        var log = new FileLog(_logFile);
        log.DrainHook = () => { if (Thread.CurrentThread.Name == "PhotoReview.LogWriter") gate.Wait(TimeSpan.FromSeconds(30)); };
        log.Enabled = true; // writer starts and blocks in its first drain
        log.Info("pending");
        log.Flush();        // waits (and times out) on _drained, which allocates its kernel handle -- the one Dispose would tear down

        log.Dispose();      // join times out (~2 s) while the writer is still blocked
        Assert.True(log.IsWriterAlive);
        Assert.False(log.HandlesReleased, "handles were disposed while the writer still uses them");

        gate.Set();         // writer resumes: Drain's finally sets _drained, loop exits
        Assert.True(log.WaitWriterExit(10_000), "writer thread did not exit");
        Assert.True(log.HandlesReleased, "deferred handle release should happen once the writer exits");
    }

    [Fact(DisplayName = "Log file rotates to .1 backup when size threshold is reached")]
    public void LogFileRotatesAtThreshold()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_logFile)!);
        // Plant 2 KB file with 1 KB threshold
        File.WriteAllBytes(_logFile, new byte[2048]);
        var backupPath = Path.Combine(Path.GetDirectoryName(_logFile)!, Path.GetFileNameWithoutExtension(_logFile) + ".1" + Path.GetExtension(_logFile));

        using var log = new FileLog(_logFile, maxLogBytes: 1024)
        {
            Enabled = true
        };

        log.Info("post-rotation-entry");
        log.Flush();

        Assert.True(File.Exists(backupPath), "Backup .1 file should exist after rotation");
        Assert.True(new FileInfo(backupPath).Length >= 2048);
        Assert.True(File.Exists(_logFile));
        Assert.True(new FileInfo(_logFile).Length < 2048);
        Assert.Contains("post-rotation-entry", File.ReadAllText(_logFile));
    }
}


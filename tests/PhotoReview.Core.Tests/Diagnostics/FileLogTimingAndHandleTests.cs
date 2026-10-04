using System.Diagnostics;
using System.Reflection;
using PhotoReview.Core.Diagnostics;

namespace PhotoReview.Core.Tests.Diagnostics;

/// <summary>
/// Flush/Shutdown budgets, wait-handle ownership and queue-cap boundaries of <see cref="FileLog"/>. A "stuck writer" is a
/// writer thread parked in <see cref="FileLog.DrainHook"/>, which makes the queue non-empty while nothing drains it.
/// </summary>
[Trait("Category", "HotPath")]
[Collection("GlobalState")]
public sealed class FileLogTimingAndHandleTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "PhotoReview-FileLogTiming-" + Guid.NewGuid().ToString("N"));
    private readonly ManualResetEventSlim _entered = new(false);
    private readonly ManualResetEventSlim _gate = new(false);
    private readonly ManualResetEventSlim _gateAfterBatch = new(false);
    private volatile bool _afterBatch;

    private string LogFile => Path.Combine(_tempDir, "logs", "test.log");

    public void Dispose()
    {
        _gate.Set();
        _gateAfterBatch.Set();
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch (IOException) { /* best effort temp cleanup */ }
        _entered.Dispose();
        _gate.Dispose();
        _gateAfterBatch.Dispose();
    }

    /// <summary>An enabled log whose writer is parked in the drain hook with one entry queued.</summary>
    private FileLog StuckWriterLog(string message = "stuck-entry")
    {
        var log = new FileLog(LogFile)
        {
            DrainHook = () =>
            {
                _entered.Set();
                if (_afterBatch) _gateAfterBatch.Wait(TimeSpan.FromSeconds(20));
                else _gate.Wait(TimeSpan.FromSeconds(20));
            },
            AfterBatchWrittenHook = () => _afterBatch = true,
        };
        log.Enabled = true;
        log.Info(message);
        Assert.True(_entered.Wait(TimeSpan.FromSeconds(10)), "writer never reached the drain hook");
        return log;
    }

    // A bounded timed wait on a never-signalled event: the moment being waited out (Flush starting to poll, the 1 s flush
    // interval elapsing) has no observable signal.
    private static void Pause(int milliseconds)
    {
        using var never = new ManualResetEventSlim(false);
        never.Wait(milliseconds);
    }

    private static T GetField<T>(FileLog log, string name) =>
        (T)typeof(FileLog).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(log)!;

    private static void SetField(FileLog log, string name, object value) =>
        typeof(FileLog).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(log, value);

    [Fact(DisplayName = "Shutdown with a stuck writer spends one shared ~2 s budget on join plus flush, not two")]
    public void Shutdown_StuckWriter_UsesOneSharedBudget()
    {
        var log = StuckWriterLog();

        var stopwatch = Stopwatch.StartNew();
        log.Shutdown();
        stopwatch.Stop();

        _gate.Set();
        _gateAfterBatch.Set();
        Assert.True(log.WaitWriterExit(10_000));
        log.Dispose();
        Assert.InRange(stopwatch.ElapsedMilliseconds, 1_500, 3_900);
    }

    [Fact(DisplayName = "Dispose with a stuck writer keeps the handles until the writer exits, enabling the disposed log is a no-op, and the handles are then released")]
    public void Dispose_StuckWriter_DefersHandleRelease()
    {
        var log = StuckWriterLog();
        var signal = GetField<AutoResetEvent>(log, "_signal");

        log.Dispose();

        Assert.True(log.IsWriterAlive);
        Assert.False(log.HandlesReleased);
        Assert.False(signal.SafeWaitHandle.IsClosed);

        log.Enabled = true; // must not revive a disposed log
        Assert.False(log.Enabled);

        _gate.Set();
        _gateAfterBatch.Set();
        Assert.True(log.WaitWriterExit(10_000), "writer did not exit after Dispose");
        Assert.True(log.HandlesReleased);
        Assert.True(signal.SafeWaitHandle.IsClosed);
        Assert.Contains("stuck-entry", File.ReadAllText(LogFile));
    }

    [Fact(DisplayName = "A plain Dispose closes the wait handles")]
    public void Dispose_ClosesWaitHandles()
    {
        var log = new FileLog(LogFile) { Enabled = true };
        var signal = GetField<AutoResetEvent>(log, "_signal");
        log.Info("x");

        log.Dispose();

        Assert.True(log.HandlesReleased);
        Assert.True(signal.SafeWaitHandle.IsClosed);
    }

    [Fact(DisplayName = "Flush returns at once when the wake-up signal was already disposed under it")]
    public void Flush_SignalDisposed_ReturnsImmediately()
    {
        var log = StuckWriterLog();
        var original = GetField<AutoResetEvent>(log, "_signal");
        var dead = new AutoResetEvent(false);
        dead.Dispose();
        SetField(log, "_signal", dead);
        try
        {
            var stopwatch = Stopwatch.StartNew();
            log.Flush();
            stopwatch.Stop();

            Assert.True(stopwatch.ElapsedMilliseconds < 1_000, $"Flush took {stopwatch.ElapsedMilliseconds} ms");
        }
        finally
        {
            SetField(log, "_signal", original);
            _gate.Set();
            log.Dispose();
        }
    }

    [Fact(DisplayName = "Flush returns at once when the drained event was already disposed under it")]
    public void Flush_DrainedEventDisposed_ReturnsImmediately()
    {
        var log = StuckWriterLog();
        var original = GetField<ManualResetEventSlim>(log, "_drained");
        var dead = new ManualResetEventSlim(false);
        dead.Dispose();
        SetField(log, "_drained", dead);
        try
        {
            // Count the exceptions thrown on this thread: Flush must give up on the first one instead of retrying
            // (a swallowed ObjectDisposedException would re-throw on every spin until the 2 s budget is used up).
            var thrown = 0;
            var threadId = Environment.CurrentManagedThreadId;
            void OnFirstChance(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
            {
                if (e.Exception is ObjectDisposedException && Environment.CurrentManagedThreadId == threadId) thrown++;
            }
            AppDomain.CurrentDomain.FirstChanceException += OnFirstChance;
            try
            {
                var stopwatch = Stopwatch.StartNew();
                log.Flush();
                stopwatch.Stop();

                Assert.True(stopwatch.ElapsedMilliseconds < 1_000, $"Flush took {stopwatch.ElapsedMilliseconds} ms");
            }
            finally
            {
                AppDomain.CurrentDomain.FirstChanceException -= OnFirstChance;
            }
            Assert.InRange(thrown, 1, 3);
        }
        finally
        {
            SetField(log, "_drained", original);
            _gate.Set();
            log.Dispose();
        }
    }

    [Fact(DisplayName = "Flush keeps polling while the drained event reads set but an entry is still queued, until it is on disk")]
    public void Flush_DrainedSetWhileEntryQueued_WaitsUntilWritten()
    {
        var log = StuckWriterLog("polled-entry");
        var original = GetField<ManualResetEventSlim>(log, "_drained");
        SetField(log, "_drained", new ManualResetEventSlim(true));
        try
        {
            // A dedicated thread: the pool may be saturated by parallel test collections and would delay the release past Flush's budget.
            new Thread(() => { Pause(400); _gate.Set(); }) { IsBackground = true }.Start();

            log.Flush();

            Assert.True(File.Exists(LogFile), "Flush returned before the queued entry was written");
            Assert.Contains("polled-entry", File.ReadAllText(LogFile));
        }
        finally
        {
            SetField(log, "_drained", original);
            _gate.Set();
            log.Dispose();
        }
    }

    [Fact(DisplayName = "A Flush waiting on a busy writer is released as soon as the drain that wrote the entry finishes")]
    public void Flush_ReleasedWhenDrainFinishes()
    {
        var log = StuckWriterLog("released-entry");
        try
        {
            using var flushed = new ManualResetEventSlim(false);
            new Thread(() => { log.Flush(); flushed.Set(); }) { IsBackground = true }.Start();
            Pause(200); // let Flush start waiting on the drained event

            _gate.Set(); // writer drains and writes the entry, then parks in the next (empty) drain's hook

            Assert.True(flushed.Wait(TimeSpan.FromSeconds(1)), "Flush was not released when the drain finished");
            Assert.Contains("released-entry", File.ReadAllText(LogFile));
        }
        finally
        {
            _gateAfterBatch.Set();
            log.Dispose();
        }
    }

    [Fact(DisplayName = "The queue holds exactly MaxQueuedEntries without dropping, and drops the oldest only past it")]
    public void QueueCap_Boundary()
    {
        Directory.CreateDirectory(_tempDir);
        var blocker = Path.Combine(_tempDir, "blocker");
        File.WriteAllText(blocker, "x"); // a file where a directory is needed: every drain fails, entries stay queued
        using var log = new FileLog(Path.Combine(blocker, "logs", "test.log")) { Enabled = true };

        for (var i = 0; i < FileLog.MaxQueuedEntries; i++) log.Info("entry " + i);
        Assert.Equal(FileLog.MaxQueuedEntries, log.PendingCount);
        Assert.Equal(0, log.DroppedCount);

        log.Info("one too many");
        Assert.Equal(FileLog.MaxQueuedEntries, log.PendingCount);
        Assert.Equal(1, log.DroppedCount);
    }

    [Fact(DisplayName = "An enabled log with nothing queued never creates its directory or file")]
    public void EnabledButIdle_CreatesNothingOnDisk()
    {
        var drains = 0;
        using var log = new FileLog(LogFile) { DrainHook = () => Interlocked.Increment(ref drains) };
        log.Enabled = true;

        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref drains) >= 2, TimeSpan.FromSeconds(10)), "writer did not drain twice");

        Assert.False(Directory.Exists(Path.GetDirectoryName(LogFile)));
        Assert.False(File.Exists(LogFile));
    }

    [Fact(DisplayName = "A log file exactly at the size limit is rotated before the next entry is appended")]
    public void LogAtExactLimit_IsRotated()
    {
        Directory.CreateDirectory(_tempDir);
        var path = Path.Combine(_tempDir, "app.log");
        File.WriteAllBytes(path, new byte[1024]);

        using var log = new FileLog(path, maxLogBytes: 1024) { Enabled = true };
        log.Info("after-rotation");
        log.Flush();

        var backup = Path.Combine(_tempDir, "app.1.log");
        Assert.True(File.Exists(backup));
        Assert.Equal(1024, new FileInfo(backup).Length);
        var text = File.ReadAllText(path);
        Assert.Contains("after-rotation", text);
        Assert.True(new FileInfo(path).Length < 1024);
    }
}

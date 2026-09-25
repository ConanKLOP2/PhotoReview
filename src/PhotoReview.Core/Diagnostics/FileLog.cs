using System.Collections.Concurrent;
using System.IO;
using System.Text;
using PhotoReview.Core.Abstractions;

namespace PhotoReview.Core.Diagnostics;

/// <summary>
/// Thread-safe file-backed implementation of <see cref="ILog"/> with asynchronous worker draining,
/// size-based log rotation (10 MB), and bounded non-spinning flush.
/// Preserves Invariant INV-10 (disabled by default, zero I/O when disabled).
/// </summary>
public sealed class FileLog : ILog, IDisposable
{
    private sealed record Entry(string Level, string Message, Exception? Exception, DateTime Timestamp, int ThreadId);

    private readonly object _sync = new();
    private readonly ConcurrentQueue<Entry> _queue = new();
    private readonly AutoResetEvent _signal = new(false);
    private readonly ManualResetEventSlim _drained = new(true);
    private readonly long _maxLogBytes;
    private readonly string _filePath;

    private volatile bool _enabled;
    private volatile bool _stopping;
    private volatile bool _writing;
    private volatile bool _lastDrainFailed;
    private int _queuedCount;
    private long _dropped;
    private Thread? _writer;
    private bool _disposed;

    private static readonly Lazy<FileLog> DefaultLazy = new(() => new FileLog(PhotoReview.Core.AppPaths.FromEnvironment()));
    public static FileLog Default => DefaultLazy.Value;

    public FileLog(IAppPaths appPaths, long maxLogBytes = 10 * 1024 * 1024)
        : this(appPaths?.LogFile ?? throw new ArgumentNullException(nameof(appPaths)), maxLogBytes)
    {
    }

    public FileLog(string filePath, long maxLogBytes = 10 * 1024 * 1024)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = Path.GetFullPath(filePath);
        _maxLogBytes = Math.Max(1024, maxLogBytes);
    }

    /// <summary>R2-F-23: hard cap on queued entries; when the log file cannot be written the oldest entries are dropped.</summary>
    internal const int MaxQueuedEntries = 10_000;

    internal int PendingCount => Volatile.Read(ref _queuedCount);

    internal long DroppedCount => Interlocked.Read(ref _dropped);

    public string FilePath => _filePath;

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (value)
            {
                Start();
            }
            else
            {
                _enabled = false;
                Signal();
                Flush();
            }
        }
    }

    public void Info(string message) => Write("INFO", message, null);

    public void Warn(string message) => Write("WARN", message, null);

    public void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    /// <summary>
    /// Blocks (without spinning) until the writer thread reports the queue drained,
    /// or until the 2000ms bounded timeout elapses.
    /// </summary>
    public void Flush() => Flush(2000);

    // Returns early when the last drain attempt failed (log file unavailable): waiting cannot help, and the UI thread
    // (Shutdown / Enabled = false) must not stall for the whole timeout on an unwritable log.
    private void Flush(int timeoutMs)
    {
        var remaining = timeoutMs;
        while (((!_queue.IsEmpty && !_lastDrainFailed) || _writing) && remaining > 0 && !HandlesReleased)
        {
            try { _signal.Set(); } catch (ObjectDisposedException) { return; } // disposed under us: nothing left to wait for
            var start = Environment.TickCount64;
            // _drained can be set while an entry is already (or about to be) queued: the writer's empty-queue Drain may run
            // between Write's Reset and Enqueue. Waiting on a set event returns at once and would burn the whole budget in a
            // spin, returning before the entry is written, so back off briefly and re-check the real condition instead.
            try
            {
                if (_drained.IsSet) Thread.Sleep(1);
                else _drained.Wait(remaining);
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            remaining -= (int)Math.Max(1, Environment.TickCount64 - start);
        }
    }

    public void Shutdown()
    {
        _stopping = true;
        _enabled = false;
        Signal();
        // One shared 2 s budget for join + flush (was up to 4 s on the UI thread).
        var start = Environment.TickCount64;
        _writer?.Join(2000);
        Flush((int)Math.Max(0, 2000 - (Environment.TickCount64 - start)));
    }

    /// <summary>Test seam (CORE-02): invoked by the writer at the start of every drain.</summary>
    internal Action? DrainHook { get; set; }

    internal bool IsWriterAlive => _writer is { IsAlive: true };

    internal bool WaitWriterExit(int timeoutMs) => _writer?.Join(timeoutMs) ?? true;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Shutdown();
        // CORE-02: if the writer did not exit within the join budget (blocked in I/O) it is still using these handles
        // (Drain's finally calls _drained.Set, the loop waits on _signal). Disposing them now would throw
        // ObjectDisposedException on that background thread; leave them to the finalizer instead.
        // The writer releases them itself when it finally exits (WriterLoop).
        if (_writer is { IsAlive: true }) return;
        ReleaseHandles();
    }

    private int _handlesReleased;

    internal bool HandlesReleased => Volatile.Read(ref _handlesReleased) != 0;

    private void ReleaseHandles()
    {
        if (Interlocked.Exchange(ref _handlesReleased, 1) != 0) return;
        _signal.Dispose();
        _drained.Dispose();
    }

    private void Start()
    {
        lock (_sync)
        {
            // Enabling a disposed log must be a no-op: a new writer thread would wait on the released handles and
            // die with an unhandled ObjectDisposedException, taking the process down.
            if (_disposed || HandlesReleased) return;
            _stopping = false;
            _enabled = true;
            if (_writer is null || !_writer.IsAlive)
            {
                _writer = new Thread(WriterLoop)
                {
                    IsBackground = true,
                    Name = "PhotoReview.LogWriter"
                };
                _writer.Start();
            }
        }
    }

    private void Write(string level, string message, Exception? exception)
    {
        if (!_enabled || _stopping) return;
        try
        {
            _drained.Reset();
            _lastDrainFailed = false;
            _queue.Enqueue(new Entry(level, message, exception, DateTime.Now, Environment.CurrentManagedThreadId));
            if (Interlocked.Increment(ref _queuedCount) > MaxQueuedEntries && _queue.TryDequeue(out _))
            {
                Interlocked.Decrement(ref _queuedCount);
                Interlocked.Increment(ref _dropped);
            }
            _signal.Set();
        }
        catch (ObjectDisposedException)
        {
            // Dispose released the wait handles between the _enabled check above and here: logging must never throw
            // into the caller, and the entry is moot because the log is closed.
        }
    }

    private void Signal()
    {
        try { _signal.Set(); } catch (ObjectDisposedException) { /* already disposed: nothing to wake */ }
    }

    private void WriterLoop()
    {
        while (!_stopping)
        {
            _signal.WaitOne(250);
            Drain();
        }
        Drain();
        if (_disposed) ReleaseHandles(); // Dispose deferred the release because this thread outlived its join timeout
    }

    private void Drain()
    {
        DrainHook?.Invoke();
        if (_queue.IsEmpty)
        {
            _drained.Set();
            return;
        }

        _writing = true;
        try
        {
            var path = _filePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (NeedsRotation(path))
            {
                Rotate(path);
            }

            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 65536, FileOptions.SequentialScan);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            while (_queue.TryDequeue(out var e))
            {
                Interlocked.Decrement(ref _queuedCount);
                _lastDrainFailed = false;
                // Machine-read log (AGENTS.md rule 4): invariant culture, else a th-TH/ar-SA/fa-IR machine writes Buddhist/Hijri years.
                writer.WriteLine(FormattableString.Invariant($"{e.Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{e.Level}] [T{e.ThreadId}] {e.Message}") + (e.Exception is null ? "" : "\n" + e.Exception));
            }
        }
        catch
        {
            // Log file unavailable (locked, read-only, bad path): entries stay queued (bounded by MaxQueuedEntries) and are
            // retried on the next signal, but waiters must not block for the full flush timeout.
            _lastDrainFailed = true;
        }
        finally
        {
            _writing = false;
            if (_queue.IsEmpty || _lastDrainFailed)
            {
                _drained.Set();
            }
        }
    }

    private bool NeedsRotation(string path) => File.Exists(path) && new FileInfo(path).Length >= _maxLogBytes;

    private static void Rotate(string path)
    {
        var backup = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + ".1" + Path.GetExtension(path));
        File.Move(path, backup, overwrite: true);
    }
}

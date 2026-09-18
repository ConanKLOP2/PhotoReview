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
                _signal.Set();
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
    public void Flush()
    {
        var remaining = 2000;
        while ((!_queue.IsEmpty || _writing) && remaining > 0)
        {
            _signal.Set();
            var start = Environment.TickCount64;
            _drained.Wait(remaining);
            remaining -= (int)Math.Max(1, Environment.TickCount64 - start);
        }
    }

    public void Shutdown()
    {
        _stopping = true;
        _enabled = false;
        _signal.Set();
        _writer?.Join(2000);
        Flush();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Shutdown();
        _signal.Dispose();
        _drained.Dispose();
    }

    private void Start()
    {
        lock (_sync)
        {
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
        _drained.Reset();
        _queue.Enqueue(new Entry(level, message, exception, DateTime.Now, Environment.CurrentManagedThreadId));
        _signal.Set();
    }

    private void WriterLoop()
    {
        while (!_stopping)
        {
            _signal.WaitOne(250);
            Drain();
        }
        Drain();
    }

    private void Drain()
    {
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
                writer.WriteLine($"{e.Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{e.Level}] [T{e.ThreadId}] {e.Message}" + (e.Exception is null ? "" : $"\n{e.Exception}"));
            }
        }
        catch
        {
        }
        finally
        {
            _writing = false;
            if (_queue.IsEmpty)
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

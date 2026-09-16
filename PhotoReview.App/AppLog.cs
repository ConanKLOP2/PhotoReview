using System.Collections.Concurrent;
using System.Text;
using System.IO;

namespace PhotoReview.App;

public static class AppLog
{
    private sealed record Entry(string Level, string Message, Exception? Exception, DateTime Timestamp, int ThreadId);
    private static readonly object Sync = new();
    private static readonly ConcurrentQueue<Entry> Queue = new();
    private static readonly AutoResetEvent Signal = new(false);
    // Signaled by the writer thread each time it finishes a Drain() pass while the
    // queue is empty (and nothing is currently being written). Flush() waits on this
    // instead of busy-spinning with Thread.Yield(). Using a ManualResetEventSlim
    // (rather than pulsing once) means a Flush() call that arrives exactly when the
    // queue just became idle still observes the "drained" state immediately instead
    // of racing a one-shot signal.
    private static readonly ManualResetEventSlim Drained = new(true);
    private const long MaxLogBytes = 10 * 1024 * 1024;
    private static volatile bool _enabled;
    private static volatile bool _stopping;
    private static volatile bool _writing;
    private static Thread? _writer;
    public static bool Enabled { get => _enabled; set { if (value) Start(); else { _enabled = false; Signal.Set(); Flush(); } } }
    public static string FilePath => Path.Combine(Environment.GetEnvironmentVariable("PHOTOREVIEW_DATA_ROOT") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhotoReview"), "logs", "app.log");
    public static void Info(string message) => Write("INFO", message, null);
    public static void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);
    // Blocks (without spinning) until the writer thread reports the queue drained,
    // or until the timeout elapses, matching the previous ~2s bounded-wait behavior.
    public static void Flush()
    {
        var remaining = 2000;
        while ((!Queue.IsEmpty || _writing) && remaining > 0)
        {
            Signal.Set();
            var start = Environment.TickCount64;
            Drained.Wait(remaining);
            remaining -= (int)Math.Max(1, Environment.TickCount64 - start);
        }
    }
    public static void Shutdown() { _stopping = true; _enabled = false; Signal.Set(); _writer?.Join(2000); Flush(); }
    private static void Start() { lock (Sync) { _stopping = false; _enabled = true; if (_writer is null || !_writer.IsAlive) { _writer = new Thread(WriterLoop) { IsBackground = true, Name = "PhotoReview.LogWriter" }; _writer.Start(); } } }
    private static void Write(string level, string message, Exception? exception) { if (!_enabled || _stopping) return; Drained.Reset(); Queue.Enqueue(new Entry(level, message, exception, DateTime.Now, Environment.CurrentManagedThreadId)); Signal.Set(); }
    private static void WriterLoop() { while (!_stopping) { Signal.WaitOne(250); Drain(); } Drain(); }
    private static void Drain()
    {
        if (Queue.IsEmpty) { Drained.Set(); return; }
        _writing = true;
        try
        {
            var path = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (NeedsRotation(path)) Rotate(path);
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 65536, FileOptions.SequentialScan);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            while (Queue.TryDequeue(out var e)) writer.WriteLine($"{e.Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{e.Level}] [T{e.ThreadId}] {e.Message}" + (e.Exception is null ? "" : $"\n{e.Exception}"));
        }
        catch { }
        finally { _writing = false; if (Queue.IsEmpty) Drained.Set(); }
    }
    private static bool NeedsRotation(string path) => File.Exists(path) && new FileInfo(path).Length >= MaxLogBytes;
    private static void Rotate(string path)
    {
        var backup = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + ".1" + Path.GetExtension(path));
        File.Move(path, backup, overwrite: true);
    }
}

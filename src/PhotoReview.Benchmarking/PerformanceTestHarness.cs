using System.IO;
using System.Diagnostics;
using System.Text.Json;
using System.Windows.Media.Imaging;

namespace PhotoReview.Benchmarking;

/// <summary>Deterministic, relative performance probes. These are intentionally warnings rather than
/// machine-specific hard failures; correctness tests remain hard assertions.</summary>
public sealed record PerformanceSample(string Name, int Items, long TotalMs, long P50Ms, long P95Ms,
    long MaxMs, long WorkingSetBeforeBytes, long WorkingSetAfterBytes, long SourceReads, long CacheHits,
    long CacheMisses, long QueueWaitP95Ms, string Status, string? Note = null);

public sealed record PerformanceReport(DateTimeOffset StartedUtc, string Folder, int FileCount,
    long TotalSourceBytes, IReadOnlyList<PerformanceSample> Samples);

public static class PerformanceTestHarness
{
    // Pinned on purpose (not ImageFileTypes.SupportedExtensions): the viewer gained .gif later, and reports must stay
    // comparable with earlier runs over the same folders. Extend this set only together with a new baseline.
    private static readonly HashSet<string> Supported = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff"
    };

    /// <summary>The extensions a run measures (test seam for the pinned set).</summary>
    internal static IReadOnlySet<string> MeasuredExtensions => Supported;

    private static readonly JsonSerializerOptions DefaultOptions = new() { WriteIndented = true };

    // A valid, tiny PNG keeps the fixture portable while exercising WPF's real decoder; also the in-memory warm-up image.
    private static readonly byte[] TinyPng = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    /// <summary>Test seam: called once per cold-read decode with (timed, source file path); the path is null for the
    /// untimed warm-up, which decodes an in-memory image and never touches a measured file.</summary>
    internal static Action<bool, string?>? DecodeObserver { get; set; }

    /// <summary>R07 test seam: invoked once per file right after its concurrency slot is acquired, immediately before
    /// that file's decode starts, so a test can assert bounded concurrency (via increment/decrement counters) without
    /// relying on wall-clock timing.</summary>
    internal static Action<string>? ParallelDecodeObserver { get; set; }

    public static string CreateFixture(string root, int count = 30)
    {
        var folder = Path.Combine(root, "performance-fixture");
        Directory.CreateDirectory(folder);
        var png = TinyPng;
        for (var i = 0; i < count; i++)
        {
            var path = Path.Combine(folder, $"fixture-{i:000}.png");
            if (!File.Exists(path)) File.WriteAllBytes(path, png);
        }
        return folder;
    }

    /// <summary>R2-F-31: default report location; the user's photo folder must not receive files from a measurement run.</summary>
    public static string DefaultReportPath =>
        Path.Combine(DefaultReportDirectoryOverride ?? Path.Combine(Path.GetTempPath(), "PhotoReview-Benchmark"), "photoreview-performance-report.json");

    /// <summary>TEST-01: lets tests redirect the default report into a directory they own instead of the shared %TEMP% location.</summary>
    public static string? DefaultReportDirectoryOverride { get; set; }

    public static async Task<PerformanceReport> RunAsync(string folder, int take = 30, int workers = 8,
        string? reportPath = null, CancellationToken cancellationToken = default)
    {
        var files = Directory.EnumerateFiles(folder).Where(p => Supported.Contains(Path.GetExtension(p)))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).Take(take).ToArray();
        if (files.Length == 0) throw new InvalidOperationException("Performance folder has no supported images");
        var started = DateTimeOffset.UtcNow;
        var totalBytes = files.Sum(SafeLength);
        var samples = new List<PerformanceSample>
        {
            await MeasureColdAsync(files, cancellationToken),
            await MeasureParallelAsync(files, Math.Clamp(workers, 1, 16), cancellationToken)
        };
        reportPath ??= DefaultReportPath;
        var report = new PerformanceReport(started, folder, files.Length, totalBytes, samples);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, DefaultOptions), cancellationToken);
        Console.WriteLine($"PERF report={reportPath} files={files.Length} bytes={totalBytes} " +
            string.Join("; ", samples.Select(s => $"{s.Name}:p50={s.P50Ms}ms,p95={s.P95Ms}ms,status={s.Status}")));
        return report;
    }

    private static async Task<PerformanceSample> MeasureColdAsync(string[] files, CancellationToken ct)
    {
        var times = new List<long>(files.Length); long reads = 0;
        // One untimed decode of an in-memory image so first-call WPF/codec JIT and native init do not inflate the first
        // timed sample (TOOL-02). It must not read a measured file, or the first "cold" read would hit the OS file cache (R2-A-09).
        await Task.Run(() => { DecodeObserver?.Invoke(false, null); using var stream = new MemoryStream(TinyPng, writable: false); GC.KeepAlive(Decode(stream, 2200)); }, ct);
        var before = CurrentWorkingSet(); // after warm-up so JIT/native init is not part of the delta
        foreach (var path in files)
        {
            ct.ThrowIfCancellationRequested();
            var sw = Stopwatch.StartNew();
            await Task.Run(() => { DecodeObserver?.Invoke(true, path); GC.KeepAlive(DecodeFile(path)); }, ct);
            sw.Stop(); times.Add(sw.ElapsedMilliseconds); reads++;
        }
        return Sample("cold-read", times, before, CurrentWorkingSet(), reads, 0, 0, 0);
    }

    private static async Task<PerformanceSample> MeasureParallelAsync(string[] files, int workers, CancellationToken ct)
    {
        var before = CurrentWorkingSet();
        var times = new long[files.Length]; var waits = new long[files.Length]; long reads = 0;
        var gate = new SemaphoreSlim(workers, workers); var queue = Stopwatch.StartNew();
        // R07: the semaphore slot is acquired here, in the loop, BEFORE each file's Task is created --
        // not inside it. Previously every selected file got its own Task.Run (and its own queued
        // thread-pool work item) up front, all waiting on the same gate; for a `take` far larger than
        // `workers` that meant thousands of pre-created Task objects queued at once even though only
        // `workers` of them could ever be doing anything. Now at most `workers` file-Tasks are ever
        // running concurrently, and the loop itself blocks (awaiting the gate) instead of pre-creating
        // the next file's Task -- `running` only ever holds already-started-or-finished Tasks, not
        // pre-queued ones. `queued`/`waits[i]` keep the same meaning as before (elapsed time from
        // "this file is next in line" to "its slot was acquired").
        var running = new List<Task>(Math.Min(files.Length, 4096));
        try
        {
        for (var i = 0; i < files.Length; i++)
        {
            var index = i; var path = files[i];
            var queued = queue.ElapsedMilliseconds;
            await gate.WaitAsync(ct).ConfigureAwait(false);
            waits[index] = queue.ElapsedMilliseconds - queued;
            running.Add(Task.Run(async () =>
            {
                try
                {
                    ParallelDecodeObserver?.Invoke(path);
                    var sw = Stopwatch.StartNew();
                    await Task.Run(() => { GC.KeepAlive(DecodeFile(path)); }, ct);
                    times[index] = sw.ElapsedMilliseconds; Interlocked.Increment(ref reads);
                }
                finally { gate.Release(); }
            }, ct));
        }
        await Task.WhenAll(running);
        }
        catch
        {
            // Cancelled or failed mid-run: observe every already-started decode so none is left running (or
            // unobserved) after this method has thrown; the original exception is the one that propagates.
            try { await Task.WhenAll(running).ConfigureAwait(false); }
            catch (Exception) { /* already being reported by the original exception */ }
            throw;
        }
        return Sample($"parallel-read-{workers}", times, before, CurrentWorkingSet(), reads, 0, 0, Percentile(waits, .95));
    }

    // Process is IDisposable (native handle); GetCurrentProcess() returns a new instance every call.
    private static long CurrentWorkingSet()
    {
        using var process = Process.GetCurrentProcess();
        return process.WorkingSet64;
    }

    private static PerformanceSample Sample(string name, IReadOnlyList<long> values, long before, long after, long reads, long hits, long misses, long waitP95)
    {
        var p50 = Percentile(values, .50); var p95 = Percentile(values, .95); var status = p95 <= Math.Max(1, p50) * 8 ? "PASS" : "WARN";
        return new(name, values.Count, values.Sum(), p50, p95, values.Max(), before, after, reads, hits, misses, waitP95, status);
    }

    private static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; } // vanished/locked: only the report total is affected
    }

    /// <summary>Decodes <paramref name="path"/>; a failure names the file instead of surfacing a bare codec error.</summary>
    private static BitmapImage DecodeFile(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.SequentialScan);
            return Decode(stream, 2200);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException or ArgumentException)
        {
            throw new InvalidDataException($"Could not decode '{path}': {ex.Message}", ex);
        }
    }

    private static BitmapImage Decode(Stream stream, int width)
    {
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.DecodePixelWidth = width; image.StreamSource = stream; image.EndInit(); image.Freeze(); return image;
    }

    public static long Percentile(IEnumerable<long> values, double fraction)
    {
        var sorted = values.Order().ToArray(); if (sorted.Length == 0) return 0;
        return sorted[Math.Clamp((int)Math.Ceiling(fraction * sorted.Length) - 1, 0, sorted.Length - 1)];
    }
}

using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace PhotoReview.Core.Diagnostics;

/// <summary>
/// Perf diagnostics EventSource for PhotoReview (D03/D04). Declares one event per instrumentation
/// point listed in docs/refactoring/PERF-DIAGNOSIS-PLAN.md mục 6. Event ids are part of the wire
/// contract for external tools (dotnet-trace, PerfView, WPR) and for <see cref="PerfCsvListener"/>:
/// once assigned, an id must never be reused or reassigned to a different event.
///
/// Callers (D04) must guard every call site with <c>if (PhotoReviewPerf.Log.IsEnabled())</c> so that
/// argument construction (string formatting, PathId hashing, Stopwatch reads) is skipped entirely
/// when no listener is attached — this class only declares the events, it never fires them itself.
/// </summary>
[EventSource(Name = "PhotoReview-Perf")]
public sealed class PhotoReviewPerf : EventSource
{
    public static readonly PhotoReviewPerf Log = new();

    private PhotoReviewPerf()
    {
    }

    // Nav token flowing across the async chain of a single navigation (ShowImageAsync -> decode ->
    // present -> post-work). Preload work that is not tied to a live navigation uses nav = -1.
    // Set by the caller (D04) via an AsyncLocal so it survives await boundaries within one logical
    // operation without having to thread an extra parameter through every method.
    private static readonly AsyncLocal<long> NavAsyncLocal = new();

    public static long NavContext
    {
        get => NavAsyncLocal.Value;
        set => NavAsyncLocal.Value = value;
    }

    /// <summary>
    /// Stable 8 hex-char identifier for a file path: first 4 bytes (8 hex chars) of the SHA-256 of
    /// the case-normalized, fully-qualified path. Never emit the raw path into perf events/CSV/reports.
    /// </summary>
    public static string PathId(string path)
    {
        var full = Path.GetFullPath(path).ToUpperInvariant();
        var bytes = Encoding.UTF8.GetBytes(full);
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(bytes, hash);
        return Convert.ToHexString(hash[..4]).ToLowerInvariant();
    }

    /// <summary>Converts a <see cref="Stopwatch.GetTimestamp"/> snapshot into elapsed milliseconds.</summary>
    public static double Ms(long startTimestamp)
    {
        var delta = Stopwatch.GetTimestamp() - startTimestamp;
        return delta * 1000.0 / Stopwatch.Frequency;
    }

    // ---- Event ids: fixed forever once assigned. Add new events with new ids; never renumber. ----

    [Event(1, Level = EventLevel.Informational)]
    public void KeyInput(long nav, string key, double inputDelayMs)
    {
        if (IsEnabled()) WriteEvent(1, nav, key, inputDelayMs);
    }

    [Event(2, Level = EventLevel.Informational)]
    public void ShowStart(long nav, int index, string mode)
    {
        if (IsEnabled()) WriteEvent(2, nav, index, mode);
    }

    [Event(3, Level = EventLevel.Informational)]
    public void Stat(long nav, double ms)
    {
        if (IsEnabled()) WriteEvent(3, nav, ms);
    }

    [Event(4, Level = EventLevel.Informational)]
    public void Lookup(long nav, string pathId, string result)
    {
        if (IsEnabled()) WriteEvent(4, nav, pathId, result);
    }

    [Event(5, Level = EventLevel.Informational)]
    public void ThumbStart(long nav, string pathId)
    {
        if (IsEnabled()) WriteEvent(5, nav, pathId);
    }

    [Event(6, Level = EventLevel.Informational)]
    public void ThumbEnd(long nav, string pathId, string source, double ms)
    {
        if (IsEnabled()) WriteEvent(6, nav, pathId, source, ms);
    }

    [Event(7, Level = EventLevel.Informational)]
    public void JoinStart(long nav, string pathId)
    {
        if (IsEnabled()) WriteEvent(7, nav, pathId);
    }

    [Event(8, Level = EventLevel.Informational)]
    public void JoinEnd(long nav, string pathId, double ms)
    {
        if (IsEnabled()) WriteEvent(8, nav, pathId, ms);
    }

    [Event(9, Level = EventLevel.Informational)]
    public void DiskCacheRead(long nav, string pathId, double ms, long bytes)
    {
        if (IsEnabled()) WriteEvent(9, nav, pathId, ms, bytes);
    }

    [Event(10, Level = EventLevel.Informational)]
    public void SourceOpen(long nav, string pathId, double ms)
    {
        if (IsEnabled()) WriteEvent(10, nav, pathId, ms);
    }

    [Event(11, Level = EventLevel.Informational)]
    public void SourceRead(long nav, string pathId, double ms, long bytes)
    {
        if (IsEnabled()) WriteEvent(11, nav, pathId, ms, bytes);
    }

    [Event(12, Level = EventLevel.Informational)]
    public void Decode(long nav, string pathId, double ms, int targetWidth, bool downscaled, bool fallback)
    {
        if (IsEnabled()) WriteEvent(12, nav, pathId, ms, targetWidth, downscaled, fallback);
    }

    [Event(13, Level = EventLevel.Informational)]
    public void Verify(long nav, string pathId, double ms)
    {
        if (IsEnabled()) WriteEvent(13, nav, pathId, ms);
    }

    [Event(14, Level = EventLevel.Informational)]
    public void Assign(long nav, double ms, int pixelWidth, int pixelHeight)
    {
        if (IsEnabled()) WriteEvent(14, nav, ms, pixelWidth, pixelHeight);
    }

    [Event(15, Level = EventLevel.Informational)]
    public void Rendered(long nav, double msSinceAssign)
    {
        if (IsEnabled()) WriteEvent(15, nav, msSinceAssign);
    }

    [Event(16, Level = EventLevel.Informational)]
    public void Presented(long nav, string kind)
    {
        if (IsEnabled()) WriteEvent(16, nav, kind);
    }

    [Event(17, Level = EventLevel.Informational)]
    public void PostStart(long nav, string part)
    {
        if (IsEnabled()) WriteEvent(17, nav, part);
    }

    [Event(18, Level = EventLevel.Informational)]
    public void PostEnd(long nav, string part, double ms)
    {
        if (IsEnabled()) WriteEvent(18, nav, part, ms);
    }

    [Event(19, Level = EventLevel.Informational)]
    public void PreloadItem(int slot, string pathId, double queueWaitMs, string kind, double ms)
    {
        if (IsEnabled()) WriteEvent(19, slot, pathId, queueWaitMs, kind, ms);
    }

    [Event(20, Level = EventLevel.Informational)]
    public void PreloadPaused(int loadPercent, long availableMb)
    {
        if (IsEnabled()) WriteEvent(20, loadPercent, availableMb);
    }

    [Event(21, Level = EventLevel.Informational)]
    public void PreloadCancel(string reason)
    {
        if (IsEnabled()) WriteEvent(21, reason);
    }

    [Event(22, Level = EventLevel.Informational)]
    public void DispatcherLongOp(double ms, string priority, string name)
    {
        if (IsEnabled()) WriteEvent(22, ms, priority, name);
    }

    [Event(23, Level = EventLevel.Informational)]
    public void Folder(long gen, string phase, double ms)
    {
        if (IsEnabled()) WriteEvent(23, gen, phase, ms);
    }

    [Event(24, Level = EventLevel.Informational)]
    public void DiagMode(string flags)
    {
        if (IsEnabled()) WriteEvent(24, flags);
    }

    /// <summary>
    /// perf(render-metric): time from the Source assign to the SECOND CompositionTarget.Rendering
    /// tick after it -- i.e. the frame containing the new image has actually been rendered, not
    /// just the dispatcher/vsync phase <see cref="Rendered"/> alone measures (that event fires at
    /// the start of the frame, before layout/render run). Emitted alongside (never instead of)
    /// <see cref="Rendered"/> for continuity; see WpfPresentationSink.TracePresented.
    /// </summary>
    [Event(25, Level = EventLevel.Informational)]
    public void RenderedFrame(long nav, double msSinceAssign)
    {
        if (IsEnabled()) WriteEvent(25, nav, msSinceAssign);
    }

    /// <summary>
    /// perf(startup): one process-startup milestone (App_Startup entry, settings loaded, services
    /// built, MainWindow constructed, window shown/loaded, open-path begin, ...). The timestamp is
    /// milliseconds since the OS process start time (<see cref="MsSinceProcessStart"/>), so the
    /// first row also covers runtime/assembly load before any managed app code ran. The CSV row's
    /// qpcTicks of any Startup row maps every other event onto the same process-start timeline.
    /// </summary>
    [Event(26, Level = EventLevel.Informational)]
    public void Startup(string phase, double msSinceProcessStart)
    {
        if (IsEnabled()) WriteEvent(26, phase, msSinceProcessStart);
    }

    /// <summary>
    /// perf(startup): extra detail for a <see cref="Folder"/> phase that the 3-field Folder event
    /// cannot carry (it is a fixed wire contract): e.g. <c>scanned</c> with the file count, or
    /// <c>explorer</c> with the snapshot status and its ordered-path count.
    /// </summary>
    [Event(27, Level = EventLevel.Informational)]
    public void FolderInfo(long gen, string phase, long value, string detail)
    {
        if (IsEnabled()) WriteEvent(27, gen, phase, value, detail);
    }

    private static DateTime? _processStartUtc;

    /// <summary>Milliseconds elapsed since the OS started this process (wall clock, ~1 ms resolution).</summary>
    public static double MsSinceProcessStart()
    {
        var start = _processStartUtc ??= ReadProcessStartUtc();
        return (DateTime.UtcNow - start).TotalMilliseconds;
    }

    private static DateTime ReadProcessStartUtc()
    {
        using var process = Process.GetCurrentProcess();
        return process.StartTime.ToUniversalTime();
    }

    /// <summary>Emits <see cref="Startup"/> for <paramref name="phase"/> when a listener is attached.</summary>
    public static void StartupMark(string phase)
    {
        if (Log.IsEnabled()) Log.Startup(phase, MsSinceProcessStart());
    }
}

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace PhotoReview.Benchmark.Cli;

/// <summary>
/// perf(harness) <c>--perf-session --resource-sample</c>: samples this process (and the machine) every interval into
/// <c>resources.csv</c> of the run directory, for the device-tuning bench (tools/diag/tune-rank.ps1 reads it to fill
/// the min-available-RAM / peak-working-set constraints). Runs on its own background thread; one row at start, one per
/// interval, one final row on <see cref="Dispose"/>. Columns: utc, elapsedMs, availPhysMb (machine, GlobalMemoryStatusEx),
/// wsMb, peakWsMb, privateMb, gcPct (GC pause time share of the interval), pageFaultsPerSec, cpuPct (process CPU time /
/// (wall x logical cores)). Invariant culture; the file is flushed after every row so a killed run keeps its samples.
/// </summary>
internal sealed class ResourceSampler : IDisposable
{
    internal const string Header = "utc,elapsedMs,availPhysMb,wsMb,peakWsMb,privateMb,gcPct,pageFaultsPerSec,cpuPct";

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    private readonly StreamWriter _writer;
    private readonly Process _process = Process.GetCurrentProcess();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly ManualResetEventSlim _stop = new(false);
    private readonly Thread _thread;
    private readonly TimeSpan _interval;
    private double _lastWallMs;
    private double _lastCpuMs;
    private double _lastGcPauseMs;
    private long _lastFaults;
    private bool _disposed;

    public ResourceSampler(string path, TimeSpan interval)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        _interval = interval;
        _writer = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };
        _writer.WriteLine(Header);
        _process.Refresh();
        _lastCpuMs = _process.TotalProcessorTime.TotalMilliseconds;
        _lastGcPauseMs = GC.GetTotalPauseDuration().TotalMilliseconds;
        _lastFaults = ProcessPageFaults.Read();
        WriteRow(first: true);
        _thread = new Thread(Loop) { IsBackground = true, Name = "ResourceSampler" };
        _thread.Start();
    }

    private void Loop()
    {
        while (!_stop.Wait(_interval)) WriteRow(first: false);
    }

    private void WriteRow(bool first)
    {
        var wallMs = _clock.Elapsed.TotalMilliseconds;
        _process.Refresh();
        var cpuMs = _process.TotalProcessorTime.TotalMilliseconds;
        var gcMs = GC.GetTotalPauseDuration().TotalMilliseconds;
        var faults = ProcessPageFaults.Read();
        var dt = wallMs - _lastWallMs;
        var gcPct = first ? 0 : Percent(gcMs - _lastGcPauseMs, dt, 1);
        var cpuPct = first ? 0 : Percent(cpuMs - _lastCpuMs, dt, Environment.ProcessorCount);
        var faultRate = first || faults < 0 || _lastFaults < 0 ? 0 : Rate(faults - _lastFaults, dt);
        _lastWallMs = wallMs; _lastCpuMs = cpuMs; _lastGcPauseMs = gcMs; _lastFaults = faults;
        var line = string.Join(',',
            DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            wallMs.ToString("F1", CultureInfo.InvariantCulture),
            AvailablePhysicalMb().ToString("F1", CultureInfo.InvariantCulture),
            Mb(_process.WorkingSet64), Mb(_process.PeakWorkingSet64), Mb(_process.PrivateMemorySize64),
            gcPct.ToString("F2", CultureInfo.InvariantCulture),
            faultRate.ToString("F1", CultureInfo.InvariantCulture),
            cpuPct.ToString("F1", CultureInfo.InvariantCulture));
        lock (_writer) { if (!_disposed) _writer.WriteLine(line); }
    }

    private static string Mb(long bytes) => (bytes / (1024.0 * 1024.0)).ToString("F1", CultureInfo.InvariantCulture);

    /// <summary>Busy time as a percent of (wall x divisor); 0 when the interval is empty. Clamped to [0, 100].</summary>
    internal static double Percent(double busyMs, double wallMs, int divisor) =>
        wallMs <= 0 || divisor <= 0 ? 0 : Math.Clamp(100.0 * busyMs / (wallMs * divisor), 0, 100);

    /// <summary>Events per second over the interval; 0 when the interval is empty.</summary>
    internal static double Rate(long delta, double wallMs) => wallMs <= 0 ? 0 : Math.Max(0, delta) * 1000.0 / wallMs;

    internal static double AvailablePhysicalMb()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref status) ? status.AvailPhys / (1024.0 * 1024.0) : double.NaN;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _stop.Set();
        _thread.Join(TimeSpan.FromSeconds(5));
        WriteRow(first: false);   // final sample covers the tail of the run
        lock (_writer) { _disposed = true; _writer.Dispose(); }
        _process.Dispose();
        _stop.Dispose();
    }
}

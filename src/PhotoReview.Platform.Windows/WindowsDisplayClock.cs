using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PhotoReview.Platform.Windows;

/// <summary>
/// Vblank timing of the monitor under a window, for any refresh rate (60, 75, 144, 240 Hz ...). A background thread
/// waits for that monitor's vertical blanks (<c>D3DKMTWaitForVerticalBlankEvent</c>, the kernel-thunk vsync wait
/// Chromium also uses) and feeds a <see cref="VBlankEstimator"/>. The thread starts on the first
/// <see cref="GetTiming"/> call, follows the window to another monitor (re-opens the adapter and resets the estimate)
/// and exits after <see cref="IdleStopMs"/> without calls, so it only runs around a glide.
/// If the vblank wait is unavailable, falls back to the desktop compositor's timing (<c>DwmGetCompositionTimingInfo</c>),
/// which describes the primary display only.
/// </summary>
public sealed class WindowsDisplayClock : IDisplayClock
{
    public static readonly WindowsDisplayClock Instance = new();

    /// <summary>The vblank thread exits after this long without a <see cref="GetTiming"/> call (ms).</summary>
    public const int IdleStopMs = 1500;

    private readonly object _gate = new();
    private IntPtr _monitor;          // monitor the thread should follow (written under _gate)
    private long _lastUse;            // QPC of the latest GetTiming call
    private Thread? _thread;
    private Published? _published;    // latest estimate, swapped atomically by the vblank thread
    private IntPtr _failedMonitor;    // a monitor whose vblank wait could not be opened: use DWM for it

    private sealed record Published(IntPtr Monitor, DisplayTiming Timing);

    public DisplayTiming? GetTiming(IntPtr window)
    {
        var monitor = window != IntPtr.Zero ? MonitorFromWindow(window, MonitorDefaultToNearest) : IntPtr.Zero;
        if (monitor == IntPtr.Zero) return DwmTiming();
        Volatile.Write(ref _lastUse, Stopwatch.GetTimestamp());
        lock (_gate)
        {
            if (monitor == _failedMonitor) return DwmTiming();
            _monitor = monitor;
            if (_thread is null)
            {
                _thread = new Thread(Run) { IsBackground = true, Name = "PhotoReview vblank clock", Priority = ThreadPriority.Highest };
                _thread.Start();
            }
        }
        var published = Volatile.Read(ref _published);
        return published is not null && published.Monitor == monitor ? published.Timing : null;
    }

    private void Run()
    {
        var estimator = new VBlankEstimator();
        var opened = IntPtr.Zero;
        var adapter = 0u;
        var source = 0u;
        try
        {
            while (true)
            {
                IntPtr wanted;
                lock (_gate)
                {
                    var idle = (Stopwatch.GetTimestamp() - Volatile.Read(ref _lastUse)) * 1000.0 / Stopwatch.Frequency;
                    if (idle > IdleStopMs)
                    {
                        _thread = null;
                        Volatile.Write(ref _published, null); // a stale phase drifts; re-measure next time
                        return;
                    }
                    wanted = _monitor;
                }
                if (wanted != opened)
                {
                    Close(ref adapter);
                    estimator.Reset();
                    Volatile.Write(ref _published, null);
                    opened = wanted;
                    if (!TryOpen(wanted, out adapter, out source))
                    {
                        lock (_gate)
                        {
                            _failedMonitor = wanted;
                            _thread = null;
                        }
                        return;
                    }
                }
                var wait = new WaitForVerticalBlankEvent { Adapter = adapter, Device = 0, VidPnSourceId = source };
                if (D3DKMTWaitForVerticalBlankEvent(ref wait) != 0)
                {
                    lock (_gate)
                    {
                        _failedMonitor = opened;
                        _thread = null;
                    }
                    return;
                }
                estimator.Add(Stopwatch.GetTimestamp());
                if (estimator.Current is { } timing) Volatile.Write(ref _published, new Published(opened, timing));
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            lock (_gate)
            {
                _failedMonitor = opened;
                _thread = null;
            }
        }
        finally
        {
            Close(ref adapter);
        }
    }

    private static bool TryOpen(IntPtr monitor, out uint adapter, out uint source)
    {
        adapter = 0;
        source = 0;
        var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
        if (!GetMonitorInfo(monitor, ref info)) return false;
        var dc = CreateDC(null, info.Device, null, IntPtr.Zero);
        if (dc == IntPtr.Zero) return false;
        try
        {
            var open = new OpenAdapterFromHdc { Hdc = dc };
            if (D3DKMTOpenAdapterFromHdc(ref open) != 0) return false;
            adapter = open.Adapter;
            source = open.VidPnSourceId;
            return true;
        }
        finally
        {
            _ = DeleteDC(dc);
        }
    }

    private static void Close(ref uint adapter)
    {
        if (adapter == 0) return;
        var close = new CloseAdapter { Adapter = adapter };
        _ = D3DKMTCloseAdapter(ref close);
        adapter = 0;
    }

    /// <summary>The compositor's timing (primary display), or null.</summary>
    public static DisplayTiming? DwmTiming()
    {
        var info = new TimingInfo { Size = (uint)Marshal.SizeOf<TimingInfo>() };
        try
        {
            if (DwmGetCompositionTimingInfo(IntPtr.Zero, ref info) != 0) return null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
        if (info.QpcRefreshPeriod == 0 || info.QpcVBlank == 0) return null;
        return new DisplayTiming(unchecked((long)info.QpcVBlank), unchecked((long)info.QpcRefreshPeriod));
    }

    private const uint MonitorDefaultToNearest = 2;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string Device;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateDC(string? driver, string device, string? output, IntPtr initData);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr dc);

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OpenAdapterFromHdc
    {
        public IntPtr Hdc;
        public uint Adapter;
        public Luid AdapterLuid;
        public uint VidPnSourceId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaitForVerticalBlankEvent
    {
        public uint Adapter;
        public uint Device;
        public uint VidPnSourceId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CloseAdapter
    {
        public uint Adapter;
    }

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTOpenAdapterFromHdc(ref OpenAdapterFromHdc data);

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTWaitForVerticalBlankEvent(ref WaitForVerticalBlankEvent data);

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTCloseAdapter(ref CloseAdapter data);

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct UnsignedRatio
    {
        public uint Numerator;
        public uint Denominator;
    }

    // DWM_TIMING_INFO (dwmapi.h); only the leading fields are read, the rest keep the size right.
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct TimingInfo
    {
        public uint Size;
        public UnsignedRatio RateRefresh;
        public ulong QpcRefreshPeriod;
        public UnsignedRatio RateCompose;
        public ulong QpcVBlank;
        public ulong CRefresh;
        public uint CDXRefresh;
        public ulong QpcCompose;
        public ulong CFrame;
        public uint CDXPresent;
        public ulong CRefreshFrame;
        public ulong CFrameSubmitted;
        public uint CDXPresentSubmitted;
        public ulong CFrameConfirmed;
        public uint CDXPresentConfirmed;
        public ulong CRefreshConfirmed;
        public uint CDXRefreshConfirmed;
        public ulong CFramesLate;
        public uint CFramesOutstanding;
        public ulong CFrameDisplayed;
        public ulong QpcFrameDisplayed;
        public ulong CRefreshFrameDisplayed;
        public ulong CFrameComplete;
        public ulong QpcFrameComplete;
        public ulong CFramePending;
        public ulong QpcFramePending;
        public ulong CFramesDisplayed;
        public ulong CFramesComplete;
        public ulong CFramesPending;
        public ulong CFramesAvailable;
        public ulong CFramesDropped;
        public ulong CFramesMissed;
        public ulong CRefreshNextDisplayed;
        public ulong CRefreshNextPresented;
        public ulong CRefreshesDisplayed;
        public ulong CRefreshesPresented;
        public ulong CRefreshStarted;
        public ulong CPixelsReceived;
        public ulong CPixelsDrawn;
        public ulong CBuffersEmpty;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetCompositionTimingInfo(IntPtr hwnd, ref TimingInfo info);
}

using System.Diagnostics;
using PhotoReview.Shell.Interop.Graphics;

namespace PhotoReview.Shell.Rendering;

/// <summary>Một lần "chạm GPU" tí hon của luồng giữ ấm (<see cref="GpuKeepAlive"/>); tạo/dùng/huỷ trên cùng MỘT luồng.</summary>
internal interface IGpuKeepAliveWork : IDisposable
{
    /// <summary>Nộp một lệnh rỗng cho GPU. Ném khi thiết bị hỏng (luồng giữ ấm tự tắt).</summary>
    void Touch();
}

/// <summary>
/// WP21-STALL: giữ GPU "thức" bằng một luồng nền nộp lệnh vẽ tí hon cách <c>intervalMs</c> trong khoảng <see cref="Activate"/> còn
/// hiệu lực.
/// <para>
/// Nguyên nhân gốc đã đo (docs/refactoring/perf/2026-10-12-wp21-stall.md): trên GPU Intel UHD 630 (máy dev) lần nộp việc đầu tiên
/// sau một khoảng GPU rỗi (chỉ cần ~25-33 ms) bị chặn ~0,47 s ở 25-50 % số lần, ở EndDraw (hoặc Present nếu vẽ trên adapter khác);
/// swap chain, số buffer, latency, flush, D2D/D3D threading đều không liên quan, WARP không bị. Nộp việc cách &lt;= 25 ms thì
/// không khung nào bị chặn (0/150+), và luồng riêng với thiết bị D3D/D2D riêng đủ - luồng UI không phải chờ.
/// </para>
/// Thiết bị mở lười ở lần <see cref="Activate"/> đầu, TRÊN luồng nền (không tốn gì tới khi có tương tác). Không có GPU thật (WARP)
/// hoặc thiết bị hỏng = tự tắt (<see cref="IsDisabled"/>), không bao giờ ném ra ngoài: giữ ấm là tối ưu, không phải chức năng.
/// Ngoài khoảng hiệu lực luồng ngủ trên một event (không đánh thức, không CPU/GPU).
/// </summary>
internal sealed class GpuKeepAlive : IDisposable
{
    private readonly int _intervalMs;
    private readonly Func<IGpuKeepAliveWork?> _openWork;
    private readonly AutoResetEvent _wake = new(false);
    private readonly object _gate = new();
    private Thread? _thread;
    private long _untilTimestamp;
    private long _ticks;
    private volatile bool _stop;
    private volatile bool _failed;

    /// <param name="intervalMs">Khoảng giữa hai lần nộp (1-1000 ms; mặc định production 20).</param>
    /// <param name="openWork">Mở việc giữ ấm, gọi trên luồng nền; null = không có GPU thật (tự tắt).</param>
    public GpuKeepAlive(int intervalMs, Func<IGpuKeepAliveWork?> openWork)
    {
        _intervalMs = Math.Clamp(intervalMs, 1, 1000);
        _openWork = openWork ?? throw new ArgumentNullException(nameof(openWork));
    }

    /// <summary>Thiết bị offscreen riêng trên GPU thật; null khi chỉ có WARP.</summary>
    public static IGpuKeepAliveWork? OpenHardwareWork()
    {
        var resources = DeviceResources.CreateOffscreen(16, 16, 1.0, preferWarp: false);
        if (resources.IsWarp)
        {
            resources.Dispose();
            return null;
        }

        return new HardwareWork(resources);
    }

    /// <summary>Số lệnh đã nộp (chẩn đoán/test).</summary>
    public long Ticks => Interlocked.Read(ref _ticks);

    /// <summary>Đã tự tắt (không có GPU thật / thiết bị hỏng) hoặc đã Dispose.</summary>
    public bool IsDisabled => _failed || _stop;

    /// <summary>Luồng nền đã được tạo (false tới lần <see cref="Activate"/> đầu).</summary>
    public bool HasThread
    {
        get
        {
            lock (_gate)
            {
                return _thread is not null;
            }
        }
    }

    /// <summary>Kéo dài khoảng giữ ấm tới <paramref name="untilTimestamp"/> (Stopwatch ticks, không bao giờ rút ngắn).</summary>
    public void Activate(long untilTimestamp)
    {
        if (_stop || _failed)
        {
            return;
        }

        lock (_gate)
        {
            if (_stop)
            {
                return;
            }

            if (untilTimestamp > Volatile.Read(ref _untilTimestamp))
            {
                Volatile.Write(ref _untilTimestamp, untilTimestamp);
            }

            if (_thread is null)
            {
                _thread = new Thread(Run) { IsBackground = true, Name = "GpuKeepAlive", Priority = ThreadPriority.BelowNormal };
                _thread.Start();
            }
        }

        _wake.Set();
    }

    public void Dispose()
    {
        Thread? thread;
        lock (_gate)
        {
            if (_stop)
            {
                return;
            }

            _stop = true;
            thread = _thread;
        }

        _wake.Set();
        thread?.Join(3000); // thiết bị có thể đang chờ một lần nộp bị chặn (<= ~0,5 s)
    }

    private void Run()
    {
        IGpuKeepAliveWork? work = null;
        nint timer = 0;
        try
        {
            work = _openWork();
            if (work is null)
            {
                _failed = true;
                return;
            }

            timer = GraphicsKernel32.CreateWaitableTimerEx(0, 0, GraphicsKernel32.CreateWaitableTimerHighResolution, GraphicsKernel32.TimerAllAccess);
            if (timer == 0)
            {
                timer = GraphicsKernel32.CreateWaitableTimerEx(0, 0, 0, GraphicsKernel32.TimerAllAccess);
            }

            while (!_stop)
            {
                _wake.WaitOne();
                while (!_stop && Stopwatch.GetTimestamp() < Volatile.Read(ref _untilTimestamp))
                {
                    work.Touch();
                    Interlocked.Increment(ref _ticks);
                    SleepInterval(timer);
                }
            }
        }
        catch (Exception)
        {
            _failed = true; // thiết bị mất/không tạo được: tắt hẳn, UI không bị ảnh hưởng
        }
        finally
        {
            if (timer != 0)
            {
                GraphicsKernel32.CloseHandle(timer);
            }

            try
            {
                work?.Dispose();
            }
            catch (Exception)
            {
                // đang tắt: bỏ qua
            }
        }
    }

    /// <summary>Ngủ <c>intervalMs</c> bằng waitable timer độ phân giải cao (Thread.Sleep làm tròn lên 15,6 ms: khoảng hở > 25 ms thì GPU kịp rỗi).</summary>
    private unsafe void SleepInterval(nint timer)
    {
        if (timer != 0)
        {
            long due = -(long)_intervalMs * 10_000; // 100 ns, âm = tương đối
            if (GraphicsKernel32.SetWaitableTimer(timer, &due, 0, 0, 0, resume: false))
            {
                GraphicsKernel32.WaitForSingleObjectEx(timer, (uint)_intervalMs + 100, alertable: false);
                return;
            }
        }

        Thread.Sleep(_intervalMs);
    }

    private sealed class HardwareWork(DeviceResources resources) : IGpuKeepAliveWork
    {
        public void Touch() => resources.TouchGpu();

        public void Dispose() => resources.Dispose();
    }
}

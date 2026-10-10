using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using PhotoReview.Shell.Interop;
using PhotoReview.Shell.Win32.Hosting.PendingInterop;

namespace PhotoReview.Shell.Win32.Hosting;

/// <summary>
/// Điểm móc lọc message trước TranslateMessage/DispatchMessage (WP-19b: <c>ComponentDispatcher.RaiseThreadMessage</c> cho cửa sổ
/// WPF modeless trong cùng thread). True = đã xử lý, bỏ qua Translate/Dispatch.
/// </summary>
internal interface IMessageFilter
{
    bool PreFilterMessage(in WindowMessage message);
}

/// <summary>
/// Vòng lặp của UI thread shell (NO-WPF-EXEC-PLAN thẻ WP-14). Mỗi lượt, theo thứ tự:
/// <list type="number">
/// <item>xả việc Send/Normal/Render của <see cref="Win32UiDispatcher"/> (trước input kế tiếp và trước khi vẽ);</item>
/// <item>phát khung cho đồng hồ armed đã tới nhịp (handle đã báo hoặc quá hạn chót);</item>
/// <item>bơm message (PeekMessage); dừng bơm ngay khi có việc mức cao mới (Normal chạy trước input kế tiếp) hoặc sau
/// <see cref="MaxMessagesPerPass"/> message (để đồng hồ khung không bị một trận message bỏ đói);</item>
/// <item>hàng đợi message rỗng: chạy MỘT việc Background rồi lặp;</item>
/// <item>không còn gì: chặn trong <c>MsgWaitForMultipleObjectsEx</c> (message, waitable của đồng hồ armed, hạn chót gần nhất).
/// Cửa sổ nhàn rỗi không có đồng hồ armed thì chờ vô hạn: 0 % CPU.</item>
/// </list>
/// Ngoại lệ thoát từ WndProc (qua <see cref="WndProcThunk"/>) hoặc handler khung lan ra <see cref="Run"/>.
/// </summary>
internal sealed unsafe class MessageLoop
{
    /// <summary>Số message tối đa bơm trong một lượt trước khi kiểm tra lại dispatcher và đồng hồ khung.</summary>
    internal const int MaxMessagesPerPass = 32;

    // MAXIMUM_WAIT_OBJECTS = 64, trừ một chỗ cho hàng đợi message.
    private const int MaxWaitHandles = 63;

    private readonly Win32UiDispatcher _dispatcher;
    private readonly FrameClockBase?[] _waitOwners = new FrameClockBase?[MaxWaitHandles];
    private FrameClockBase[] _clocks = [];
    private IMessageFilter[] _filters = [];
    private bool _running;

    public MessageLoop(Win32UiDispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _dispatcher.VerifyAccess();
    }

    /// <summary>Số lần vòng lặp chặn trong MsgWaitForMultipleObjectsEx (chẩn đoán/test nhàn rỗi).</summary>
    internal long WaitCount { get; private set; }

    /// <summary>Số lượt của vòng lặp (chẩn đoán/test: vòng lặp nhàn rỗi không quay rỗng).</summary>
    internal long PassCount { get; private set; }

    internal Win32UiDispatcher Dispatcher => _dispatcher;

    public void AddFrameClock(FrameClockBase clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _dispatcher.VerifyAccess();
        if (Array.IndexOf(_clocks, clock) < 0)
        {
            _clocks = [.. _clocks, clock];
        }
    }

    public void RemoveFrameClock(FrameClockBase clock)
    {
        _dispatcher.VerifyAccess();
        _clocks = Array.FindAll(_clocks, c => !ReferenceEquals(c, clock));
    }

    public void AddMessageFilter(IMessageFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        _dispatcher.VerifyAccess();
        _filters = [.. _filters, filter];
    }

    public void RemoveMessageFilter(IMessageFilter filter)
    {
        _dispatcher.VerifyAccess();
        _filters = Array.FindAll(_filters, f => !ReferenceEquals(f, filter));
    }

    /// <summary>Kết thúc <see cref="Run"/> của thread hiện tại với mã <paramref name="exitCode"/> (PostQuitMessage).</summary>
    public static void Quit(int exitCode) => User32.PostQuitMessage(exitCode);

    /// <summary>Hàng đợi message của thread hiện tại còn message (không lấy ra).</summary>
    internal static bool IsMessagePending()
    {
        Msg message;
        return User32.PeekMessage(&message, 0, 0, 0, WindowMessages.PmNoRemove);
    }

    /// <summary>Chạy tới WM_QUIT; trả mã thoát của <see cref="Quit"/>. Không lồng.</summary>
    public int Run()
    {
        _dispatcher.VerifyAccess();
        if (_running)
        {
            throw new InvalidOperationException("MessageLoop.Run is not re-entrant.");
        }

        _running = true;
        try
        {
            while (true)
            {
                PassCount++;
                if (_dispatcher.HasHighPriorityWork)
                {
                    _dispatcher.RunHighPriorityBatch();
                }

                PollClocks();

                if (PumpMessages(out var exitCode))
                {
                    return exitCode;
                }

                if (_dispatcher.HasHighPriorityWork)
                {
                    continue;
                }

                if (_dispatcher.HasBackgroundWork)
                {
                    if (!IsMessagePending())
                    {
                        _dispatcher.TryRunBackground();
                    }

                    continue;
                }

                if (!IsMessagePending())
                {
                    Wait();
                }
            }
        }
        finally
        {
            _running = false;
        }
    }

    /// <returns>True khi gặp WM_QUIT.</returns>
    private bool PumpMessages(out int exitCode)
    {
        exitCode = 0;
        Msg message;
        for (var pumped = 0; pumped < MaxMessagesPerPass; pumped++)
        {
            if (!User32.PeekMessage(&message, 0, 0, 0, WindowMessages.PmRemove))
            {
                return false;
            }

            if (message.Message == WindowMessages.WmQuit)
            {
                exitCode = (int)message.WParam;
                return true;
            }

            if (!Filtered(message))
            {
                User32.TranslateMessage(&message);
                User32.DispatchMessage(&message);
                WndProcThunk.RethrowPending();
            }

            if (_dispatcher.HasHighPriorityWork)
            {
                return false;
            }
        }

        return false;
    }

    private bool Filtered(in Msg message)
    {
        var filters = _filters;
        if (filters.Length == 0)
        {
            return false;
        }

        var windowMessage = new WindowMessage(message.Hwnd, message.Message, message.WParam, message.LParam);
        foreach (var filter in filters)
        {
            if (filter.PreFilterMessage(in windowMessage))
            {
                return true;
            }
        }

        return false;
    }

    private void PollClocks()
    {
        foreach (var clock in _clocks)
        {
            if (!clock.IsArmed)
            {
                continue;
            }

            var handle = clock.WaitHandle;
            if (handle != 0 && PendingKernel32.WaitForSingleObject(handle, 0) == WindowMessages.WaitObject0)
            {
                clock.OnSignaled();
            }
            else if (clock.TicksUntilDeadline <= 0)
            {
                clock.OnDeadline();
            }
        }
    }

    private void Wait()
    {
        var handles = stackalloc nint[MaxWaitHandles];
        var count = 0;
        var untilDeadline = long.MaxValue;
        foreach (var clock in _clocks)
        {
            if (!clock.IsArmed)
            {
                continue;
            }

            untilDeadline = Math.Min(untilDeadline, clock.TicksUntilDeadline);
            if (clock.WaitHandle != 0 && count < MaxWaitHandles)
            {
                handles[count] = clock.WaitHandle;
                _waitOwners[count] = clock;
                count++;
            }
        }

        var timeout = WindowMessages.InfiniteWait;
        if (untilDeadline != long.MaxValue)
        {
            var ms = untilDeadline <= 0 ? 0 : ((untilDeadline * 1000) + Stopwatch.Frequency - 1) / Stopwatch.Frequency;
            timeout = (uint)Math.Min(ms, WindowMessages.InfiniteWait - 1);
        }

        WaitCount++;
        var result = User32.MsgWaitForMultipleObjectsEx((uint)count, handles, timeout, WindowMessages.QsAllInput,
            WindowMessages.MwmoInputAvailable);
        try
        {
            if (result == WindowMessages.WaitFailed)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "MsgWaitForMultipleObjectsEx failed");
            }

            if (result < (uint)count)
            {
                _waitOwners[result]!.OnSignaled(); // lần chờ đã tiêu tín hiệu (timer đồng bộ / semaphore)
            }
        }
        finally
        {
            Array.Clear(_waitOwners, 0, count);
        }
    }
}

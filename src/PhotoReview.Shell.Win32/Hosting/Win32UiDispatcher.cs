using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Shell.Interop;
using PhotoReview.Shell.Win32.Hosting.PendingInterop;

namespace PhotoReview.Shell.Win32.Hosting;

/// <summary>
/// C-04 trên Win32 (NO-WPF-EXEC-PLAN mục 5): hàng đợi theo mức ưu tiên gắn với MỘT UI thread (thread tạo nó), đánh thức
/// bằng <c>PostMessage(hwndMessageOnly, WM_APP+1)</c>.
/// <list type="bullet">
/// <item>Send &gt; Normal &gt; Render: chạy theo mức (mức cao trước, cùng mức FIFO) trong một lượt "xả" của
/// <see cref="MessageLoop"/>, TRƯỚC khi xử lý message kế tiếp và trước khi phát khung; việc post trong lượt cũng chạy trong lượt
/// đó (xả tới rỗng, như ưu tiên trên Input của WPF) - chỉ ngắt sau <see cref="BatchSliceMs"/> để input không chết đói.</item>
/// <item>Background: một việc mỗi lần, chỉ khi hàng đợi message của thread rỗng và không còn việc mức cao.</item>
/// <item>Trong vòng lặp modal không phải của ta (kéo resize, menu, MessageBox), WM_APP+1 vẫn được dispatch nên hàng đợi
/// vẫn chạy (<see cref="OnWakeMessage"/>).</item>
/// <item>Ngoại lệ của <see cref="Post(Action, UiPriority)"/> được log, vòng lặp sống tiếp; của <see cref="InvokeAsync(Action, UiPriority)"/>
/// đi vào Task trả về.</item>
/// <item>Sau <see cref="Dispose"/>: Post bị bỏ, InvokeAsync/YieldAsync trả Task đã huỷ (không treo người chờ).</item>
/// </list>
/// Thread-safe cho Post/InvokeAsync/YieldAsync/CheckAccess; phần còn lại chỉ trên UI thread.
/// </summary>
internal sealed unsafe class Win32UiDispatcher : IUiDispatcher, IDisposable
{
    /// <summary>WM_APP+1 gửi tới cửa sổ message-only để đánh thức UI thread.</summary>
    internal const uint WakeMessage = WindowMessages.WmApp + 1;

    /// <summary>Một lượt xả mức cao tự ngắt sau chừng này ms để message (input) được xử lý.</summary>
    internal const int BatchSliceMs = 50;

    private const string ClassName = "PhotoReview.Shell.Dispatcher";
    private const int PriorityCount = 4;

    private static readonly object ClassGate = new();
    private static bool classRegistered;

    [ThreadStatic]
    private static Win32UiDispatcher? current;

    private readonly object _gate = new();
    private readonly Queue<WorkItem>[] _queues = [new(), new(), new(), new()];
    private readonly int _threadId;
    private readonly ILog _log;
    private readonly nint _hwnd;
    private int _highCount;      // Send + Normal + Render đang chờ (dưới _gate)
    private int _wakePending;    // 1 = đã có WM_APP+1 chưa được xả (Interlocked)
    private bool _shutDown;      // dưới _gate
    private long _wakeMessagesPosted;

    /// <summary>Tạo dispatcher cho thread hiện tại (thread đó phải bơm message: <see cref="MessageLoop"/>).</summary>
    public Win32UiDispatcher(ILog? log = null)
    {
        if (current is not null)
        {
            throw new InvalidOperationException("This thread already has a Win32UiDispatcher.");
        }

        _log = log ?? NullLog.Instance;
        _threadId = Environment.CurrentManagedThreadId;
        EnsureClassRegistered();
        _hwnd = User32.CreateWindowEx(0, ClassName, string.Empty, 0, 0, 0, 0, 0, PendingUser32.HwndMessage, 0,
            Kernel32.GetModuleHandle(0), 0);
        if (_hwnd == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateWindowEx (message-only dispatcher window) failed");
        }

        current = this;
    }

    /// <summary>Dispatcher của thread đang chạy, nếu có.</summary>
    internal static Win32UiDispatcher? FromCurrentThread => current;

    internal nint MessageWindow => _hwnd;

    /// <summary>Số WM_APP+1 đã gửi (chẩn đoán/test: việc post không làm ngập hàng đợi message).</summary>
    internal long WakeMessagesPosted => Interlocked.Read(ref _wakeMessagesPosted);

    internal bool IsShutDown
    {
        get
        {
            lock (_gate)
            {
                return _shutDown;
            }
        }
    }

    internal bool HasHighPriorityWork
    {
        get
        {
            lock (_gate)
            {
                return _highCount > 0;
            }
        }
    }

    internal bool HasBackgroundWork
    {
        get
        {
            lock (_gate)
            {
                return _queues[(int)UiPriority.Background].Count > 0;
            }
        }
    }

    public bool CheckAccess() => Environment.CurrentManagedThreadId == _threadId;

    public void Post(Action action) => Post(action, UiPriority.Normal);

    public void Post(Action action, UiPriority priority)
    {
        ArgumentNullException.ThrowIfNull(action);
        _ = TryEnqueue(new WorkItem(action, null, null, null), priority);
    }

    public Task InvokeAsync(Action action) => InvokeAsync(action, UiPriority.Normal);

    public Task InvokeAsync(Action action, UiPriority priority)
    {
        ArgumentNullException.ThrowIfNull(action);
        // Không chạy continuation lạ (của thread pool) đồng bộ trên UI thread khi việc xong.
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!TryEnqueue(new WorkItem(action, null, null, completion), priority))
        {
            completion.TrySetCanceled();
        }

        return completion.Task;
    }

    public ValueTask YieldAsync(CancellationToken cancellationToken = default) => YieldAsync(UiPriority.Background, cancellationToken);

    public ValueTask YieldAsync(UiPriority priority, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled(cancellationToken);
        }

        // Không RunContinuationsAsynchronously: phần sau `await YieldAsync(p)` chạy NGAY trong việc mức p (cùng
        // SynchronizationContext của UI thread thì awaiter inline), đúng nghĩa "nhường tới mức p".
        var completion = new TaskCompletionSource();
        var registration = cancellationToken.CanBeCanceled
            ? cancellationToken.Register(static (s, token) => ((TaskCompletionSource)s!).TrySetCanceled(token), completion)
            : default;
        // completion là Completion của việc: Execute đặt kết quả sau khi gỡ đăng ký huỷ; Dispose huỷ nó (người await không treo).
        var item = new WorkItem(() => registration.Dispose(), null, null, completion);
        if (!TryEnqueue(item, priority))
        {
            registration.Dispose();
            completion.TrySetCanceled(CancellationToken.None);
        }

        return new ValueTask(completion.Task);
    }

    /// <summary>Đường của <see cref="Win32UiSynchronizationContext"/> (không cấp closure). False khi đã dừng.</summary>
    internal bool TryPost(SendOrPostCallback callback, object? state, UiPriority priority) =>
        TryEnqueue(new WorkItem(null, callback, state, null), priority);

    /// <summary>
    /// Xả Send/Normal/Render theo mức tới khi rỗng (hoặc hết <see cref="BatchSliceMs"/>). UI thread.
    /// </summary>
    internal void RunHighPriorityBatch()
    {
        VerifyAccess();
        Volatile.Write(ref _wakePending, 0);
        var started = Stopwatch.GetTimestamp();
        while (TryDequeueHigh(out var item))
        {
            Execute(item);
            if (Stopwatch.GetElapsedTime(started).TotalMilliseconds >= BatchSliceMs)
            {
                break;
            }
        }
    }

    /// <summary>Chạy một việc Background nếu có. Người gọi bảo đảm hàng đợi message rỗng. UI thread.</summary>
    internal bool TryRunBackground()
    {
        VerifyAccess();
        WorkItem item;
        lock (_gate)
        {
            if (_highCount > 0 || !_queues[(int)UiPriority.Background].TryDequeue(out item))
            {
                return false;
            }
        }

        Execute(item);
        return true;
    }

    /// <summary>
    /// WM_APP+1 (từ vòng lặp của ta hoặc vòng lặp modal của hệ thống): xả mức cao; chạy một việc Background khi hàng đợi
    /// message rỗng; tự đánh thức lại nếu còn việc chạy được.
    /// </summary>
    internal void OnWakeMessage()
    {
        RunHighPriorityBatch();
        var ranBackground = false;
        if (!HasHighPriorityWork && HasBackgroundWork && !MessageLoop.IsMessagePending())
        {
            ranBackground = TryRunBackground();
        }

        if (HasHighPriorityWork || (ranBackground && HasBackgroundWork))
        {
            RequestWake();
        }
    }

    /// <summary>Dừng nhận việc; việc còn chờ: Post bị bỏ, InvokeAsync/YieldAsync huỷ. Huỷ cửa sổ message-only. UI thread.</summary>
    public void Dispose()
    {
        VerifyAccess();
        List<WorkItem> abandoned = [];
        lock (_gate)
        {
            if (_shutDown)
            {
                return;
            }

            _shutDown = true;
            foreach (var queue in _queues)
            {
                abandoned.AddRange(queue);
                queue.Clear();
            }

            _highCount = 0;
        }

        foreach (var item in abandoned)
        {
            item.Completion?.TrySetCanceled();
        }

        User32.DestroyWindow(_hwnd);
        if (ReferenceEquals(current, this))
        {
            current = null;
        }
    }

    internal void VerifyAccess()
    {
        if (!CheckAccess())
        {
            throw new InvalidOperationException("This Win32UiDispatcher member must be called on its UI thread.");
        }
    }

    private bool TryEnqueue(WorkItem item, UiPriority priority)
    {
        if ((uint)priority >= PriorityCount)
        {
            throw new ArgumentOutOfRangeException(nameof(priority), priority, "Unknown UiPriority.");
        }

        lock (_gate)
        {
            if (_shutDown)
            {
                return false;
            }

            _queues[(int)priority].Enqueue(item);
            if (priority != UiPriority.Background)
            {
                _highCount++;
            }
        }

        RequestWake();
        return true;
    }

    private bool TryDequeueHigh(out WorkItem item)
    {
        lock (_gate)
        {
            for (var p = 0; p < (int)UiPriority.Background; p++)
            {
                if (_queues[p].TryDequeue(out item))
                {
                    _highCount--;
                    return true;
                }
            }
        }

        item = default;
        return false;
    }

    /// <summary>
    /// Giao thức cờ: người post đặt cờ rồi mới gửi WM_APP+1 nếu cờ đang 0; lượt xả xoá cờ TRƯỚC khi đọc hàng đợi. Nên mỗi
    /// lượt xả có tối đa một WM_APP+1 chờ (không ngập hàng đợi message 10.000 mục của thread) và không việc nào bị bỏ quên.
    /// </summary>
    private void RequestWake()
    {
        if (Interlocked.Exchange(ref _wakePending, 1) != 0)
        {
            return;
        }

        if (User32.PostMessage(_hwnd, WakeMessage, 0, 0))
        {
            Interlocked.Increment(ref _wakeMessagesPosted);
            return;
        }

        // Hàng đợi message đầy hoặc cửa sổ đã huỷ: cho lần post sau thử lại; vòng lặp của ta vẫn thấy việc qua HasHighPriorityWork.
        Volatile.Write(ref _wakePending, 0);
        _log.Warn($"Win32UiDispatcher: PostMessage(WM_APP+1) failed (error {Marshal.GetLastPInvokeError().ToString(System.Globalization.CultureInfo.InvariantCulture)})");
    }

    private void Execute(in WorkItem item)
    {
        try
        {
            if (item.Action is not null)
            {
                item.Action();
            }
            else
            {
                item.Callback!(item.State);
            }

            item.Completion?.TrySetResult();
        }
        catch (Exception ex)
        {
            if (item.Completion is not null)
            {
                item.Completion.TrySetException(ex);
            }
            else
            {
                _log.Error("Win32UiDispatcher: a posted UI action threw; the message loop keeps running", ex);
            }
        }
    }

    private static void EnsureClassRegistered()
    {
        lock (ClassGate)
        {
            if (classRegistered)
            {
                return;
            }

            fixed (char* className = ClassName)
            {
                var windowClass = new WndClassEx
                {
                    Size = (uint)sizeof(WndClassEx),
                    WndProc = WndProcThunk.DispatcherProc,
                    Instance = Kernel32.GetModuleHandle(0),
                    ClassName = className,
                };
                if (User32.RegisterClassEx(&windowClass) == 0)
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "RegisterClassEx (dispatcher window) failed");
                }
            }

            classRegistered = true;
        }
    }

    private readonly record struct WorkItem(Action? Action, SendOrPostCallback? Callback, object? State, TaskCompletionSource? Completion);
}

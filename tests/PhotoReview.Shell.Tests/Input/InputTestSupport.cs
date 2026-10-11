using System.ComponentModel;
using PhotoReview.App.Input;
using PhotoReview.App.ViewModels;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Settings;
using PhotoReview.Shell.Interop;
using PhotoReview.Shell.Win32.Hosting;
using PhotoReview.Shell.Win32.Input;
using PhotoReview.Shell.Win32.Viewing;

namespace PhotoReview.Shell.Tests.Input;

/// <summary>IFrameClock điều khiển bằng tay: test phát từng nhịp với RenderingTime cố định (đà kinetic).</summary>
internal sealed class ManualFrameClock : IFrameClock
{
    public event EventHandler<FrameTickEventArgs>? Frame;

    public int Requests { get; private set; }

    public int SubscriberCount => Frame?.GetInvocationList().Length ?? 0;

    public void RequestFrame() => Requests++;

    public void Tick(double renderingTimeMs) => Frame?.Invoke(this, new FrameTickEventArgs(new FrameTick(0, renderingTimeMs, null)));
}

/// <summary>
/// <see cref="IShellInputHost"/> giả: ghi các lệnh không thuộc viewport mà <see cref="ShellInput"/> chuyển cho cửa sổ chính,
/// và để test đặt trạng thái (có ảnh, fullscreen, so sánh...).
/// </summary>
internal sealed class RecordingInputHost : IShellInputHost
{
    public bool HasImages { get; set; } = true;

    public bool IsFullscreen { get; set; }

    public bool IsCompareVisible { get; set; }

    public bool CurrentHasComparePair { get; set; }

    public bool CurrentHasCapturePair { get; set; }

    public bool DuplicateCheckRunning { get; set; }

    public List<string> Log { get; } = [];

    public List<ReviewCommand> Commands { get; } = [];

    public int Nexts { get; private set; }

    public int Previouses { get; private set; }

    /// <summary>Nếu đặt, <see cref="ExecuteAsync"/> ném ngoại lệ này (kiểm đường báo lỗi).</summary>
    public Exception? ExecuteFailure { get; set; }

    /// <summary>`ViewerState.ZoomToActualSize` của cửa sổ (view model: `ZoomActualSize`); test nối vào viewer của họ.</summary>
    public Action? ZoomActualSizeAction { get; set; }

    public Task NextAsync()
    {
        Nexts++;
        return Task.CompletedTask;
    }

    public Task PreviousAsync()
    {
        Previouses++;
        return Task.CompletedTask;
    }

    public void ZoomActualSize()
    {
        Log.Add("ZoomActualSize");
        ZoomActualSizeAction?.Invoke();
    }

    public bool CancelDuplicateCheck()
    {
        if (!DuplicateCheckRunning)
        {
            return false;
        }

        DuplicateCheckRunning = false;
        Log.Add("CancelDuplicateCheck");
        return true;
    }

    public Task ExecuteAsync(ReviewCommand command)
    {
        Commands.Add(command);
        return ExecuteFailure is { } failure ? Task.FromException(failure) : Task.CompletedTask;
    }

    public void ReportActionFailed(string actionName, Exception exception) => Log.Add($"failed:{actionName}:{exception.Message}");
}

/// <summary>
/// <see cref="IShellWindow"/> không HWND: kích thước/DPI/capture do test đặt, danh sách handler thật được gọi bằng
/// <see cref="Dispatch"/> theo thứ tự đăng ký (giống <c>ShellWindow.ProcessMessage</c>).
/// </summary>
internal sealed class FakeShellWindow : IShellWindow
{
    private readonly List<IWindowMessageHandler> _handlers = [];

    public nint Hwnd => 0x1234;

    public double DpiScale { get; set; } = 1.0;

    public SizeD ClientSizeDip { get; set; } = new(1000, 600);

    public bool IsActive { get; set; } = true;

    public bool IsLoaded { get; set; } = true;

    public bool HasPointerCapture { get; private set; }

    public ShellCursor Cursor { get; private set; }

    public int Invalidations { get; private set; }

    public int CaptureCalls { get; private set; }

    public int Releases { get; private set; }

    /// <summary>Gốc toạ độ client trong toạ độ màn hình (pixel) cho <see cref="ScreenToClientDip"/>.</summary>
    public PointD ClientOriginPixel { get; set; }

    public IReadOnlyList<IWindowMessageHandler> Handlers => _handlers;

    public event EventHandler? ClientSizeChanged;

    public event EventHandler? DpiChanged
    {
        add { }
        remove { }
    }

    public event EventHandler? Activated
    {
        add { }
        remove { }
    }

    public event EventHandler? Deactivated;

    public event EventHandler<CancelEventArgs>? Closing
    {
        add { }
        remove { }
    }

    public event EventHandler? Closed
    {
        add { }
        remove { }
    }

    public void Resize(double width, double height)
    {
        ClientSizeDip = new SizeD(width, height);
        ClientSizeChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RaiseDeactivated() => Deactivated?.Invoke(this, EventArgs.Empty);

    public void AddMessageHandler(IWindowMessageHandler handler) => _handlers.Add(handler);

    public void Invalidate() => Invalidations++;

    public void SetTitle(string title)
    {
    }

    public void SetCursor(ShellCursor cursor) => Cursor = cursor;

    public void CapturePointer()
    {
        CaptureCalls++;
        HasPointerCapture = true;
    }

    public void ReleasePointer()
    {
        Releases++;
        HasPointerCapture = false;
    }

    public PointD ScreenToClientDip(PointD screenPixel) =>
        new((screenPixel.X - ClientOriginPixel.X) / DpiScale, (screenPixel.Y - ClientOriginPixel.Y) / DpiScale);

    public void Close()
    {
    }

    /// <summary>Chạy thông điệp qua các handler; trả (đã xử lý, kết quả).</summary>
    public (bool Handled, nint Result) Dispatch(uint message, nint wParam, nint lParam)
    {
        var windowMessage = new WindowMessage(Hwnd, message, wParam, lParam);
        foreach (var handler in _handlers)
        {
            if (handler.TryHandle(windowMessage, out var result))
            {
                return (true, result);
            }
        }

        return (false, 0);
    }
}

/// <summary>Hàng đợi theo mức (Send &gt; Normal &gt; Render &gt; Background), chạy khi <see cref="Drain"/>; continuation chạy tại chỗ.</summary>
internal sealed class QueueDispatcher : IUiDispatcher
{
    private readonly SortedDictionary<UiPriority, Queue<Action>> _queues = [];

    public List<UiPriority> PostedPriorities { get; } = [];

    public bool CheckAccess() => true;

    public void Post(Action action) => Post(action, UiPriority.Normal);

    public void Post(Action action, UiPriority priority)
    {
        PostedPriorities.Add(priority);
        if (!_queues.TryGetValue(priority, out var queue))
        {
            _queues[priority] = queue = new Queue<Action>();
        }

        queue.Enqueue(action);
    }

    public Task InvokeAsync(Action action) => InvokeAsync(action, UiPriority.Normal);

    public Task InvokeAsync(Action action, UiPriority priority)
    {
        var done = new TaskCompletionSource();
        Post(() =>
        {
            action();
            done.SetResult();
        }, priority);
        return done.Task;
    }

    public ValueTask YieldAsync(CancellationToken cancellationToken = default) => YieldAsync(UiPriority.Background, cancellationToken);

    public ValueTask YieldAsync(UiPriority priority, CancellationToken cancellationToken = default) =>
        new(InvokeAsync(static () => { }, priority));

    public void Drain()
    {
        while (_queues.Values.FirstOrDefault(q => q.Count > 0) is { } queue)
        {
            queue.Dequeue()();
        }
    }
}

/// <summary>
/// "Cửa sổ + viewport + nhập liệu" không HWND: <see cref="FakeShellWindow"/>, <see cref="QueueDispatcher"/>, đồng hồ khung tay,
/// ViewerState thật và <see cref="ShellInput"/> thật cùng ba handler đăng vào cửa sổ giả. Mọi thứ hệ thống ngoài thông điệp
/// (phím chỉnh, dấu thời gian, thiết bị wheel) do test đặt qua các thuộc tính.
/// </summary>
internal sealed class InputRig : IDisposable
{
    private (double Width, double Height) _bitmap;

    public InputRig(double clientWidth = 1000, double clientHeight = 600, int imageWidth = 3000, int imageHeight = 2000,
        Action<AppSettings>? configure = null, double dpiScale = 1.0)
    {
        Settings = new AppSettings();
        configure?.Invoke(Settings);
        Window = new FakeShellWindow { ClientSizeDip = new SizeD(clientWidth, clientHeight), DpiScale = dpiScale };
        Dispatcher = new QueueDispatcher();
        Clock = new ManualFrameClock();
        Host = new RecordingInputHost();
        Viewer = new ViewerState { DpiScale = dpiScale, ZoomStep = Settings.KeyboardZoomStepPercent / 100.0 };
        Viewer.SetSourceSize(imageWidth, imageHeight, newImage: true);
        _bitmap = (imageWidth, imageHeight);
        Viewport = new ViewportController(Window, Dispatcher, Clock, ContentFromViewer, UpdateFitSize, new ViewportControllerOptions
        {
            DragMetricsPixels = () => (4, 4),
            Timestamp = () => Timestamp,
        });
        Viewer.PropertyChanged += (_, _) => Viewport.InvalidateLayout();
        Environment = new ShellInputEnvironment
        {
            Modifiers = () => Modifiers,
            MessageTime = () => MessageTime,
            TickCount = () => TickCount,
            WheelDevice = () => WheelDevice,
            DoubleClickTimeMs = () => 500,
            DoubleClickSizePixels = _ => (4, 4),
        };
        Host.ZoomActualSizeAction = Viewer.ZoomToActualSize;
        Input = new ShellInput(Window, Viewport, Viewer, () => Settings, Host, Environment);
        Drain();
    }

    public AppSettings Settings { get; }

    public FakeShellWindow Window { get; }

    public QueueDispatcher Dispatcher { get; }

    public ManualFrameClock Clock { get; }

    public RecordingInputHost Host { get; }

    public ViewerState Viewer { get; }

    public ViewportController Viewport { get; }

    public ShellInput Input { get; }

    public ShellInputEnvironment Environment { get; }

    public KeyModifiers Modifiers { get; set; }

    public int MessageTime { get; set; }

    public int TickCount { get; set; }

    public WheelDeviceHint WheelDevice { get; set; }

    public long Timestamp { get; set; }

    private ViewportContent ContentFromViewer() => new(Viewer.Stretch, Viewer.ImageWidth, Viewer.ImageHeight, Viewer.MaxImageWidth,
        Viewer.MaxImageHeight, _bitmap.Width, _bitmap.Height, Viewer.IsFit);

    private void UpdateFitSize()
    {
        var (width, height) = Viewport.ViewportSize;
        Viewer.UpdateViewport(width, height);
    }

    /// <summary>Chạy hàng đợi tới khi rỗng (layout ở Render, continuation của thao tác async).</summary>
    public void Drain() => Dispatcher.Drain();

    /// <summary>Đưa ảnh về Fit (như ApplyFit): tới khi hội tụ.</summary>
    public void Fit()
    {
        var task = Input.ApplyFitAsync();
        Pump(task);
    }

    /// <summary>Bơm hàng đợi tới khi <paramref name="task"/> xong (có giới hạn).</summary>
    public void Pump(Task task)
    {
        for (var i = 0; i < 100 && !task.IsCompleted; i++)
        {
            Drain();
        }

        Drain();
        Assert.True(task.IsCompleted, "thao tác không xong sau 100 lượt bơm");
        task.GetAwaiter().GetResult();
    }

    public (bool Handled, nint Result) Send(uint message, nint wParam, nint lParam)
    {
        var outcome = Window.Dispatch(message, wParam, lParam);
        Drain();
        return outcome;
    }

    public static nint Pack(int x, int y) => (nint)(((uint)(ushort)y << 16) | (ushort)x);

    public (bool Handled, nint Result) Mouse(uint message, int x, int y, int keyFlags = 0) => Send(message, keyFlags, Pack(x, y));

    public (bool Handled, nint Result) Wheel(uint message, int delta, int x, int y, bool ctrl = false) =>
        Send(message, (nint)(((uint)(ushort)(short)delta << 16) | (uint)(ctrl ? InputNative.MkControl : 0)), Pack(x, y));

    public (bool Handled, nint Result) KeyDown(int virtualKey, bool repeat = false, bool system = false, bool extended = false, int scanCode = 0)
    {
        long lParam = 1 | ((long)scanCode << 16);
        if (repeat)
        {
            lParam |= InputNative.KeyRepeatBit;
        }

        if (extended)
        {
            lParam |= InputNative.KeyExtendedBit;
        }

        return Send(system ? PhotoReview.Shell.Interop.WindowMessages.WmSysKeyDown : PhotoReview.Shell.Interop.WindowMessages.WmKeyDown,
            virtualKey, (nint)lParam);
    }

    public (bool Handled, nint Result) KeyDown(KeyId key, bool repeat = false, bool system = false) =>
        KeyDown(KeyIdMapping.ToVirtualKey(key), repeat, system);

    public void Dispose()
    {
        Input.Dispose();
        Viewport.Dispose();
    }
}

using System.ComponentModel;
using PhotoReview.App.Input;
using PhotoReview.App.ViewModels;
using PhotoReview.Core.Abstractions;
using PhotoReview.Shell.Win32.Hosting;
using PhotoReview.Shell.Win32.Viewing;

namespace PhotoReview.Shell.Tests.Viewport;

/// <summary>
/// WP-16: <see cref="ViewportController"/> trên cửa sổ/dispatcher/đồng hồ khung giả (không HWND): lượt layout xếp ở mức Render
/// và gộp, ScrollTo áp ở lượt layout, YieldToRenderAsync xong sau lượt layout đã xếp, resize -> UpdateFitSize, nhánh
/// MainImage_SizeChanged khi Fit, DragThreshold theo DPI, PointerPosition trong viewport, chuột/con trỏ/khung render.
/// </summary>
public sealed class ViewportControllerTests
{
    private readonly FakeWindow _window = new();
    private readonly FakeDispatcher _dispatcher = new();
    private readonly FakeFrameClock _clock = new();
    private ViewportContent _content = new(ViewerStretchMode.None, 3000, 2000, double.PositiveInfinity, double.PositiveInfinity, 750, 500, IsFit: false);
    private int _fitUpdates;

    private ViewportController Create(ViewportControllerOptions? options = null) =>
        new(_window, _dispatcher, _clock, () => _content, () => _fitUpdates++, options);

    [Fact]
    public void Construction_PostsOneLayoutAtRenderPriority_ThatReadsTheWindowAndTheContent()
    {
        using var controller = Create();
        Assert.Equal([UiPriority.Render], _dispatcher.PostedPriorities);
        Assert.True(controller.IsLayoutPosted);
        Assert.Equal(0, controller.ExtentWidth);

        _dispatcher.Drain();

        Assert.False(controller.IsLayoutPosted);
        Assert.Equal(3000, controller.ExtentWidth);
        Assert.Equal(990, controller.ViewportWidth); // 1000 client - thanh dọc 10
        Assert.Equal(590, controller.ViewportHeight);
        Assert.Equal(1, _window.Invalidations);
    }

    [Fact]
    public void InvalidateLayout_IsCoalescedUntilThePostedLayoutRuns()
    {
        using var controller = Create();
        controller.InvalidateLayout();
        controller.ScrollTo(10, 10);
        controller.InvalidateLayout();
        Assert.Single(_dispatcher.PostedPriorities);
        _dispatcher.Drain();
        controller.InvalidateLayout();
        Assert.Equal(2, _dispatcher.PostedPriorities.Count);
    }

    [Fact]
    public void ScrollTo_IsAppliedByTheNextLayout_Clamped_AndRedraws()
    {
        using var controller = Create();
        _dispatcher.Drain();
        var updates = 0;
        controller.LayoutUpdated += (_, _) => updates++;

        controller.ScrollTo(5000, 100);
        Assert.Equal(0, controller.HorizontalOffset);
        _dispatcher.Drain();

        Assert.Equal(2010, controller.HorizontalOffset); // 3000 - 990
        Assert.Equal(100, controller.VerticalOffset);
        Assert.Equal(1, updates);
        Assert.Equal(2, _window.Invalidations);
    }

    [Fact]
    public void ALayoutThatChangesNothing_DoesNotRedraw()
    {
        using var controller = Create();
        _dispatcher.Drain();
        controller.ScrollTo(0, 0);
        _dispatcher.Drain();
        Assert.Equal(1, _window.Invalidations);
    }

    [Fact]
    public async Task YieldToRenderAsync_CompletesAfterTheLayoutPostedBeforeIt()
    {
        using var controller = Create();
        _dispatcher.Drain();
        controller.ScrollTo(300, 200);
        var yielded = controller.YieldToRenderAsync();
        Assert.False(yielded.IsCompleted);
        double? seen = null;
        var continuation = yielded.ContinueWith(_ => seen = controller.HorizontalOffset, TaskScheduler.Default);

        _dispatcher.Drain();
        await continuation;

        Assert.Equal(300, seen);
        Assert.Equal(UiPriority.Render, _dispatcher.PostedPriorities[^1]);
    }

    [Fact]
    public void UpdateLayout_IsSynchronous_AndRereadsTheContent()
    {
        using var controller = Create();
        _content = _content with { ImageWidth = 500, ImageHeight = 400 };
        controller.UpdateLayout();
        Assert.Equal(500, controller.ImageActualWidth);
        Assert.Equal(new PointD(500 - 250, 300 - 200), controller.ImageOrigin); // căn giữa trong 1000x600
        Assert.Equal(new PointD(50, 60), controller.ToImageElement(new PointD(300, 160)));
    }

    [Fact]
    public void ClientResize_UpdatesTheFitSize_ThenLaysOutAtTheNewSize()
    {
        using var controller = Create();
        _dispatcher.Drain();
        _window.Resize(1600, 900);
        Assert.Equal(1, _fitUpdates);
        _dispatcher.Drain();
        Assert.Equal(1590, controller.ViewportWidth);
        Assert.Equal((1600.0, 900.0), controller.ViewportSize);
    }

    [Fact]
    public void FitImageChangingSize_RunsUpdateFitSize_LikeMainImageSizeChanged_ButNotWhenZoomed()
    {
        _content = new ViewportContent(ViewerStretchMode.Uniform, double.NaN, double.NaN, 1000, 600, 1500, 1000, IsFit: true);
        using var controller = Create();
        _dispatcher.Drain();
        Assert.Equal(1, _fitUpdates); // 0x0 -> 900x600

        _content = _content with { BitmapWidth = 1000, BitmapHeight = 1000 };
        controller.UpdateLayout();
        Assert.Equal(2, _fitUpdates);

        _content = new ViewportContent(ViewerStretchMode.None, 2000, 2000, double.PositiveInfinity, double.PositiveInfinity, 1000, 1000, IsFit: false);
        controller.UpdateLayout();
        Assert.Equal(2, _fitUpdates);
    }

    [Fact]
    public void FitSizeHook_FeedsBackIntoTheSameLayout_UntilStable()
    {
        // UpdateFitSize đổi MaxImage (như ViewerState.UpdateViewport) -> lượt layout chạy lại trong cùng UpdateLayout.
        _content = new ViewportContent(ViewerStretchMode.Uniform, double.NaN, double.NaN, double.PositiveInfinity, double.PositiveInfinity, 1500, 1000, IsFit: true);
        using var controller = new ViewportController(_window, _dispatcher, _clock, () => _content,
            () => _content = _content with { MaxImageWidth = 1000, MaxImageHeight = 600 });
        controller.UpdateLayout();
        Assert.Equal(900, controller.ImageActualWidth, 9);
        Assert.False(controller.Layout.VerticalBarVisible);
    }

    [Theory]
    [InlineData(1.0, 4.0)]
    [InlineData(1.5, 4.0 / 1.5)]
    [InlineData(2.0, 2.0)]
    [InlineData(0.0, 4.0)]      // DPI hỏng -> 1
    [InlineData(double.NaN, 4.0)]
    [InlineData(17.0, 4.0)]
    [InlineData(0.25, 16.0)]    // biên dưới của khoảng hợp lệ 0,25..16 (đóng) như ViewerState.NormalizeDpi
    [InlineData(16.0, 0.25)]    // biên trên
    public void DragThreshold_IsTheSystemMetricInDips(double dpi, double expected)
    {
        _window.DpiScale = dpi;
        using var controller = Create();
        Assert.Equal(expected, controller.DragThreshold.Horizontal, 9);
        Assert.Equal(expected, controller.DragThreshold.Vertical, 9);
    }

    [Fact]
    public void DragThreshold_UsesBothAxesOfTheMetric()
    {
        _window.DpiScale = 1.25;
        using var controller = Create(new ViewportControllerOptions { DragMetricsPixels = () => (5, 10) });
        Assert.Equal((4.0, 8.0), controller.DragThreshold);
    }

    [Theory]
    [InlineData(10, 10, true)]
    [InlineData(0, 0, true)]
    [InlineData(990, 590, true)]   // biên viewport (đã trừ thanh) tính là trong
    [InlineData(990.5, 10, false)]
    [InlineData(10, 590.5, false)]
    [InlineData(-0.5, 10, false)]
    [InlineData(10, -0.5, false)]
    public void PointerPosition_IsTheCursorInViewportDips_OnlyInsideTheViewport(double x, double y, bool inside)
    {
        _window.DpiScale = 2.0;
        using var controller = Create(new ViewportControllerOptions { CursorScreenPixel = () => new PointD((x * 2) + 100, (y * 2) + 40) });
        _window.ClientOriginPixel = new PointD(100, 40);
        _dispatcher.Drain();
        Assert.Equal(inside ? new PointD(x, y) : null, controller.PointerPosition);
    }

    [Fact]
    public void PointerPosition_UnknownCursor_IsNull()
    {
        using var controller = Create();
        _dispatcher.Drain();
        Assert.Null(controller.PointerPosition);
    }

    [Fact]
    public void MouseCaptureAndCursor_GoToTheWindow_ReleaseOnlyWhenHeld()
    {
        using var controller = Create();
        controller.ReleaseMouseCapture();
        Assert.Equal(0, _window.Releases);
        controller.CaptureMouse();
        Assert.True(_window.HasPointerCapture);
        controller.ReleaseMouseCapture();
        Assert.False(_window.HasPointerCapture);
        Assert.Equal(1, _window.Releases);

        controller.SetPanCursor(true);
        Assert.Equal(ShellCursor.SizeAll, _window.Cursor);
        controller.SetPanCursor(false);
        Assert.Equal(ShellCursor.Arrow, _window.Cursor);
    }

    [Fact]
    public void RenderFrames_ReachHookedHandlers_WithTheirRenderingTime_UntilUnhooked()
    {
        using var controller = Create();
        var times = new List<TimeSpan?>();
        EventHandler handler = (_, e) => times.Add(controller.RenderingTime(e));

        controller.HookRenderFrame(handler);
        Assert.Equal(1, _clock.FrameRequests);
        _clock.Tick(16.5);
        controller.UnhookRenderFrame(handler);
        _clock.Tick(33);

        Assert.Equal([TimeSpan.FromMilliseconds(16.5)], times);
        Assert.Equal(0, _clock.SubscriberCount);
        Assert.Null(controller.RenderingTime(EventArgs.Empty));
    }

    [Fact]
    public void HookingTwice_CallsTwice_AndUnhookRemovesOneAtATime_UnknownHandlersAreIgnored()
    {
        using var controller = Create();
        var calls = 0;
        EventHandler handler = (_, _) => calls++;
        controller.HookRenderFrame(handler);
        controller.HookRenderFrame(handler);
        controller.UnhookRenderFrame((_, _) => { });
        _clock.Tick(1);
        Assert.Equal(2, calls);
        controller.UnhookRenderFrame(handler);
        _clock.Tick(2);
        Assert.Equal(3, calls);
    }

    [Fact]
    public void Dispose_UnhooksEverything_StopsLayout_AndReportsNotLoaded()
    {
        var controller = Create();
        controller.HookRenderFrame((_, _) => { });
        Assert.True(controller.IsLoaded);
        controller.Dispose();
        controller.Dispose();

        Assert.Equal(0, _clock.SubscriberCount);
        Assert.False(controller.IsLoaded);
        _dispatcher.Drain(); // lượt đã xếp trước khi dispose: không làm gì
        Assert.Equal(0, _window.Invalidations);
        controller.InvalidateLayout();
        Assert.Single(_dispatcher.PostedPriorities);
        _window.Resize(500, 500);
        Assert.Equal(0, _fitUpdates);
    }

    [Fact]
    public void IsLoaded_FollowsTheWindow()
    {
        using var controller = Create();
        _window.IsLoaded = false;
        Assert.False(controller.IsLoaded);
    }

    [Fact]
    public void SourceSize_IsTheBitmap_OrNullWithoutOne()
    {
        using var controller = Create();
        Assert.Equal((750.0, 500.0), controller.SourceSize);
        _content = _content with { BitmapWidth = 0 };
        Assert.Null(controller.SourceSize);
        _content = _content with { BitmapWidth = 10, BitmapHeight = 0 };
        Assert.Null(controller.SourceSize);
    }

    [Fact]
    public void ScrollHome_ReturnsToTheOrigin()
    {
        using var controller = Create();
        controller.ScrollTo(400, 300);
        _dispatcher.Drain();
        controller.ScrollHome();
        Assert.True(controller.IsLayoutPosted);
        _dispatcher.Drain();
        Assert.Equal((0.0, 0.0), (controller.HorizontalOffset, controller.VerticalOffset));
    }

    [Fact]
    public void ViewportSize_ReadsAnInvalidClientAsZero()
    {
        using var controller = Create();
        _window.ClientSizeDip = new SizeD(-3, double.NaN);
        Assert.Equal((0.0, 0.0), controller.ViewportSize);
    }

    [Fact]
    public void TimestampAndDisplayTiming_ComeFromTheOptions()
    {
        var display = new FakeDisplayClock();
        using var controller = Create(new ViewportControllerOptions { Timestamp = () => 1234, DisplayClock = display });
        Assert.Equal(1234, controller.Timestamp);
        Assert.Equal(new DisplayTiming(5, 6), controller.DisplayTiming);
        Assert.Equal(_window.Hwnd, display.LastWindow);
        using var noClock = Create();
        Assert.Null(noClock.DisplayTiming);
        Assert.True(noClock.Timestamp > 0);
    }

    [Fact]
    public void Constructor_RejectsNulls()
    {
        Assert.Throws<ArgumentNullException>(() => new ViewportController(null!, _dispatcher, _clock, () => _content, () => { }));
        Assert.Throws<ArgumentNullException>(() => new ViewportController(_window, null!, _clock, () => _content, () => { }));
        Assert.Throws<ArgumentNullException>(() => new ViewportController(_window, _dispatcher, null!, () => _content, () => { }));
        Assert.Throws<ArgumentNullException>(() => new ViewportController(_window, _dispatcher, _clock, null!, () => { }));
        Assert.Throws<ArgumentNullException>(() => new ViewportController(_window, _dispatcher, _clock, () => _content, null!));
        using var controller = Create();
        Assert.Throws<ArgumentNullException>(() => controller.HookRenderFrame(null!));
    }

    [Fact]
    public void EmptyContent_IsAnUnboundedFitWithoutABitmap()
    {
        _content = ViewportContent.Empty;
        using var controller = Create();
        _dispatcher.Drain();
        Assert.True(ViewportContent.Empty.IsFit);
        Assert.Equal(0, controller.ImageActualWidth);
        Assert.False(controller.Layout.HorizontalBarVisible);
        Assert.Null(controller.SourceSize);
    }

    // ---- fakes ----

    private sealed class FakeWindow : IShellWindow
    {
        public nint Hwnd => 0x1234;
        public double DpiScale { get; set; } = 1.0;
        public SizeD ClientSizeDip { get; set; } = new(1000, 600);
        public bool IsActive => true;
        public bool IsLoaded { get; set; } = true;
        public int Invalidations { get; private set; }
        public int Releases { get; private set; }
        public ShellCursor Cursor { get; private set; }
        public bool HasPointerCapture { get; private set; }
        public PointD ClientOriginPixel { get; set; }

        public event EventHandler? ClientSizeChanged;
        public event EventHandler? DpiChanged { add { } remove { } }
        public event EventHandler? Activated { add { } remove { } }
        public event EventHandler? Deactivated { add { } remove { } }
        public event EventHandler<CancelEventArgs>? Closing { add { } remove { } }
        public event EventHandler? Closed { add { } remove { } }

        public void Resize(double width, double height)
        {
            ClientSizeDip = new SizeD(width, height);
            ClientSizeChanged?.Invoke(this, EventArgs.Empty);
        }

        public void AddMessageHandler(IWindowMessageHandler handler) { }
        public void Invalidate() => Invalidations++;
        public void SetTitle(string title) { }
        public void SetCursor(ShellCursor cursor) => Cursor = cursor;
        public void CapturePointer() => HasPointerCapture = true;

        public void ReleasePointer()
        {
            HasPointerCapture = false;
            Releases++;
        }

        public PointD ScreenToClientDip(PointD screenPixel) =>
            new((screenPixel.X - ClientOriginPixel.X) / DpiScale, (screenPixel.Y - ClientOriginPixel.Y) / DpiScale);

        public void Close() { }
    }

    /// <summary>Hàng đợi FIFO theo mức (Send > Normal > Render > Background), chạy khi <see cref="Drain"/>.</summary>
    private sealed class FakeDispatcher : IUiDispatcher
    {
        private readonly SortedDictionary<UiPriority, Queue<Action>> _queues = [];

        public List<UiPriority> PostedPriorities { get; } = [];

        public bool CheckAccess() => true;

        public void Post(Action action) => Post(action, UiPriority.Normal);

        public void Post(Action action, UiPriority priority)
        {
            PostedPriorities.Add(priority);
            if (!_queues.TryGetValue(priority, out var queue)) _queues[priority] = queue = new Queue<Action>();
            queue.Enqueue(action);
        }

        public Task InvokeAsync(Action action) => InvokeAsync(action, UiPriority.Normal);

        public Task InvokeAsync(Action action, UiPriority priority)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(() => { action(); done.SetResult(); }, priority);
            return done.Task;
        }

        public ValueTask YieldAsync(CancellationToken cancellationToken = default) => YieldAsync(UiPriority.Background, cancellationToken);

        public ValueTask YieldAsync(UiPriority priority, CancellationToken cancellationToken = default) => new(InvokeAsync(static () => { }, priority));

        public void Drain()
        {
            while (_queues.Values.FirstOrDefault(q => q.Count > 0) is { } queue) queue.Dequeue()();
        }
    }

    private sealed class FakeFrameClock : IFrameClock
    {
        private EventHandler<FrameTickEventArgs>? _frame;

        public int FrameRequests { get; private set; }
        public int SubscriberCount => _frame?.GetInvocationList().Length ?? 0;

        public event EventHandler<FrameTickEventArgs>? Frame
        {
            add => _frame += value;
            remove => _frame -= value;
        }

        public void RequestFrame() => FrameRequests++;

        public void Tick(double renderingTimeMs) => _frame?.Invoke(this, new FrameTickEventArgs(new FrameTick(0, renderingTimeMs, null)));
    }

    private sealed class FakeDisplayClock : IDisplayClock
    {
        public nint LastWindow { get; private set; }

        public DisplayTiming? GetTiming(IntPtr window)
        {
            LastWindow = window;
            return new DisplayTiming(5, 6);
        }
    }
}

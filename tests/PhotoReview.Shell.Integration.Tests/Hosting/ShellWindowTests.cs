using System.ComponentModel;
using System.Runtime.InteropServices;
using PhotoReview.App.Input;
using PhotoReview.Core.Abstractions;
using PhotoReview.Shell.Tests.Hosting;
using PhotoReview.Shell.Win32.Hosting;
using PhotoReview.Shell.Win32.Hosting.PendingInterop;

namespace PhotoReview.Shell.Integration.Tests.Hosting;

/// <summary>
/// WP-14 (C-15): HWND thật trên UI thread thật (desktop ẩn khi chạy qua tools/run-tests-hidden.ps1). Cửa sổ không bao giờ được
/// kích hoạt bằng API foreground; message được gửi thẳng vào chính cửa sổ (SendMessage/PostMessage, N-8).
/// </summary>
[Trait("Category", "UI")]
[Trait("Category", "Integration")]
public sealed partial class ShellWindowTests
{
    private const uint TestMessage = PendingUser32.WmApp + 60;

    [Fact]
    public async Task Close_ClosingCanceled_KeepsTheWindow_ThenClosesWhenAllowed()
    {
        await using var ui = await UiThread.StartAsync();
        var events = new List<string>();
        var cancel = true;
        var window = await ui.InvokeAsync(() =>
        {
            var w = CreateWindow(ui);
            w.Closing += (_, e) =>
            {
                events.Add("closing");
                e.Cancel = cancel;
            };
            w.Closed += (_, _) => events.Add("closed");
            return w;
        });

        await ui.InvokeAsync(window.Close);
        var stillOpen = await ui.InvokeAsync(() => (window.IsLoaded, PendingUser32.IsWindow(window.Hwnd)));
        cancel = false;
        await ui.InvokeAsync(window.Close);
        var afterClose = await ui.InvokeAsync(() => (window.IsLoaded, PendingUser32.IsWindow(window.Hwnd)));

        Assert.Equal((true, true), stillOpen);
        Assert.Equal((false, false), afterClose);
        Assert.Equal(["closing", "closing", "closed"], events);
    }

    [Fact]
    public async Task PostedWmClose_LikeTheCloseButton_RaisesClosingThenClosed()
    {
        await using var ui = await UiThread.StartAsync();
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var events = new List<string>();
        var hwnd = await ui.InvokeAsync(() =>
        {
            var w = CreateWindow(ui);
            w.Closing += (_, _) => events.Add("closing");
            w.Closed += (_, _) =>
            {
                events.Add("closed");
                closed.TrySetResult();
            };
            return w.Hwnd;
        });

        Assert.True(PendingUser32.PostMessage(hwnd, PendingUser32.WmClose, 0, 0)); // từ thread test
        await closed.Task.WaitAsync(UiThread.Bound);

        Assert.Equal(["closing", "closed"], events);
    }

    [Fact]
    public async Task DpiChanged_SimulatedMessage_AppliesTheSuggestedRectBeforeRaisingTheEvent()
    {
        await using var ui = await UiThread.StartAsync();
        var window = await ui.InvokeAsync(() => CreateWindow(ui));
        var observed = new List<(double Scale, PendingRect Rect)>();
        var sizeChanges = 0;
        await ui.InvokeAsync(() =>
        {
            window.DpiChanged += (_, _) => observed.Add((window.DpiScale, WindowRect(window.Hwnd)));
            window.ClientSizeChanged += (_, _) => sizeChanges++;
        });
        var suggested = new PendingRect(40, 50, 40 + 800, 50 + 600);

        var (scale, client, clientPixels) = await ui.InvokeAsync(() =>
        {
            SendDpiChanged(window.Hwnd, 144, suggested);
            return (window.DpiScale, window.ClientSizeDip, window.ClientSizePixels);
        });

        Assert.Equal(1.5, scale);
        var single = Assert.Single(observed);
        Assert.Equal(1.5, single.Scale);
        Assert.Equal(suggested, single.Rect);
        Assert.Equal(new SizeD(clientPixels.Width / 1.5, clientPixels.Height / 1.5), client);
        Assert.True(sizeChanges >= 1);
        await ui.InvokeAsync(window.Dispose);
    }

    [Fact]
    public async Task DpiChanged_SamePixelSize_StillRaisesClientSizeChangedBecauseDipsChanged()
    {
        await using var ui = await UiThread.StartAsync();
        var window = await ui.InvokeAsync(() => CreateWindow(ui));
        var sizeChanges = 0;
        var (before, rect) = await ui.InvokeAsync(() =>
        {
            window.ClientSizeChanged += (_, _) => sizeChanges++;
            return (window.ClientSizeDip, WindowRect(window.Hwnd));
        });

        var after = await ui.InvokeAsync(() =>
        {
            SendDpiChanged(window.Hwnd, 192, rect); // cùng rect: số pixel không đổi
            return window.ClientSizeDip;
        });

        Assert.Equal(1, sizeChanges);
        Assert.Equal(before.Width / 2, after.Width, 6);
        Assert.Equal(before.Height / 2, after.Height, 6);
        await ui.InvokeAsync(window.Dispose);
    }

    [Fact]
    public async Task GetMinMaxInfo_ReportsTheMinimumSizeScaledByDpi()
    {
        await using var ui = await UiThread.StartAsync();
        var options = new ShellWindowOptions(MinWidthDip: 400, MinHeightDip: 300);
        var window = await ui.InvokeAsync(() => CreateWindow(ui, options));

        var (minTrack, expected) = await ui.InvokeAsync(() =>
        {
            SendDpiChanged(window.Hwnd, 120, WindowRect(window.Hwnd));
            return (SendGetMinMaxInfo(window.Hwnd), ExpectedFrameSize(500, 375, 120)); // 400x300 DIP x 1,25
        });

        Assert.Equal(expected, minTrack);
        await ui.InvokeAsync(window.Dispose);
    }

    [Fact]
    public async Task Activate_Messages_RaiseActivatedAndDeactivatedOncePerChange()
    {
        await using var ui = await UiThread.StartAsync();
        var window = await ui.InvokeAsync(() => CreateWindow(ui));
        var events = new List<string>();

        var states = await ui.InvokeAsync(() =>
        {
            window.Activated += (_, _) => events.Add("activated");
            window.Deactivated += (_, _) => events.Add("deactivated");
            var result = new List<bool>();
            PendingUser32.SendMessage(window.Hwnd, PendingUser32.WmActivate, 1, 0);  // WA_ACTIVE
            result.Add(window.IsActive);
            PendingUser32.SendMessage(window.Hwnd, PendingUser32.WmActivate, 2, 0);  // WA_CLICKACTIVE: vẫn active
            PendingUser32.SendMessage(window.Hwnd, PendingUser32.WmActivate, 0, 0);  // WA_INACTIVE
            result.Add(window.IsActive);
            return result;
        });

        Assert.Equal([true, false], states);
        Assert.Equal(["activated", "deactivated"], events);
        await ui.InvokeAsync(window.Dispose);
    }

    [Fact]
    public async Task MessageHandlers_RunInRegistrationOrder_AndTheFirstThatHandlesWins()
    {
        await using var ui = await UiThread.StartAsync();
        var window = await ui.InvokeAsync(() => CreateWindow(ui));
        var calls = new List<string>();

        var result = await ui.InvokeAsync(() =>
        {
            window.AddMessageHandler(new Handler("first", calls, handles: false, result: 0));
            window.AddMessageHandler(new Handler("second", calls, handles: true, result: 42));
            window.AddMessageHandler(new Handler("third", calls, handles: true, result: 7));
            return PendingUser32.SendMessage(window.Hwnd, TestMessage, 0, 0);
        });

        Assert.Equal(42, result);
        Assert.Equal(["first", "second"], calls);
        await ui.InvokeAsync(window.Dispose);
    }

    [Fact]
    public async Task Handlers_SeeResizeAfterTheWindowUpdatedItsClientSize()
    {
        await using var ui = await UiThread.StartAsync();
        var window = await ui.InvokeAsync(() => CreateWindow(ui));
        var seen = new List<SizeD>();
        var raised = 0;

        await ui.InvokeAsync(() =>
        {
            window.ClientSizeChanged += (_, _) => raised++;
            window.AddMessageHandler(new SizeProbe(window, seen));
            PendingUser32.SetWindowPos(window.Hwnd, 0, 0, 0, 900, 700, PendingUser32.SwpNoMove | PendingUser32.SwpNoZOrder | PendingUser32.SwpNoActivate);
        });

        var client = await ui.InvokeAsync(() => window.ClientSizeDip);
        Assert.Equal(client, seen[^1]);
        Assert.Equal(1, raised);
        await ui.InvokeAsync(window.Dispose);
    }

    [Fact]
    public async Task Invalidate_ManyTimes_CoalescesIntoOneRenderAndReleasesTheFrameClock()
    {
        await using var ui = await UiThread.StartAsync();
        TimerFrameClock? clock = null;
        ShellWindow? window = null;
        var rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await ui.InvokeAsync(() =>
        {
            clock = new TimerFrameClock();
            ui.Loop.AddFrameClock(clock);
            window = new ShellWindow(new ShellWindowOptions(), clock);
        });
        await WaitUntilIdleAsync(ui, clock!);   // khung của lần tạo (WM_SIZE) đã vẽ xong

        var before = await ui.InvokeAsync(() =>
        {
            var count = window!.RenderCount;
            window.Render += (_, _) => rendered.TrySetResult();
            window.Invalidate();
            window.Invalidate();
            window.Invalidate();
            return count;
        });
        await rendered.Task.WaitAsync(UiThread.Bound);
        await WaitUntilIdleAsync(ui, clock!);

        var (after, armed) = await ui.InvokeAsync(() => (window!.RenderCount, clock!.IsArmed));
        Assert.Equal(before + 1, after);
        Assert.False(armed);
        await ui.InvokeAsync(window!.Dispose);
    }

    [Fact]
    public async Task SetTitle_ChangesTheWindowText()
    {
        await using var ui = await UiThread.StartAsync();
        var window = await ui.InvokeAsync(() => CreateWindow(ui));

        var text = await ui.InvokeAsync(() =>
        {
            window.SetTitle("ảnh 1/20 - PhotoReview");
            return WindowText(window.Hwnd);
        });

        Assert.Equal("ảnh 1/20 - PhotoReview", text);
        await ui.InvokeAsync(window.Dispose);
    }

    [Fact]
    public async Task ScreenToClientDip_SubtractsTheClientOriginAndDividesByDpi()
    {
        await using var ui = await UiThread.StartAsync();
        var window = await ui.InvokeAsync(() => CreateWindow(ui));

        var (dip, origin) = await ui.InvokeAsync(() =>
        {
            SendDpiChanged(window.Hwnd, 192, WindowRect(window.Hwnd));
            var o = ClientOrigin(window.Hwnd);
            return (window.ScreenToClientDip(new PointD(o.X + 200, o.Y + 100)), o);
        });

        Assert.Equal(new PointD(100, 50), dip);
        Assert.NotEqual(default, origin);
        await ui.InvokeAsync(window.Dispose);
    }

    [Fact]
    public async Task SetCursor_IsAppliedForClientHitTests()
    {
        await using var ui = await UiThread.StartAsync();
        var window = await ui.InvokeAsync(() => CreateWindow(ui));

        var (client, cursor) = await ui.InvokeAsync(() =>
        {
            window.SetCursor(ShellCursor.SizeAll);
            var inClient = PendingUser32.SendMessage(window.Hwnd, PendingUser32.WmSetCursor, window.Hwnd, PendingUser32.HtClient);
            return (inClient, window.Cursor);
        });

        Assert.Equal(1, client); // đã đặt con trỏ, dừng xử lý mặc định
        Assert.Equal(ShellCursor.SizeAll, cursor);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ui.InvokeAsync(() => window.SetCursor((ShellCursor)9)));
        await ui.InvokeAsync(window.Dispose);
    }

    [Fact]
    public async Task PointerCapture_IsTakenAndReleased()
    {
        await using var ui = await UiThread.StartAsync();
        var window = await ui.InvokeAsync(() => CreateWindow(ui));

        var states = await ui.InvokeAsync(() =>
        {
            window.CapturePointer();
            var captured = window.HasPointerCapture;
            window.ReleasePointer();
            return (captured, window.HasPointerCapture);
        });

        Assert.Equal((true, false), states);
        await ui.InvokeAsync(window.Dispose);
    }

    [Fact]
    public async Task WindowMembers_OffTheUiThread_Throw()
    {
        await using var ui = await UiThread.StartAsync();
        var window = await ui.InvokeAsync(() => CreateWindow(ui));

        Assert.Throws<InvalidOperationException>(window.Invalidate);
        Assert.Throws<InvalidOperationException>(window.Close);
        Assert.Throws<InvalidOperationException>(() => window.SetTitle("x"));
        await ui.InvokeAsync(window.Dispose);
    }

    [Fact]
    public async Task HandlerException_IsRethrownFromTheMessageLoop()
    {
        var ui = await UiThread.StartAsync();
        var hwnd = await ui.InvokeAsync(() =>
        {
            var w = CreateWindow(ui);
            w.AddMessageHandler(new ThrowingHandler());
            return w.Hwnd;
        });

        Assert.True(PendingUser32.PostMessage(hwnd, TestMessage, 0, 0));

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => ui.Exit.WaitAsync(UiThread.Bound));
        Assert.Equal("from WndProc", error.Message);
        await ui.DisposeAsync();
    }

    [Fact]
    public async Task HandlerException_DuringSynchronousClose_IsRethrownToTheCaller()
    {
        await using var ui = await UiThread.StartAsync();
        var window = await ui.InvokeAsync(() =>
        {
            var w = CreateWindow(ui);
            w.Closing += (_, _) => throw new InvalidDataException("closing failed");
            return w;
        });

        await Assert.ThrowsAsync<InvalidDataException>(() => ui.InvokeAsync(window.Close));
        Assert.True(await ui.InvokeAsync(() => window.IsLoaded)); // không bị huỷ nửa chừng
        await ui.InvokeAsync(window.Dispose);
    }

    private static ShellWindow CreateWindow(UiThread ui, ShellWindowOptions? options = null)
    {
        var clock = new TimerFrameClock();
        ui.Loop.AddFrameClock(clock);
        return new ShellWindow(options ?? new ShellWindowOptions(), clock);
    }

    /// <summary>Chờ (có giới hạn) tới khi đồng hồ không còn armed: không còn khung nào đang chờ vẽ.</summary>
    private static async Task WaitUntilIdleAsync(UiThread ui, FrameClockBase clock)
    {
        var deadline = DateTime.UtcNow + UiThread.Bound;
        while (await ui.InvokeAsync(() => clock.IsArmed))
        {
            Assert.True(DateTime.UtcNow < deadline, "frame clock stayed armed");
            await ui.Dispatcher.YieldAsync(UiPriority.Background).AsTask().WaitAsync(UiThread.Bound);
        }
    }

    private static unsafe void SendDpiChanged(nint hwnd, int dpi, PendingRect suggested)
    {
        var rect = suggested;
        PendingUser32.SendMessage(hwnd, PendingUser32.WmDpiChanged, (dpi << 16) | dpi, (nint)(&rect));
        WndProcThunk.RethrowPending(); // ngoại lệ WndProc trong SendMessage đồng bộ tới được test
    }

    private static unsafe PendingPoint SendGetMinMaxInfo(nint hwnd)
    {
        var info = default(PendingMinMaxInfo);
        PendingUser32.SendMessage(hwnd, PendingUser32.WmGetMinMaxInfo, 0, (nint)(&info));
        return info.MinTrackSize;
    }

    private static unsafe PendingPoint ExpectedFrameSize(int clientWidth, int clientHeight, uint dpi)
    {
        var rect = new PendingRect(0, 0, clientWidth, clientHeight);
        Assert.True(PendingUser32.AdjustWindowRectExForDpi(&rect, PendingUser32.WsOverlappedWindow, false, 0, dpi));
        return new PendingPoint { X = rect.Width, Y = rect.Height };
    }

    private static unsafe PendingRect WindowRect(nint hwnd)
    {
        PendingRect rect;
        Assert.True(GetWindowRect(hwnd, &rect));
        return rect;
    }

    private static unsafe PendingPoint ClientOrigin(nint hwnd)
    {
        var point = default(PendingPoint);
        Assert.True(ClientToScreen(hwnd, &point));
        return point;
    }

    private static unsafe string WindowText(nint hwnd)
    {
        var buffer = stackalloc char[256];
        var length = GetWindowText(hwnd, buffer, 256);
        return new string(buffer, 0, length);
    }


    [LibraryImport("user32.dll", EntryPoint = "GetWindowRect", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetWindowRect(nint hwnd, PendingRect* rect);

    [LibraryImport("user32.dll", EntryPoint = "ClientToScreen")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool ClientToScreen(nint hwnd, PendingPoint* point);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW")]
    private static unsafe partial int GetWindowText(nint hwnd, char* buffer, int maxCount);

    private sealed class Handler(string name, List<string> calls, bool handles, nint result) : IWindowMessageHandler
    {
        public bool TryHandle(in WindowMessage message, out nint handled)
        {
            handled = result;
            if (message.Msg != TestMessage)
            {
                return false;
            }

            calls.Add(name);
            return handles;
        }
    }

    private sealed class SizeProbe(ShellWindow window, List<SizeD> seen) : IWindowMessageHandler
    {
        public bool TryHandle(in WindowMessage message, out nint result)
        {
            result = 0;
            if (message.Msg == PendingUser32.WmSize)
            {
                seen.Add(window.ClientSizeDip);
            }

            return false;
        }
    }

    private sealed class ThrowingHandler : IWindowMessageHandler
    {
        public bool TryHandle(in WindowMessage message, out nint result)
        {
            result = 0;
            if (message.Msg == TestMessage)
            {
                throw new InvalidDataException("from WndProc");
            }

            return false;
        }
    }
}

using PhotoReview.Core.Abstractions;
using PhotoReview.Shell.Win32.Hosting;
using PhotoReview.Shell.Interop;
using PhotoReview.Shell.Win32.Hosting.PendingInterop;

namespace PhotoReview.Shell.Tests.Hosting;

/// <summary>
/// WP-14 (C-04 Win32): thứ tự ưu tiên, Background chỉ khi hàng đợi message rỗng, thread affinity (ADR 0005), post từ thread
/// khác, ngoại lệ, dừng. UI thread thật (cửa sổ message-only, không hiện gì) - chạy trên desktop ẩn như mọi test UI.
/// </summary>
[Trait("Category", "UI")]
public sealed class Win32UiDispatcherTests
{
    private const uint TestMessage = WindowMessages.WmApp + 50;

    [Fact]
    public async Task Priorities_ThousandInterleavedPostsFromUiThread_RunSendNormalRenderBackgroundEachInFifoOrder()
    {
        await using var ui = await UiThread.StartAsync();
        var order = new List<(UiPriority Priority, int Index)>();
        var posted = InterleavedPriorities(1000, seed: 7);

        Task? last = null;
        await ui.InvokeAsync(() =>
        {
            for (var i = 0; i < posted.Length; i++)
            {
                var item = (posted[i], i);
                ui.Dispatcher.Post(() => order.Add(item), posted[i]);
            }

            last = ui.Dispatcher.InvokeAsync(() => { }, UiPriority.Background);
        });
        await last!.WaitAsync(UiThread.Bound);

        var expected = order.OrderBy(o => o.Priority).ThenBy(o => o.Index).ToArray();
        Assert.Equal(posted.Length, order.Count);
        Assert.Equal(expected, order);
        Assert.Equal(4, order.Select(o => o.Priority).Distinct().Count());
    }

    [Fact]
    public async Task Priorities_ThousandInterleavedPostsFromPoolThreadWhileUiBusy_RunInPriorityOrderWithOneWakeMessage()
    {
        await using var ui = await UiThread.StartAsync();
        var order = new List<(UiPriority Priority, int Index)>();
        var posted = InterleavedPriorities(1000, seed: 11);
        using var uiBusy = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        ui.Dispatcher.Post(() =>
        {
            uiBusy.Set();
            Assert.True(release.Wait(UiThread.Bound)); // giữ UI thread trong một việc cho tới khi pool post xong
        }, UiPriority.Normal);
        Assert.True(uiBusy.Wait(UiThread.Bound));

        var wakesBefore = ui.Dispatcher.WakeMessagesPosted;
        Task last = Task.CompletedTask;
        await Task.Run(() =>
        {
            for (var i = 0; i < posted.Length; i++)
            {
                var item = (posted[i], i);
                ui.Dispatcher.Post(() => order.Add(item), posted[i]);
            }

            last = ui.Dispatcher.InvokeAsync(() => { }, UiPriority.Background);
        });
        var wakesDuringPosting = ui.Dispatcher.WakeMessagesPosted - wakesBefore;
        release.Set();
        await last.WaitAsync(UiThread.Bound);

        Assert.Equal(order.OrderBy(o => o.Priority).ThenBy(o => o.Index).ToArray(), order);
        // Giao thức cờ: 1001 lần post khi UI bận chỉ đẩy tối đa một WM_APP+1 (không ngập hàng đợi message của thread).
        Assert.InRange(wakesDuringPosting, 0, 1);
    }

    [Fact]
    public async Task Background_RunsOnlyAfterQueuedMessages_WhileNormalRunsBeforeThem()
    {
        await using var ui = await UiThread.StartAsync();
        var order = new List<string>();
        var window = await ui.InvokeAsync(() => CreateRecordingWindow(ui, order));

        Task? background = null;
        await ui.InvokeAsync(() =>
        {
            background = ui.Dispatcher.InvokeAsync(() => order.Add("B"), UiPriority.Background);
            for (var i = 1; i <= 3; i++)
            {
                Assert.True(User32.PostMessage(window.Hwnd, TestMessage, i, 0));
            }

            ui.Dispatcher.Post(() => order.Add("N"), UiPriority.Normal);
        });
        await background!.WaitAsync(UiThread.Bound);

        Assert.Equal(["N", "M1", "M2", "M3", "B"], order);
        await ui.InvokeAsync(window.Dispose);
    }

    [Fact]
    public async Task Normal_PostedWhileHandlingAMessage_RunsBeforeTheNextQueuedMessage()
    {
        await using var ui = await UiThread.StartAsync();
        var order = new List<string>();
        var window = await ui.InvokeAsync(() => CreateRecordingWindow(ui, order, postNormalOnFirst: true));

        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await ui.InvokeAsync(() =>
        {
            Assert.True(User32.PostMessage(window.Hwnd, TestMessage, 1, 0));
            Assert.True(User32.PostMessage(window.Hwnd, TestMessage, 2, 0));
            ui.Dispatcher.Post(() => drained.TrySetResult(), UiPriority.Background);
        });
        await drained.Task.WaitAsync(UiThread.Bound);

        Assert.Equal(["M1", "N-from-M1", "M2"], order);
        await ui.InvokeAsync(window.Dispose);
    }

    [Fact]
    public async Task InvokeAsync_FromPoolThread_RunsOnUiThreadAndCompletes()
    {
        await using var ui = await UiThread.StartAsync();

        var ranOn = await Task.Run(async () =>
        {
            var id = -1;
            await ui.Dispatcher.InvokeAsync(() => id = Environment.CurrentManagedThreadId, UiPriority.Normal);
            return id;
        }).WaitAsync(UiThread.Bound);

        Assert.Equal(ui.ManagedThreadId, ranOn);
    }

    [Fact]
    public async Task Post_FromPoolThread_RunsOnUiThread()
    {
        await using var ui = await UiThread.StartAsync();
        var ranOn = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        await Task.Run(() => ui.Dispatcher.Post(() => ranOn.TrySetResult(Environment.CurrentManagedThreadId)));

        Assert.Equal(ui.ManagedThreadId, await ranOn.Task.WaitAsync(UiThread.Bound));
    }

    [Fact]
    public async Task Await_OnUiThread_ResumesOnTheUiThreadThroughItsSynchronizationContext()
    {
        await using var ui = await UiThread.StartAsync();
        var observed = new List<int>();
        var poolThread = -1;

        await ui.RunAsync(async () =>
        {
            Assert.IsType<Win32UiSynchronizationContext>(SynchronizationContext.Current);
            observed.Add(Environment.CurrentManagedThreadId);
            await Task.Run(() => poolThread = Environment.CurrentManagedThreadId);
            observed.Add(Environment.CurrentManagedThreadId);
            await ui.Dispatcher.YieldAsync(UiPriority.Background);
            observed.Add(Environment.CurrentManagedThreadId);
        });

        Assert.NotEqual(ui.ManagedThreadId, poolThread);
        Assert.Equal([ui.ManagedThreadId, ui.ManagedThreadId, ui.ManagedThreadId], observed);
    }

    [Fact]
    public async Task CheckAccess_IsTrueOnlyOnTheUiThread()
    {
        await using var ui = await UiThread.StartAsync();

        Assert.False(ui.Dispatcher.CheckAccess());
        Assert.True(await ui.InvokeAsync(ui.Dispatcher.CheckAccess));
        Assert.False(await Task.Run(ui.Dispatcher.CheckAccess));
        Assert.Throws<InvalidOperationException>(ui.Dispatcher.RunHighPriorityBatch);
    }

    [Fact]
    public async Task Post_ActionThrows_IsLoggedAndTheLoopKeepsRunning()
    {
        var log = new RecordingLog();
        await using var ui = await UiThread.StartAsync(log);
        var boom = new InvalidOperationException("boom");

        ui.Dispatcher.Post(() => throw boom, UiPriority.Normal);
        ui.Dispatcher.Post(() => throw boom, UiPriority.Background);
        await ui.Dispatcher.InvokeAsync(() => { }, UiPriority.Background).WaitAsync(UiThread.Bound);

        Assert.Equal(2, log.Errors.Count);
        Assert.All(log.Errors, e => Assert.Same(boom, e.Exception));
        Assert.False(ui.Exit.IsCompleted);
        Assert.Equal(ui.ManagedThreadId, await ui.InvokeAsync(() => Environment.CurrentManagedThreadId));
    }

    [Fact]
    public async Task InvokeAsync_ActionThrows_FaultsTheTaskWithoutLogging()
    {
        var log = new RecordingLog();
        await using var ui = await UiThread.StartAsync(log);

        var task = ui.Dispatcher.InvokeAsync(() => throw new FormatException("bad"), UiPriority.Render);

        await Assert.ThrowsAsync<FormatException>(() => task.WaitAsync(UiThread.Bound));
        Assert.Empty(log.Errors);
    }

    [Fact]
    public async Task YieldAsync_Background_ResumesAfterNormalWorkQueuedBeforeIt()
    {
        await using var ui = await UiThread.StartAsync();
        var order = new List<string>();

        await ui.RunAsync(async () =>
        {
            ui.Dispatcher.Post(() => order.Add("normal"), UiPriority.Normal);
            ui.Dispatcher.Post(() => order.Add("render"), UiPriority.Render);
            await ui.Dispatcher.YieldAsync();
            order.Add("after-yield");
        });

        Assert.Equal(["normal", "render", "after-yield"], order);
    }

    [Fact]
    public async Task YieldAsync_Canceled_ThrowsBeforeOrWhileQueued()
    {
        await using var ui = await UiThread.StartAsync();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await ui.Dispatcher.YieldAsync(UiPriority.Background, canceled.Token));

        using var later = new CancellationTokenSource();
        ValueTask pending = default;
        await ui.InvokeAsync(() =>
        {
            pending = ui.Dispatcher.YieldAsync(UiPriority.Background, later.Token);
            later.Cancel(); // còn trong hàng đợi (UI thread đang bận việc này)
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending.AsTask().WaitAsync(UiThread.Bound));
    }

    [Fact]
    public async Task Quit_EndsTheLoopWithItsExitCode()
    {
        var ui = await UiThread.StartAsync();

        await ui.InvokeAsync(() => MessageLoop.Quit(3));

        Assert.Equal(3, await ui.Exit.WaitAsync(UiThread.Bound));
        await ui.DisposeAsync();
        Assert.True(ui.Dispatcher.IsShutDown);
    }

    [Fact]
    public async Task Dispose_CancelsPendingInvokesAndDropsPosts_AndLaterCallsDoNotHang()
    {
        var ran = 0;
        Win32UiDispatcher? dispatcher = null;
        Task? pendingInvoke = null;
        ValueTask pendingYield = default;
        var thread = new Thread(() =>
        {
            dispatcher = new Win32UiDispatcher();
            dispatcher.Post(() => ran++, UiPriority.Normal);
            pendingInvoke = dispatcher.InvokeAsync(() => ran++, UiPriority.Background);
            pendingYield = dispatcher.YieldAsync(UiPriority.Render);
            dispatcher.Dispose(); // không có vòng lặp nào chạy: mọi việc còn chờ
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(UiThread.Bound));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pendingInvoke!.WaitAsync(UiThread.Bound));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pendingYield.AsTask().WaitAsync(UiThread.Bound));
        Assert.True(dispatcher!.IsShutDown);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dispatcher.InvokeAsync(() => ran++, UiPriority.Normal).WaitAsync(UiThread.Bound));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await dispatcher.YieldAsync(UiPriority.Normal));
        dispatcher.Post(() => ran++, UiPriority.Normal);
        Assert.Equal(0, ran);
        Assert.False(dispatcher.HasHighPriorityWork);
        Assert.False(dispatcher.HasBackgroundWork);
    }

    [Fact]
    public async Task SynchronizationContextSend_FromPoolThread_RunsOnUiThreadAndRethrows()
    {
        await using var ui = await UiThread.StartAsync();
        var context = await ui.InvokeAsync(() => SynchronizationContext.Current!);

        var ranOn = await Task.Run(() =>
        {
            var id = -1;
            context.Send(_ => id = Environment.CurrentManagedThreadId, null);
            return id;
        }).WaitAsync(UiThread.Bound);
        var thrown = await Task.Run(() => Assert.Throws<ArithmeticException>(() => context.Send(_ => throw new ArithmeticException(), null)))
            .WaitAsync(UiThread.Bound);

        Assert.Equal(ui.ManagedThreadId, ranOn);
        Assert.NotNull(thrown);
        Assert.Same(context, context.CreateCopy());
    }

    [Fact]
    public async Task Constructor_SecondDispatcherOnTheSameThread_Throws()
    {
        await using var ui = await UiThread.StartAsync();

        var error = await ui.InvokeAsync(() => Record.Exception(() => new Win32UiDispatcher()));

        Assert.IsType<InvalidOperationException>(error);
    }

    [Fact]
    public async Task WakeMessage_DispatchedByAForeignModalLoop_StillRunsQueuedWork()
    {
        await using var ui = await UiThread.StartAsync();
        var ranInsideForeignLoop = false;

        await ui.InvokeAsync(() =>
        {
            var ran = false;
            ui.Dispatcher.Post(() => ran = true, UiPriority.Normal);
            ForeignModalLoop(() => ran);
            ranInsideForeignLoop = ran;
        });

        Assert.True(ranInsideForeignLoop);
    }

    [Fact]
    public async Task Post_UnknownPriority_Throws()
    {
        await using var ui = await UiThread.StartAsync();

        Assert.Throws<ArgumentOutOfRangeException>(() => ui.Dispatcher.Post(() => { }, (UiPriority)4));
        Assert.Throws<ArgumentNullException>(() => ui.Dispatcher.Post(null!, UiPriority.Normal));
    }

    /// <summary>Vòng lặp "modal" không phải MessageLoop (như kéo resize / TrackPopupMenu): chỉ Peek + Dispatch, có giới hạn.</summary>
    private static unsafe void ForeignModalLoop(Func<bool> until)
    {
        var deadline = DateTime.UtcNow + UiThread.Bound;
        while (!until() && DateTime.UtcNow < deadline)
        {
            Msg message;
            if (User32.PeekMessage(&message, 0, 0, 0, WindowMessages.PmRemove))
            {
                User32.TranslateMessage(&message);
                User32.DispatchMessage(&message);
            }
            else
            {
                _ = User32.MsgWaitForMultipleObjectsEx(0, null, 100, WindowMessages.QsAllInput, WindowMessages.MwmoInputAvailable);
            }
        }
    }

    private static UiPriority[] InterleavedPriorities(int count, int seed)
    {
        var random = new Random(seed);
        return Enumerable.Range(0, count).Select(_ => (UiPriority)random.Next(4)).ToArray();
    }

    private static ShellWindow CreateRecordingWindow(UiThread ui, List<string> order, bool postNormalOnFirst = false)
    {
        var clock = new TimerFrameClock();
        ui.Loop.AddFrameClock(clock);
        var window = new ShellWindow(new ShellWindowOptions(), clock);
        window.AddMessageHandler(new RecordingHandler(order, postNormalOnFirst ? ui.Dispatcher : null));
        return window;
    }

    private sealed class RecordingHandler(List<string> order, Win32UiDispatcher? postOnFirst) : IWindowMessageHandler
    {
        public bool TryHandle(in WindowMessage message, out nint result)
        {
            result = 0;
            if (message.Msg != TestMessage)
            {
                return false;
            }

            order.Add("M" + message.WParam.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (postOnFirst is not null && message.WParam == 1)
            {
                postOnFirst.Post(() => order.Add("N-from-M1"), UiPriority.Normal);
            }

            return true;
        }
    }
}

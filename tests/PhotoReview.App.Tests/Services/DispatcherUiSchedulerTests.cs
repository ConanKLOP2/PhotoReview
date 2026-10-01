using System.Windows.Threading;
using PhotoReview.App.Services;
using PhotoReview.TestSupport.Windows;

namespace PhotoReview.App.Tests.Services;

/// <summary>
/// RV-T31: <see cref="DispatcherUiScheduler"/> on a dedicated dispatcher thread: marshalling, a cancelled yield and use
/// after the dispatcher shut down (the app closing while a background continuation still posts).
/// </summary>
[Trait("Category", "UI")]
[Collection("GlobalState")]
public sealed class DispatcherUiSchedulerTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task InvokeAsync_RunsTheActionOnTheDispatcherThread()
    {
        using var ui = new DispatcherThread();
        var scheduler = new DispatcherUiScheduler(ui.Dispatcher);
        var ranOn = -1;

        await scheduler.InvokeAsync(() => ranOn = Environment.CurrentManagedThreadId).WaitAsync(Bound);

        Assert.Equal(ui.ManagedThreadId, ranOn);
    }

    [Fact]
    public async Task Post_RunsTheActionOnTheDispatcherThread()
    {
        using var ui = new DispatcherThread();
        var scheduler = new DispatcherUiScheduler(ui.Dispatcher);
        var ran = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        scheduler.Post(() => ran.SetResult(Environment.CurrentManagedThreadId));

        Assert.Equal(ui.ManagedThreadId, await ran.Task.WaitAsync(Bound));
    }

    [Fact]
    public async Task YieldAsync_AlreadyCancelledToken_ThrowsOperationCanceledWithoutYielding()
    {
        using var ui = new DispatcherThread();
        var scheduler = new DispatcherUiScheduler(ui.Dispatcher);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Called from a thread that is not the dispatcher thread: reaching Dispatcher.Yield there would throw something else.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await scheduler.YieldAsync(cts.Token));
    }

    [Fact]
    public async Task YieldAsync_OnTheDispatcherThread_ResumesOnTheDispatcherThread()
    {
        using var ui = new DispatcherThread();
        var scheduler = new DispatcherUiScheduler(ui.Dispatcher);

        var resumedOn = await ui.Dispatcher.InvokeAsync(async () =>
        {
            await scheduler.YieldAsync();
            return Environment.CurrentManagedThreadId;
        }).Task.Unwrap().WaitAsync(Bound);

        Assert.Equal(ui.ManagedThreadId, resumedOn);
    }

    [Fact]
    public async Task InvokeAsync_AfterTheDispatcherShutDown_NeverRunsTheActionAndReturnsACanceledTask()
    {
        var ui = new DispatcherThread();
        var scheduler = new DispatcherUiScheduler(ui.Dispatcher);
        ui.Dispatcher.InvokeShutdown();
        ui.Join();
        var ran = false;

        var task = scheduler.InvokeAsync(() => ran = true);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(Bound));
        Assert.True(task.IsCanceled);
        Assert.False(ran);
    }

    [Fact]
    public void Post_AfterTheDispatcherShutDown_DoesNotThrowAndNeverRunsTheAction()
    {
        var ui = new DispatcherThread();
        var scheduler = new DispatcherUiScheduler(ui.Dispatcher);
        ui.Dispatcher.InvokeShutdown();
        ui.Join();
        var ran = false;

        scheduler.Post(() => ran = true);

        Assert.False(ran);
    }

    [Fact]
    public void NullActions_AreRejected()
    {
        using var ui = new DispatcherThread();
        var scheduler = new DispatcherUiScheduler(ui.Dispatcher);

        Assert.Throws<ArgumentNullException>(() => scheduler.Post(null!));
        Assert.Throws<ArgumentNullException>(() => { _ = scheduler.InvokeAsync(null!); });
    }

    private sealed class DispatcherThread : IDisposable
    {
        private readonly Thread _thread;

        public DispatcherThread()
        {
            using var ready = new ManualResetEventSlim();
            Dispatcher? dispatcher = null;
            _thread = new Thread(() =>
            {
                dispatcher = Dispatcher.CurrentDispatcher;
                using var guard = Win32DialogGuard.InstallOnCurrentThread();
                ready.Set();
                Dispatcher.Run();
            })
            { IsBackground = true };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            ready.Wait();
            Dispatcher = dispatcher!;
            ManagedThreadId = _thread.ManagedThreadId;
        }

        public Dispatcher Dispatcher { get; }
        public int ManagedThreadId { get; }

        public void Join() => Assert.True(_thread.Join(Bound), "The dispatcher thread did not exit after shutdown.");

        public void Dispose()
        {
            if (!Dispatcher.HasShutdownStarted) Dispatcher.InvokeShutdown();
            _thread.Join(Bound);
        }
    }
}

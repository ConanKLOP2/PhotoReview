using System.Windows.Threading;
using PhotoReview.App.Services;
using PhotoReview.Core.Abstractions;
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

    // ---- C-04: IUiDispatcher (priorities map by name onto DispatcherPriority) ----

    [Theory]
    [InlineData(UiPriority.Send, DispatcherPriority.Send)]
    [InlineData(UiPriority.Normal, DispatcherPriority.Normal)]
    [InlineData(UiPriority.Render, DispatcherPriority.Render)]
    [InlineData(UiPriority.Background, DispatcherPriority.Background)]
    [InlineData((UiPriority)99, DispatcherPriority.Normal)]
    public void ToDispatcherPriority_MapsByName(UiPriority priority, DispatcherPriority expected)
    {
        Assert.Equal(expected, DispatcherUiScheduler.ToDispatcherPriority(priority));
    }

    [Fact]
    public async Task CheckAccess_IsTrueOnTheDispatcherThreadAndFalseElsewhere()
    {
        using var ui = new DispatcherThread();
        var scheduler = new DispatcherUiScheduler(ui.Dispatcher);

        var onUi = await ui.Dispatcher.InvokeAsync(scheduler.CheckAccess).Task.WaitAsync(Bound);

        Assert.IsAssignableFrom<IUiDispatcher>(scheduler); // C-04: shared code depends on the interface only
        Assert.True(onUi);
        Assert.False(scheduler.CheckAccess());
    }

    [Fact]
    public async Task Post_WithPriority_RunsInPriorityOrderNotInPostOrder()
    {
        using var ui = new DispatcherThread();
        var scheduler = new DispatcherUiScheduler(ui.Dispatcher);
        var order = new List<string>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Queued from inside the dispatcher thread so every item is pending before any of them can run.
        await scheduler.InvokeAsync(() =>
        {
            scheduler.Post(() => { order.Add("background"); done.SetResult(); }, UiPriority.Background);
            scheduler.Post(() => order.Add("render"), UiPriority.Render);
            scheduler.Post(() => order.Add("normal"), UiPriority.Normal);
            scheduler.Post(() => order.Add("send"), UiPriority.Send);
        }).WaitAsync(Bound);
        await done.Task.WaitAsync(Bound);

        Assert.Equal(["send", "normal", "render", "background"], order);
    }

    [Fact]
    public async Task InvokeAsync_WithPriority_RunsInPriorityOrderAndCompletesItsTask()
    {
        using var ui = new DispatcherThread();
        var scheduler = new DispatcherUiScheduler(ui.Dispatcher);
        var order = new List<string>();
        Task? background = null, normal = null;

        await scheduler.InvokeAsync(() =>
        {
            background = scheduler.InvokeAsync(() => order.Add("background"), UiPriority.Background);
            normal = scheduler.InvokeAsync(() => order.Add("normal"), UiPriority.Normal);
        }).WaitAsync(Bound);
        await Task.WhenAll(background!, normal!).WaitAsync(Bound);

        Assert.Equal(["normal", "background"], order);
    }

    [Fact]
    public async Task YieldAsync_WithPriority_ResumesAtThatPriority()
    {
        using var ui = new DispatcherThread();
        var scheduler = new DispatcherUiScheduler(ui.Dispatcher);
        var order = new List<string>();

        await ui.Dispatcher.InvokeAsync(async () =>
        {
            var bg = scheduler.InvokeAsync(() => order.Add("background"), UiPriority.Background);
            var normal = scheduler.InvokeAsync(() => order.Add("normal"), UiPriority.Normal);
            await scheduler.YieldAsync(UiPriority.Render); // above Background, below Normal
            order.Add("resumed");
            await Task.WhenAll(bg, normal);
        }).Task.Unwrap().WaitAsync(Bound);

        Assert.Equal(["normal", "resumed", "background"], order);
    }

    [Fact]
    public async Task YieldAsync_WithPriority_AlreadyCancelledToken_Throws()
    {
        using var ui = new DispatcherThread();
        var scheduler = new DispatcherUiScheduler(ui.Dispatcher);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await scheduler.YieldAsync(UiPriority.Render, cts.Token));
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

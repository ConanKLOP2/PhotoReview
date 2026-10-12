using PhotoReview.App.Composition;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Shell.Win32.Startup;
using PhotoReview.Shell.Win32.Viewing;

namespace PhotoReview.Shell.Tests.Viewing;

/// <summary>WP-21 (C-16): sink Win32 - thứ tự view model -&gt; bề mặt, mốc theo hub, ADR 0005 (marshal + đếm cross-thread).</summary>
[Trait("Category", "HotPath")]
public sealed class Win32PresentationSinkTests
{
    private static MainViewModelSinkCallbacks Callbacks(List<string> order) => new(
        SetCurrentImage: (_, fileChange) => order.Add("viewmodel:" + fileChange),
        SetStatusText: s => order.Add("status:" + s),
        ApplyInitialViewMode: () => order.Add("initialView"),
        OnPresented: p => order.Add("presented:" + p));

    [Fact]
    public void Sink_UpdatesTheViewModelBeforeTheSurfaceSeesTheNewImage()
    {
        var order = new List<string>();
        var hub = new ShellPresentationHub();
        hub.ImageChanged += (_, fileChange) => order.Add("surface:" + fileChange);
        var sink = new Win32PresentationSink(hub, Callbacks(order));

        sink.SetCurrentImage(new object(), isFileChange: true);
        sink.SetStatusText("s");
        sink.ApplyInitialViewMode();
        sink.OnPresented("p");

        Assert.Equal(["viewmodel:True", "surface:True", "status:s", "initialView", "presented:p"], order);
    }

    [Fact]
    public void Factory_BuildsASinkOverTheSharedHub()
    {
        var hub = new ShellPresentationHub();
        var factory = new Win32PresentationSinkFactory(hub);
        var sink = factory.Create(new MainViewModelSinkCallbacks((_, _) => { }, _ => { }, () => { }, _ => { }));

        sink.SetCurrentImage("img", isFileChange: false);

        Assert.IsType<Win32PresentationSink>(sink);
        Assert.Equal("img", hub.CurrentImage);
    }

    [Fact]
    public void CrossThreadUpdate_IsMarshalledToTheUiDispatcherAndCounted()
    {
        var hub = new ShellPresentationHub();
        var metrics = new ReviewMetrics();
        var dispatcher = new RecordingUiDispatcher { OnUiThread = false };
        var order = new List<string>();
        var sink = new Win32PresentationSink(hub, Callbacks(order), dispatcher, metrics);

        sink.SetCurrentImage("img", isFileChange: false);
        sink.SetStatusText("x");

        Assert.Equal(2, dispatcher.PostCount);         // mỗi cập nhật qua dispatcher (không chặn thread gọi), theo thứ tự
        Assert.Equal(["viewmodel:False", "status:x"], order);
        Assert.Equal(2, metrics.Snapshot().CrossThreadPresentCount);
        Assert.Equal("img", hub.CurrentImage);
    }

    [Fact]
    public void UiThreadUpdate_DoesNotTouchTheDispatcherOrTheCounter()
    {
        var metrics = new ReviewMetrics();
        var dispatcher = new RecordingUiDispatcher { OnUiThread = true };
        var sink = new Win32PresentationSink(new ShellPresentationHub(), Callbacks([]), dispatcher, metrics);

        sink.SetCurrentImage("img", isFileChange: false);
        sink.TracePresented(1, "preview", 5);

        Assert.Equal(0, dispatcher.PostCount);
        Assert.Equal(0, metrics.Snapshot().CrossThreadPresentCount);
    }

    [Fact(DisplayName = "STALE sink: an update from the UI thread never overtakes an earlier cross-thread update that is still queued")]
    public void UiThreadUpdate_WhileAnEarlierCrossThreadUpdateIsQueued_RunsAfterIt()
    {
        var hub = new ShellPresentationHub();
        var dispatcher = new QueueingUiDispatcher { OnUiThread = false };
        var order = new List<string>();
        var sink = new Win32PresentationSink(hub, Callbacks(order), dispatcher);

        sink.SetCurrentImage("old", isFileChange: true);   // a worker thread: queued, not run yet
        dispatcher.OnUiThread = true;
        sink.SetCurrentImage("new", isFileChange: true);   // then the UI thread, before the queue ran
        sink.SetStatusText("status of new");
        dispatcher.RunAll();

        Assert.Equal(["viewmodel:True", "viewmodel:True", "status:status of new"], order);
        Assert.Equal("new", hub.CurrentImage);              // the LAST update wins on the surface
    }

    [Fact(DisplayName = "STALE sink: once the cross-thread queue has drained, UI-thread updates run inline again")]
    public void UiThreadUpdate_AfterTheQueueDrained_RunsInline()
    {
        var dispatcher = new QueueingUiDispatcher { OnUiThread = false };
        var order = new List<string>();
        var sink = new Win32PresentationSink(new ShellPresentationHub(), Callbacks(order), dispatcher);
        sink.SetStatusText("queued");
        dispatcher.RunAll();
        dispatcher.OnUiThread = true;

        sink.SetStatusText("inline");

        Assert.Equal(["status:queued", "status:inline"], order);
        Assert.Equal(1, dispatcher.PostCount);
    }

    /// <summary>A dispatcher that queues posts (FIFO) and can report being on / off the UI thread per call.</summary>
    private sealed class QueueingUiDispatcher : IUiDispatcher
    {
        private readonly Queue<Action> _queue = new();

        public bool OnUiThread { get; set; }

        public int PostCount { get; private set; }

        public bool CheckAccess() => OnUiThread;

        public void Post(Action action) => Post(action, UiPriority.Normal);

        public void Post(Action action, UiPriority priority)
        {
            PostCount++;
            _queue.Enqueue(action);
        }

        public Task InvokeAsync(Action action) => InvokeAsync(action, UiPriority.Normal);

        public Task InvokeAsync(Action action, UiPriority priority)
        {
            Post(action, priority);
            return Task.CompletedTask;
        }

        public ValueTask YieldAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask YieldAsync(UiPriority priority, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public void RunAll()
        {
            while (_queue.TryDequeue(out var action)) action();
        }
    }
    private sealed class RecordingUiDispatcher : IUiDispatcher
    {
        public bool OnUiThread { get; init; }

        public int PostCount { get; private set; }

        public bool CheckAccess() => OnUiThread;

        public void Post(Action action) => Post(action, UiPriority.Normal);

        public void Post(Action action, UiPriority priority)
        {
            PostCount++;
            action();
        }

        public Task InvokeAsync(Action action) => InvokeAsync(action, UiPriority.Normal);

        public Task InvokeAsync(Action action, UiPriority priority)
        {
            action();
            return Task.CompletedTask;
        }

        public ValueTask YieldAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask YieldAsync(UiPriority priority, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}

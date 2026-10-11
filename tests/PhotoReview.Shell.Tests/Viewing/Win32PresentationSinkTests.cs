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

        Assert.Equal(2, dispatcher.InvokeCount);       // mỗi cập nhật qua dispatcher
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

        Assert.Equal(0, dispatcher.InvokeCount);
        Assert.Equal(0, metrics.Snapshot().CrossThreadPresentCount);
    }

    private sealed class RecordingUiDispatcher : IUiDispatcher
    {
        public bool OnUiThread { get; init; }

        public int InvokeCount { get; private set; }

        public bool CheckAccess() => OnUiThread;

        public void Post(Action action) => action();

        public void Post(Action action, UiPriority priority) => action();

        public Task InvokeAsync(Action action) => InvokeAsync(action, UiPriority.Normal);

        public Task InvokeAsync(Action action, UiPriority priority)
        {
            InvokeCount++;
            action();
            return Task.CompletedTask;
        }

        public ValueTask YieldAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask YieldAsync(UiPriority priority, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Windows.Threading;
using PhotoReview.App.Services;

namespace PhotoReview.App.Tests.Services;

/// <summary>
/// perf(render-metric): WpfPresentationSink turns the render ticks after a present into the Rendered / Presented and
/// RenderedFrame perf events, and only subscribes to the (costly) tick source while a perf listener is attached.
/// SetCurrentImage forwards the image and the file-change flag.
/// </summary>
[Trait("Category", "UI")]
[Collection("GlobalState")] // attaches a listener to the process-wide PhotoReview-Perf EventSource
public sealed class WpfPresentationSinkTraceTests
{
    private const int RenderedId = 15;
    private const int PresentedId = 16;
    private const int RenderedFrameId = 25;

    // A nav value no production path uses, so concurrent unrelated events cannot satisfy an expectation.
    private const long Token = 7_700_123;

    private sealed record Captured(int Id, object?[] Payload);

    private sealed class CaptureListener : EventListener
    {
        public readonly ConcurrentQueue<Captured> Events = new();

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == "PhotoReview-Perf") EnableEvents(eventSource, EventLevel.Informational);
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData) =>
            Events.Enqueue(new Captured(eventData.EventId, eventData.Payload?.ToArray() ?? []));
    }

    private sealed class FakeTicks
    {
        public readonly List<EventHandler> Subscribed = [];
        public int SubscribeCalls;
        public int UnsubscribeCalls;

        public void Subscribe(EventHandler handler)
        {
            SubscribeCalls++;
            Subscribed.Add(handler);
        }

        public void Unsubscribe(EventHandler handler)
        {
            UnsubscribeCalls++;
            Subscribed.Remove(handler);
        }

        public void Tick() => Subscribed.ToList().ForEach(h => h(this, EventArgs.Empty));
    }

    private static WpfPresentationSink NewSink(FakeTicks ticks) =>
        new(dispatcher: Dispatcher.CurrentDispatcher)
        {
            SubscribeRendering = ticks.Subscribe,
            UnsubscribeRendering = ticks.Unsubscribe,
        };

    [Fact]
    public void TracePresented_WithAListener_ReportsRenderedAndPresentedOnTheFirstTickThenRenderedFrameOnTheSecond()
    {
        StaUi.Run(() =>
        {
            using var listener = new CaptureListener();
            var ticks = new FakeTicks();
            var sink = NewSink(ticks);

            sink.TracePresented(Token, "Preview", Stopwatch.GetTimestamp());

            Assert.Equal(1, ticks.SubscribeCalls);
            Assert.DoesNotContain(listener.Events, e => e.Id is RenderedId or PresentedId or RenderedFrameId && (long)e.Payload[0]! == Token);

            ticks.Tick();

            var rendered = Assert.Single(listener.Events, e => e.Id == RenderedId && (long)e.Payload[0]! == Token);
            Assert.True((double)rendered.Payload[1]! >= 0);
            var presented = Assert.Single(listener.Events, e => e.Id == PresentedId && (long)e.Payload[0]! == Token);
            Assert.Equal("Preview", presented.Payload[1]);
            Assert.DoesNotContain(listener.Events, e => e.Id == RenderedFrameId && (long)e.Payload[0]! == Token);
            Assert.Equal(0, ticks.UnsubscribeCalls); // still waiting for the frame that contains the image

            ticks.Tick();

            var frame = Assert.Single(listener.Events, e => e.Id == RenderedFrameId && (long)e.Payload[0]! == Token);
            Assert.True((double)frame.Payload[1]! >= 0);
            Assert.Equal(1, ticks.UnsubscribeCalls);
            Assert.Empty(ticks.Subscribed);
        });
    }

    [Fact]
    public void TracePresented_SecondPresentBeforeAnyTick_ReplacesThePendingTrace()
    {
        StaUi.Run(() =>
        {
            using var listener = new CaptureListener();
            var ticks = new FakeTicks();
            var sink = NewSink(ticks);

            sink.TracePresented(Token, "Preview", Stopwatch.GetTimestamp());
            sink.TracePresented(Token + 1, "Full", Stopwatch.GetTimestamp());

            Assert.Equal(2, ticks.SubscribeCalls);
            Assert.Equal(1, ticks.UnsubscribeCalls);
            Assert.Single(ticks.Subscribed); // one pending handler, however many presents
            ticks.Tick();

            Assert.DoesNotContain(listener.Events, e => e.Id == PresentedId && (long)e.Payload[0]! == Token);
            Assert.Single(listener.Events, e => e.Id == PresentedId && (long)e.Payload[0]! == Token + 1 && (string)e.Payload[1]! == "Full");
        });
    }

    [Fact]
    public void TracePresented_WithoutAListener_NeverSubscribesToTheRenderTicks()
    {
        StaUi.Run(() =>
        {
            var ticks = new FakeTicks();
            var sink = NewSink(ticks);

            sink.TracePresented(Token, "Preview", Stopwatch.GetTimestamp());

            Assert.Equal(0, ticks.SubscribeCalls);
        });
    }

    [Fact]
    public void SetCurrentImage_ForwardsTheImageAndTheFileChangeFlag_DefaultingToFalse()
    {
        StaUi.Run(() =>
        {
            var seen = new List<(object? Image, bool IsFileChange)>();
            var sink = new WpfPresentationSink(onSetCurrentImage: (image, change) => seen.Add((image, change)), dispatcher: Dispatcher.CurrentDispatcher);
            var picture = new object();

            sink.SetCurrentImage(picture);
            sink.SetCurrentImage(null, isFileChange: true);

            Assert.Equal([(picture, false), (null, true)], seen);
        });
    }
}

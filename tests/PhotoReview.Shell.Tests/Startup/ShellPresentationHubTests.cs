using System.Diagnostics;
using System.Diagnostics.Tracing;
using PhotoReview.App.Composition;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Shell.Win32.Startup;

namespace PhotoReview.Shell.Tests.Startup;

/// <summary>
/// WP-20 (C-18): mốc <c>Rendered/Presented/RenderedFrame</c> của shell = Present của khung có ảnh đã gán; và sink đẩy ảnh cho
/// bề mặt SAU khi view model đã cập nhật ViewerState.
/// </summary>
[Collection("PerfEventSource")]
[Trait("Category", "HotPath")]
public sealed class ShellPresentationHubTests
{
    private sealed class Capture : EventListener
    {
        public List<(string Name, long Nav)> Events { get; } = [];

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == "PhotoReview-Perf")
            {
                EnableEvents(eventSource, EventLevel.Informational);
            }
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (eventData.EventName is "Rendered" or "Presented" or "RenderedFrame" && eventData.Payload is { Count: > 0 } payload)
            {
                lock (Events)
                {
                    Events.Add((eventData.EventName, Convert.ToInt64(payload[0], System.Globalization.CultureInfo.InvariantCulture)));
                }
            }
        }

        public List<string> NamesFor(long token)
        {
            lock (Events)
            {
                return [.. Events.Where(e => e.Nav == token).Select(e => e.Name)];
            }
        }
    }

    private static long NewToken() => 9_000_000 + Random.Shared.NextInt64(1, 1_000_000);

    [Fact]
    public void Trace_AddedBeforeTheFrame_IsEmittedWhenAFrameWithTheImageIsPresented()
    {
        using var capture = new Capture();
        var hub = new ShellPresentationHub();
        var token = NewToken();
        hub.SetCurrentImage(new object(), isFileChange: false);

        hub.AddTrace(token, "preview", Stopwatch.GetTimestamp());
        Assert.Empty(capture.NamesFor(token));

        hub.NoteFramePresented(containedImage: false); // khung chỉ có canvas không tính
        Assert.Empty(capture.NamesFor(token));

        hub.NoteFramePresented(containedImage: true);
        Assert.Equal(["Rendered", "Presented", "RenderedFrame"], capture.NamesFor(token));

        hub.NoteFramePresented(containedImage: true); // không phát lại
        Assert.Equal(3, capture.NamesFor(token).Count);
    }

    [Fact]
    public void Trace_AddedAfterTheImageWasAlreadyPresented_IsEmittedImmediately()
    {
        // Ảnh đầu được vẽ ngay trong lượt gán, TRƯỚC khi presenter kịp đăng mốc; màn hình tĩnh nên không có khung sau.
        using var capture = new Capture();
        var hub = new ShellPresentationHub();
        var token = NewToken();
        hub.SetCurrentImage(new object(), isFileChange: false);
        hub.NoteFramePresented(containedImage: true);

        hub.AddTrace(token, "preview", Stopwatch.GetTimestamp());

        Assert.Equal(["Rendered", "Presented", "RenderedFrame"], capture.NamesFor(token));
    }

    [Fact]
    public void NewImage_ResetsThePresentedFlag_SoItsTraceWaitsForItsOwnFrame()
    {
        using var capture = new Capture();
        var hub = new ShellPresentationHub();
        var token = NewToken();
        hub.SetCurrentImage(new object(), isFileChange: false);
        hub.NoteFramePresented(containedImage: true);

        hub.SetCurrentImage(new object(), isFileChange: true);
        hub.AddTrace(token, "preview", Stopwatch.GetTimestamp());

        Assert.Empty(capture.NamesFor(token));
        hub.NoteFramePresented(containedImage: true);
        Assert.Equal(3, capture.NamesFor(token).Count);
    }

    [Fact]
    public void Win32Sink_TracePresented_ReachesTheHubAndIsEmittedWithTheFrameThatHasTheImage()
    {
        using var capture = new Capture();
        var hub = new ShellPresentationHub();
        var sink = new PhotoReview.Shell.Win32.Viewing.Win32PresentationSink(hub,
            new MainViewModelSinkCallbacks((_, _) => { }, _ => { }, () => { }, _ => { }));
        var token = NewToken();
        sink.SetCurrentImage(new object(), isFileChange: false);

        sink.TracePresented(token, "preview", Stopwatch.GetTimestamp());
        Assert.Empty(capture.NamesFor(token));            // chưa có khung nào chứa ảnh

        hub.NoteFramePresented(containedImage: true);
        Assert.Equal(["Rendered", "Presented", "RenderedFrame"], capture.NamesFor(token));
    }

    [Fact]
    public void ImageChanged_IsRaisedWithTheNewImageAndTheFileChangeFlag()
    {
        var hub = new ShellPresentationHub();
        var seen = new List<(object? Image, bool IsFileChange)>();
        hub.ImageChanged += (image, isFileChange) => seen.Add((image, isFileChange));
        var image = new object();

        hub.SetCurrentImage(image, isFileChange: true);

        Assert.Same(image, hub.CurrentImage);
        Assert.Equal(1, hub.ImageChangeCount);
        Assert.Equal([(image, true)], seen);
    }
}

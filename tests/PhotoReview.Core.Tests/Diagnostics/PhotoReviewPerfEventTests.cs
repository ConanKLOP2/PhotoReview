using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using PhotoReview.Core.Diagnostics;

namespace PhotoReview.Core.Tests.Diagnostics;

/// <summary>
/// Every <see cref="PhotoReviewPerf"/> event method must reach an attached listener with its fixed wire id, its name and its
/// payload in declaration order (the CSV listener and external tools such as dotnet-trace depend on that contract).
/// </summary>
[Collection("GlobalState")] // attaches a listener to the process-wide PhotoReviewPerf EventSource
public sealed class PhotoReviewPerfEventTests : IDisposable
{
    private sealed record Captured(int Id, string? Name, object?[] Payload);

    private sealed class CaptureListener : EventListener
    {
        public readonly ConcurrentQueue<Captured> Events = new();

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == "PhotoReview-Perf") EnableEvents(eventSource, EventLevel.Informational);
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData) =>
            Events.Enqueue(new Captured(eventData.EventId, eventData.EventName, eventData.Payload?.ToArray() ?? []));
    }

    private readonly CaptureListener _listener = new();

    public void Dispose() => _listener.Dispose();

    private bool Delivered(int id, string name, params object[] payload)
    {
        // Delivery to an in-process listener is synchronous; the short wait only absorbs scheduling hiccups on a loaded machine.
        return SpinWait.SpinUntil(
            () => _listener.Events.Any(e => e.Id == id && e.Name == name && e.Payload.SequenceEqual(payload)),
            TimeSpan.FromSeconds(2));
    }

    private string Diagnosis() =>
        $"enabled={PhotoReviewPerf.Log.IsEnabled()}; captured: " + string.Join(" | ", _listener.Events.Select(e => $"{e.Id}:{e.Name}[{string.Join(',', e.Payload)}]"));

    // 7_700_001 is a nav value no production code path uses, so concurrent unrelated events cannot satisfy an expectation.
    private const long N = 7_700_001;
    private static PhotoReviewPerf Log => PhotoReviewPerf.Log;

    [Fact(DisplayName = "Input, show, stat, lookup and thumbnail events carry their id, name and payload")]
    public void InputAndLookupEvents()
    {
        Log.KeyInput(N, "Right", 1.5);
        Log.ShowStart(N, 3, "mode");
        Log.Stat(N, 2.5);
        Log.Lookup(N, "pid", "hit");
        Log.ThumbStart(N, "pid");
        Log.ThumbEnd(N, "pid", "disk", 4.5);

        Assert.True(Delivered(1, "KeyInput", N, "Right", 1.5), Diagnosis());
        Assert.True(Delivered(2, "ShowStart", N, 3, "mode"), Diagnosis());
        Assert.True(Delivered(3, "Stat", N, 2.5), Diagnosis());
        Assert.True(Delivered(4, "Lookup", N, "pid", "hit"), Diagnosis());
        Assert.True(Delivered(5, "ThumbStart", N, "pid"), Diagnosis());
        Assert.True(Delivered(6, "ThumbEnd", N, "pid", "disk", 4.5), Diagnosis());
    }

    [Fact(DisplayName = "Join, disk cache, source read, decode and verify events carry their id, name and payload")]
    public void LoadPipelineEvents()
    {
        Log.JoinStart(N, "pid");
        Log.JoinEnd(N, "pid", 5.5);
        Log.DiskCacheRead(N, "pid", 6.5, 1000L);
        Log.SourceRead(N, "pid", 7.5, 2000L);
        Log.Decode(N, "pid", 8.5, 1920, true, false);
        Log.Verify(N, "pid", 9.5);

        Assert.True(Delivered(7, "JoinStart", N, "pid"), Diagnosis());
        Assert.True(Delivered(8, "JoinEnd", N, "pid", 5.5), Diagnosis());
        Assert.True(Delivered(9, "DiskCacheRead", N, "pid", 6.5, 1000L), Diagnosis());
        Assert.True(Delivered(11, "SourceRead", N, "pid", 7.5, 2000L), Diagnosis());
        Assert.True(Delivered(12, "Decode", N, "pid", 8.5, 1920, true, false), Diagnosis());
        Assert.True(Delivered(13, "Verify", N, "pid", 9.5), Diagnosis());
    }

    [Fact(DisplayName = "Assign, render, present and post-work events carry their id, name and payload")]
    public void PresentationEvents()
    {
        Log.Assign(N, 10.5, 800, 600);
        Log.Rendered(N, 11.5);
        Log.Presented(N, "kind");
        Log.PostStart(N, "part");
        Log.PostEnd(N, "part", 12.5);
        Log.RenderedFrame(N, 17.5);

        Assert.True(Delivered(14, "Assign", N, 10.5, 800, 600), Diagnosis());
        Assert.True(Delivered(15, "Rendered", N, 11.5), Diagnosis());
        Assert.True(Delivered(16, "Presented", N, "kind"), Diagnosis());
        Assert.True(Delivered(17, "PostStart", N, "part"), Diagnosis());
        Assert.True(Delivered(18, "PostEnd", N, "part", 12.5), Diagnosis());
        Assert.True(Delivered(25, "RenderedFrame", N, 17.5), Diagnosis());
    }

    [Fact(DisplayName = "Preload, dispatcher, folder, diag-mode and startup events carry their id, name and payload")]
    public void PreloadFolderAndStartupEvents()
    {
        Log.PreloadItem(4, "pid", 13.5, "kind", 14.5);
        Log.PreloadPaused(85, 512L);
        Log.PreloadCancel("why");
        Log.DispatcherLongOp(15.5, "prio", "name");
        Log.Folder(N, "phase", 16.5);
        Log.DiagMode("flags");
        Log.Startup("phase", 18.5);
        Log.FolderInfo(N, "phase", 99L, "detail");

        Assert.True(Delivered(19, "PreloadItem", 4, "pid", 13.5, "kind", 14.5), Diagnosis());
        Assert.True(Delivered(20, "PreloadPaused", 85, 512L), Diagnosis());
        Assert.True(Delivered(21, "PreloadCancel", "why"), Diagnosis());
        Assert.True(Delivered(22, "DispatcherLongOp", 15.5, "prio", "name"), Diagnosis());
        Assert.True(Delivered(23, "Folder", N, "phase", 16.5), Diagnosis());
        Assert.True(Delivered(24, "DiagMode", "flags"), Diagnosis());
        Assert.True(Delivered(26, "Startup", "phase", 18.5), Diagnosis());
        Assert.True(Delivered(27, "FolderInfo", N, "phase", 99L, "detail"), Diagnosis());
    }

    [Fact(DisplayName = "StartupMark emits a Startup event for the phase with a plausible time since process start")]
    public void StartupMark_EmitsStartupEvent()
    {
        PhotoReviewPerf.StartupMark("mt-mark-phase");

        var evt = _listener.Events.Single(e => e.Id == 26 && (string?)e.Payload[0] == "mt-mark-phase");
        Assert.InRange((double)evt.Payload[1]!, 0, 24 * 3600 * 1000.0);
    }

    [Fact(DisplayName = "Ms converts a Stopwatch timestamp to elapsed milliseconds (bounded above and below)")]
    public void Ms_ConvertsTicksToMilliseconds()
    {
        var now = Stopwatch.GetTimestamp();
        Assert.InRange(PhotoReviewPerf.Ms(now), 0, 10_000);

        var halfSecondAgo = now - Stopwatch.Frequency / 2;
        Assert.InRange(PhotoReviewPerf.Ms(halfSecondAgo), 499, 10_500);

        var tenSecondsAgo = now - 10 * Stopwatch.Frequency;
        Assert.InRange(PhotoReviewPerf.Ms(tenSecondsAgo), 9_999, 20_000);
    }
}

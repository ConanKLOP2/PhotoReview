namespace PhotoReview.App.Services;

/// <summary>
/// perf(render-metric): reports "first frame after the assign" and "frame that contains the new image" for a presented
/// image from <c>CompositionTarget.Rendering</c> ticks. The tick source is injected so the subscription count is testable.
/// </summary>
internal sealed class RenderFrameTrace(
    Action<EventHandler> subscribe,
    Action<EventHandler> unsubscribe,
    Action<long, string, long> onFirstFrame,
    Action<long, long> onSecondFrame)
{
    private EventHandler? _pending;

    /// <summary>True while a trace waits for its second tick (a handler is subscribed).</summary>
    public bool IsPending => _pending is not null;

    public void Start(long token, string kind, long assignedTimestamp)
    {
        Cancel(); // RV-A17: at most one pending handler; a newer present supersedes an older trace that never ticked
        var tickCount = 0;
        EventHandler? handler = null;
        handler = (_, _) =>
        {
            tickCount++;
            if (tickCount == 1)
            {
                onFirstFrame(token, kind, assignedTimestamp);
                return;
            }
            Cancel();
            onSecondFrame(token, assignedTimestamp);
        };
        _pending = handler;
        subscribe(handler);
    }

    /// <summary>Drops the pending trace (if any) without reporting it.</summary>
    public void Cancel()
    {
        var handler = _pending;
        if (handler is null) return;
        _pending = null;
        unsubscribe(handler);
    }
}

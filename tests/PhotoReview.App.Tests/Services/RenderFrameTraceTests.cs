using PhotoReview.App.Services;

namespace PhotoReview.App.Tests.Services;

public sealed class RenderFrameTraceTests
{
    private sealed class FakeTicks
    {
        public List<EventHandler> Handlers { get; } = [];
        public void Tick() { foreach (var h in Handlers.ToArray()) h(this, EventArgs.Empty); }
    }

    private static RenderFrameTrace Create(FakeTicks ticks, List<string> log)
        => new(h => ticks.Handlers.Add(h), h => ticks.Handlers.Remove(h),
            (token, kind, _) => log.Add($"first:{token}:{kind}"), (token, _) => log.Add($"second:{token}"));

    [Fact]
    public void Start_FiveTimesWithoutAnyTick_KeepsOneSubscription()
    {
        var ticks = new FakeTicks();
        var trace = Create(ticks, []);

        for (var i = 0; i < 5; i++) trace.Start(i, "preview", 0);

        Assert.Single(ticks.Handlers); // a minimized window never renders: handlers must not pile up
    }

    [Fact]
    public void Tick_AfterSecondStart_ReportsOnlyTheLatestTrace()
    {
        var ticks = new FakeTicks();
        var log = new List<string>();
        var trace = Create(ticks, log);
        trace.Start(1, "preview", 0);
        trace.Start(2, "preview", 0);

        ticks.Tick();
        ticks.Tick();

        Assert.Equal(["first:2:preview", "second:2"], log);
        Assert.Empty(ticks.Handlers);
    }

    [Fact]
    public void Cancel_WhilePending_UnsubscribesWithoutReporting()
    {
        var ticks = new FakeTicks();
        var log = new List<string>();
        var trace = Create(ticks, log);
        trace.Start(1, "preview", 0);

        trace.Cancel();
        ticks.Tick();

        Assert.Empty(ticks.Handlers);
        Assert.Empty(log);
    }
}

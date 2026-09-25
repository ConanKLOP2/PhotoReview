using PhotoReview.App.Input;
using Xunit;

namespace PhotoReview.App.Tests.Input;

/// <summary>feat/mouse-zoom: release velocity estimate and the time-driven kinetic step (friction, edges, stop).</summary>
public sealed class KineticPanTests
{
    private static readonly ScrollBounds Large = new(ExtentWidth: 10000, ExtentHeight: 10000, ViewportWidth: 800, ViewportHeight: 600);

    // ---- velocity ----

    [Fact]
    public void Velocity_UniformDrag_IsTheDragSpeed()
    {
        var tracker = new PanVelocityTracker();
        for (var t = 0; t <= 200; t += 10) tracker.Add(t, t * 2.0, -t * 0.5);

        var (x, y) = tracker.GetVelocity();

        Assert.Equal(2.0, x, 6);
        Assert.Equal(-0.5, y, 6);
    }

    [Fact]
    public void Velocity_UsesOnlyTheLastWindowBeforeRelease()
    {
        var tracker = new PanVelocityTracker();
        // Fast early movement, then slow for the last 100 ms: only the slow part counts.
        for (var t = 0; t <= 100; t += 10) tracker.Add(t, t * 10.0, 0);
        for (var t = 110; t <= 200; t += 10) tracker.Add(t, 1000 + (t - 100) * 1.0, 0);

        var (x, _) = tracker.GetVelocity();

        Assert.Equal(1.0, x, 6);
    }

    [Fact]
    public void Velocity_PointerRestedLongerThanTheWindowBeforeRelease_IsZero()
    {
        var tracker = new PanVelocityTracker();
        for (var t = 0; t <= 100; t += 10) tracker.Add(t, t * 3.0, 0);
        tracker.Add(300, 300, 0); // release sample, same position, 200 ms later

        Assert.Equal((0.0, 0.0), tracker.GetVelocity());
    }

    [Fact]
    public void Velocity_ShortPauseBeforeRelease_DilutesTheSpeed()
    {
        var tracker = new PanVelocityTracker();
        for (var t = 0; t <= 60; t += 10) tracker.Add(t, t * 2.0, 0);
        tracker.Add(80, 120, 0); // rested 20 ms before release: 120 DIP over the 80 ms window instead of 2 DIP/ms

        var (x, _) = tracker.GetVelocity();

        Assert.Equal(120.0 / 80.0, x, 6);
        Assert.True(x < 2.0);
    }

    [Fact]
    public void Velocity_TooFewOrTooCloseSamples_IsZero()
    {
        var tracker = new PanVelocityTracker();
        Assert.Equal((0.0, 0.0), tracker.GetVelocity());
        tracker.Add(0, 0, 0);
        tracker.Add(3, 30, 0);
        Assert.Equal((0.0, 0.0), tracker.GetVelocity());
    }

    [Fact]
    public void Velocity_RingBufferWrapsWithoutLosingTheNewestSamples()
    {
        var tracker = new PanVelocityTracker();
        for (var t = 0; t < PanVelocityTracker.Capacity * 3; t++) tracker.Add(t * 5, t * 5 * 1.5, 0);

        Assert.Equal(PanVelocityTracker.Capacity, tracker.Count);
        Assert.Equal(1.5, tracker.GetVelocity().X, 6);
    }

    // ---- kinetic step ----

    [Fact]
    public void Start_BelowThreshold_DoesNotGlide()
    {
        var scroller = new KineticScroller();

        Assert.False(scroller.Start(0.05, 0.05));
        Assert.False(scroller.IsActive);
    }

    [Fact]
    public void Start_ScrollVelocityIsOppositeThePointerAndCapped()
    {
        var scroller = new KineticScroller();

        Assert.True(scroller.Start(2, -100));

        Assert.Equal(-2, scroller.VelocityX, 6);
        Assert.Equal(KineticScroller.MaxVelocity, scroller.VelocityY, 6);
    }

    [Fact]
    public void Step_MovesWithVelocityAndDecaysExponentially()
    {
        var scroller = new KineticScroller();
        scroller.Start(-1, 0); // pointer moved left -> content scrolls right at 1 DIP/ms

        var (h, v) = scroller.Step(16, 1000, 500, Large);

        var tau = KineticScroller.TimeConstantMs;
        Assert.Equal(1000 + tau * (1 - Math.Exp(-16 / tau)), h, 6);
        Assert.Equal(500, v, 6);
        Assert.Equal(Math.Exp(-16 / tau), scroller.VelocityX, 6);
    }

    [Fact]
    public void Step_DistanceIsIndependentOfFrameRate()
    {
        var at60 = new KineticScroller();
        var at144 = new KineticScroller();
        at60.Start(-3, 0);
        at144.Start(-3, 0);
        double h60 = 1000, h144 = 1000;

        for (var i = 0; i < 60; i++) h60 = at60.Step(1000.0 / 60, h60, 0, Large).Horizontal;
        for (var i = 0; i < 144; i++) h144 = at144.Step(1000.0 / 144, h144, 0, Large).Horizontal;

        Assert.Equal(h60, h144, 3);
    }

    [Fact]
    public void Step_GlideDecelerates_ThenStopsNearTheTotalDistance()
    {
        var scroller = new KineticScroller();
        scroller.Start(-2, 0);
        var h = 1000.0;
        var lastStep = double.MaxValue;
        var frames = 0;

        while (scroller.IsActive && frames < 1000)
        {
            var next = scroller.Step(16, h, 0, Large).Horizontal;
            var step = next - h;
            Assert.True(step <= lastStep + 1e-9); // never speeds up
            lastStep = step;
            h = next;
            frames++;
        }

        Assert.False(scroller.IsActive);
        Assert.InRange(frames, 10, 200);
        // Total distance of an unbounded exponential glide is v0 * tau; stopping at StopVelocity leaves at most StopVelocity * tau.
        var full = 2 * KineticScroller.TimeConstantMs;
        Assert.InRange(h - 1000, full - KineticScroller.StopVelocity * KineticScroller.TimeConstantMs - 1e-6, full);
    }

    [Fact]
    public void Step_ReachingAnEdge_StopsThatAxisOnly()
    {
        var bounds = new ScrollBounds(2000, 2000, 800, 600); // max offsets 1200 x 1400
        var scroller = new KineticScroller();
        scroller.Start(-8, -1); // content scrolls right fast and down slowly

        var (h, v) = scroller.Step(100, 1190, 100, bounds);

        Assert.Equal(1200, h, 6);           // clamped at the right edge
        Assert.Equal(0, scroller.VelocityX); // that axis stopped
        Assert.True(v > 100);
        Assert.True(scroller.VelocityY > 0); // the other axis keeps gliding
        Assert.True(scroller.IsActive);
    }

    [Fact]
    public void Step_AtTheEdgeAlready_StopsTheGlide()
    {
        var scroller = new KineticScroller();
        scroller.Start(1, 0); // content scrolls left, but it is already at 0

        var (h, _) = scroller.Step(16, 0, 0, Large);

        Assert.Equal(0, h);
        Assert.False(scroller.IsActive);
    }

    [Fact]
    public void Step_LongFrameGap_IsCapped()
    {
        var scroller = new KineticScroller();
        scroller.Start(-1, 0);
        var capped = new KineticScroller();
        capped.Start(-1, 0);

        var afterGap = scroller.Step(5000, 0, 0, Large).Horizontal;
        var afterMax = capped.Step(KineticScroller.MaxFrameMs, 0, 0, Large).Horizontal;

        Assert.Equal(afterMax, afterGap, 6);
    }

    [Fact]
    public void Stop_EndsTheGlideImmediately()
    {
        var scroller = new KineticScroller();
        scroller.Start(-3, -3);

        scroller.Stop();
        var (h, v) = scroller.Step(16, 50, 60, Large);

        Assert.False(scroller.IsActive);
        Assert.Equal((50.0, 60.0), (h, v));
    }
}

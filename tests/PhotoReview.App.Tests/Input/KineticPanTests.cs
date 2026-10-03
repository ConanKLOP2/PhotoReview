using PhotoReview.App.Input;

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
        var factor = KineticScroller.PointerReleaseSpeedFactor;

        Assert.True(scroller.Start(2, -100));

        Assert.Equal(-2 * factor, scroller.VelocityX, 6);
        Assert.Equal(KineticScroller.MaxVelocity, scroller.VelocityY, 6); // -(-100)*factor still well past the cap

        Assert.True(scroller.Start(100, 1));
        Assert.Equal(-KineticScroller.MaxVelocity, scroller.VelocityX, 6); // -100*factor still well past the cap
        Assert.Equal(-1 * factor, scroller.VelocityY, 6);
    }

    // ---- Q-R40: mouse-release damping (PointerReleaseSpeedFactor) ----

    [Fact]
    public void Start_DampsTheRawPointerVelocityByPointerReleaseSpeedFactor()
    {
        var scroller = new KineticScroller();
        const double rawVelocity = 1.2; // realistic flick, DIP/ms, well under MaxVelocity so the factor is visible

        Assert.True(scroller.Start(rawVelocity, 0));

        Assert.Equal(-rawVelocity * KineticScroller.PointerReleaseSpeedFactor, scroller.VelocityX, 9);
        Assert.InRange(KineticScroller.PointerReleaseSpeedFactor, 0.5, 0.7); // matches the documented, chosen range
    }

    [Fact]
    public void Start_RealisticFlick_StillStartsAGlideAfterDamping()
    {
        var scroller = new KineticScroller();
        // A moderate flick (well above StartVelocity even after damping) still starts gliding.
        const double rawVelocity = 0.5;

        Assert.True(scroller.Start(rawVelocity, 0));
        Assert.True(scroller.IsActive);
        Assert.True(Math.Abs(scroller.VelocityX) >= KineticScroller.StartVelocity);
    }

    [Fact]
    public void AddImpulse_IsNotDampedByPointerReleaseSpeedFactor()
    {
        // Regression guard (Q-R40): AddImpulse (keyboard arrow-key panning) must be completely unaffected by the
        // mouse-release damping factor -- KeyboardPan.ImpulseVelocity sizes its input to land within ~1 DIP of an
        // exact step distance, so damping it here would make arrow-key panning systematically undershoot.
        var scroller = new KineticScroller();
        const double velocityX = 1.5;
        const double velocityY = -0.5;

        scroller.AddImpulse(velocityX, velocityY);

        Assert.Equal(velocityX, scroller.VelocityX, 9);
        Assert.Equal(velocityY, scroller.VelocityY, 9);
    }

    [Fact]
    public void Step_MovesWithVelocityAndDecaysExponentially()
    {
        var scroller = new KineticScroller();
        scroller.Start(-1, 0); // pointer moved left -> content scrolls right, damped by PointerReleaseSpeedFactor
        var v0 = KineticScroller.PointerReleaseSpeedFactor; // effective scroll velocity after Start's damping

        var (h, v) = scroller.Step(16, 1000, 500, Large);

        var tau = KineticScroller.TimeConstantMs;
        Assert.Equal(1000 + v0 * tau * (1 - Math.Exp(-16 / tau)), h, 6);
        Assert.Equal(500, v, 6);
        Assert.Equal(v0 * Math.Exp(-16 / tau), scroller.VelocityX, 6);
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
        // Total distance of an unbounded exponential glide is v0 * tau (v0 is the effective, damped scroll velocity);
        // stopping at StopVelocity leaves at most StopVelocity * tau.
        var full = 2 * KineticScroller.PointerReleaseSpeedFactor * KineticScroller.TimeConstantMs;
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

    // ---- AddImpulse (feat/zoom-key-anchor: kinetic arrow-key panning) ----

    [Fact]
    public void AddImpulse_WhileIdle_StartsAGlideAtThatVelocity()
    {
        var scroller = new KineticScroller();

        scroller.AddImpulse(1.5, -0.5);

        Assert.True(scroller.IsActive);
        Assert.Equal(1.5, scroller.VelocityX, 6);
        Assert.Equal(-0.5, scroller.VelocityY, 6);
    }

    [Fact]
    public void AddImpulse_WhileGliding_AccumulatesOntoTheCurrentVelocity()
    {
        var scroller = new KineticScroller();
        scroller.AddImpulse(1.0, 0.2);

        scroller.AddImpulse(0.5, 0.1);

        Assert.Equal(1.5, scroller.VelocityX, 6);
        Assert.Equal(0.3, scroller.VelocityY, 6);
    }

    [Fact]
    public void AddImpulse_OppositeDirection_ReducesOrReversesTheVelocity()
    {
        var scroller = new KineticScroller();
        scroller.AddImpulse(2.0, 0);

        scroller.AddImpulse(-0.5, 0);
        Assert.Equal(1.5, scroller.VelocityX, 6);

        scroller.AddImpulse(-3.0, 0);
        Assert.Equal(-1.5, scroller.VelocityX, 6);
        Assert.True(scroller.IsActive); // still gliding, just the other way
    }

    [Fact]
    public void AddImpulse_ClampsEachAxisToMaxVelocity()
    {
        var scroller = new KineticScroller();

        scroller.AddImpulse(KineticScroller.MaxVelocity * 10, -KineticScroller.MaxVelocity * 10);

        Assert.Equal(KineticScroller.MaxVelocity, scroller.VelocityX, 6);
        Assert.Equal(-KineticScroller.MaxVelocity, scroller.VelocityY, 6);
    }

    [Fact]
    public void AddImpulse_NonFiniteComponent_IsIgnored_OtherAxisStillApplies()
    {
        var scroller = new KineticScroller();

        scroller.AddImpulse(double.NaN, 1.0);
        Assert.Equal(0.0, scroller.VelocityX, 6);
        Assert.Equal(1.0, scroller.VelocityY, 6);

        scroller.AddImpulse(double.PositiveInfinity, double.NegativeInfinity);
        Assert.Equal(0.0, scroller.VelocityX, 6);
        Assert.Equal(1.0, scroller.VelocityY, 6);
    }

    // ---- KeyboardPan.ImpulseVelocity ----

    [Fact]
    public void ImpulseVelocity_SignMatchesTheStepDirection()
    {
        var (vx, vy) = KeyboardPan.ImpulseVelocity(-80, 60);

        Assert.True(vx < 0);
        Assert.True(vy > 0);
    }

    [Fact]
    public void ImpulseVelocity_DrivenToRest_TravelsTheStepDistance_WithinOneDip()
    {
        const double step = 80.0;
        var (vx, _) = KeyboardPan.ImpulseVelocity(step, 0);
        var scroller = new KineticScroller();
        scroller.AddImpulse(vx, 0);
        var h = 0.0;

        var frames = 0;
        while (scroller.IsActive && frames < 10_000)
        {
            h = scroller.Step(16.7, h, 0, Large).Horizontal;
            frames++;
        }

        Assert.InRange(frames, 1, 9_999); // it did stop on its own
        Assert.Equal(step, h, tolerance: 1.0);
    }

    // ---- ConsumesKey: arrow keys inside a zoomed image ----

    private static readonly ScrollBounds Fit = new(800, 600, 800, 600);
    private static readonly ScrollBounds BothAxes = new(2000, 1500, 800, 600);
    private static readonly ScrollBounds HorizontalOnly = new(2000, 600, 800, 600);
    private static readonly ScrollBounds VerticalOnly = new(800, 1500, 800, 600);

    public static TheoryData<string> ZoomedCases => new() { "both", "horizontal", "vertical" };

    private static ScrollBounds Named(string name) => name switch
    {
        "both" => BothAxes,
        "horizontal" => HorizontalOnly,
        "vertical" => VerticalOnly,
        _ => Fit,
    };

    [Theory]
    [MemberData(nameof(ZoomedCases))]
    public void ConsumesKey_Default_ZoomedConsumesEveryResult(string zoom)
    {
        foreach (var result in new[] { KeyboardPanResult.Panned, KeyboardPanResult.AtEdge, KeyboardPanResult.NotScrollable })
            foreach (var repeat in new[] { false, true })
                Assert.True(KeyboardPan.ConsumesKey(result, repeat, navigatesAtEdge: false, Named(zoom)));
    }

    [Fact]
    public void ConsumesKey_Default_AtFitFallsThrough()
    {
        foreach (var repeat in new[] { false, true })
            Assert.False(KeyboardPan.ConsumesKey(KeyboardPanResult.NotScrollable, repeat, navigatesAtEdge: false, Fit));
    }

    [Theory]
    [MemberData(nameof(ZoomedCases))]
    public void ConsumesKey_Legacy_MatchesThePreviousBehaviour(string zoom)
    {
        var bounds = Named(zoom);
        Assert.True(KeyboardPan.ConsumesKey(KeyboardPanResult.Panned, false, navigatesAtEdge: true, bounds));
        Assert.True(KeyboardPan.ConsumesKey(KeyboardPanResult.Panned, true, navigatesAtEdge: true, bounds));
        Assert.False(KeyboardPan.ConsumesKey(KeyboardPanResult.AtEdge, false, navigatesAtEdge: true, bounds));
        Assert.True(KeyboardPan.ConsumesKey(KeyboardPanResult.AtEdge, true, navigatesAtEdge: true, bounds));
        Assert.False(KeyboardPan.ConsumesKey(KeyboardPanResult.NotScrollable, false, navigatesAtEdge: true, bounds));
        Assert.False(KeyboardPan.ConsumesKey(KeyboardPanResult.NotScrollable, true, navigatesAtEdge: true, bounds));
    }

    [Fact]
    public void IsZoomed_TrueOnEitherAxis_FalseAtFit()
    {
        Assert.False(KeyboardPan.IsZoomed(Fit));
        Assert.True(KeyboardPan.IsZoomed(HorizontalOnly));
        Assert.True(KeyboardPan.IsZoomed(VerticalOnly));
        Assert.True(KeyboardPan.IsZoomed(BothAxes));
    }

    // ---- KeyboardPan.Step ----

    [Fact]
    public void Step_Horizontal_Positive_UsesDefaultStepFraction()
    {
        var bounds = new ScrollBounds(ExtentWidth: 2000, ExtentHeight: 1500, ViewportWidth: 800, ViewportHeight: 600);
        var horizontal = 400.0;
        var vertical = 300.0;

        var (result, h, v) = KeyboardPan.Step(dx: 1, dy: 0, horizontal, vertical, bounds);

        // step = ViewportWidth * StepFraction * (dx + dy) = 800 * 0.1 * 1 = 80
        var expected = horizontal + 80;
        Assert.Equal(KeyboardPanResult.Panned, result);
        Assert.Equal(expected, h);
        Assert.Equal(vertical, v);
    }

    [Fact]
    public void Step_Horizontal_Negative_MovesLeft()
    {
        var bounds = new ScrollBounds(ExtentWidth: 2000, ExtentHeight: 1500, ViewportWidth: 800, ViewportHeight: 600);
        var horizontal = 400.0;
        var vertical = 300.0;

        var (result, h, v) = KeyboardPan.Step(dx: -1, dy: 0, horizontal, vertical, bounds);

        // step = ViewportWidth * StepFraction * (dx + dy) = 800 * 0.1 * (-1) = -80
        var expected = horizontal - 80;
        Assert.Equal(KeyboardPanResult.Panned, result);
        Assert.Equal(expected, h);
        Assert.Equal(vertical, v);
    }

    [Fact]
    public void Step_Vertical_Positive_UsesViewportHeight()
    {
        var bounds = new ScrollBounds(ExtentWidth: 2000, ExtentHeight: 1500, ViewportWidth: 800, ViewportHeight: 600);
        var horizontal = 400.0;
        var vertical = 300.0;

        var (result, h, v) = KeyboardPan.Step(dx: 0, dy: 1, horizontal, vertical, bounds);

        // step = ViewportHeight * StepFraction * (dx + dy) = 600 * 0.1 * 1 = 60
        var expected = vertical + 60;
        Assert.Equal(KeyboardPanResult.Panned, result);
        Assert.Equal(horizontal, h);
        Assert.Equal(expected, v);
    }

    [Fact]
    public void Step_Vertical_Negative_MovesUp()
    {
        var bounds = new ScrollBounds(ExtentWidth: 2000, ExtentHeight: 1500, ViewportWidth: 800, ViewportHeight: 600);
        var horizontal = 400.0;
        var vertical = 300.0;

        var (result, h, v) = KeyboardPan.Step(dx: 0, dy: -1, horizontal, vertical, bounds);

        // step = ViewportHeight * StepFraction * (dx + dy) = 600 * 0.1 * (-1) = -60
        var expected = vertical - 60;
        Assert.Equal(KeyboardPanResult.Panned, result);
        Assert.Equal(horizontal, h);
        Assert.Equal(expected, v);
    }

    [Fact]
    public void Step_CustomStepFraction_0_01_MovesOnePercent()
    {
        var bounds = new ScrollBounds(ExtentWidth: 2000, ExtentHeight: 1500, ViewportWidth: 800, ViewportHeight: 600);
        var horizontal = 400.0;
        var vertical = 300.0;

        var (result, h, v) = KeyboardPan.Step(dx: 1, dy: 0, horizontal, vertical, bounds, stepFraction: 0.01);

        // step = ViewportWidth * 0.01 * 1 = 800 * 0.01 = 8
        var expected = horizontal + 8;
        Assert.Equal(KeyboardPanResult.Panned, result);
        Assert.Equal(expected, h);
        Assert.Equal(vertical, v);
    }

    [Fact]
    public void Step_CustomStepFraction_1_0_MovesEntireViewport()
    {
        var bounds = new ScrollBounds(ExtentWidth: 2000, ExtentHeight: 1500, ViewportWidth: 800, ViewportHeight: 600);
        var horizontal = 400.0;
        var vertical = 300.0;

        var (result, h, v) = KeyboardPan.Step(dx: 1, dy: 0, horizontal, vertical, bounds, stepFraction: 1.0);

        // step = ViewportWidth * 1.0 * 1 = 800 * 1 = 800
        var expected = horizontal + 800;
        Assert.Equal(KeyboardPanResult.Panned, result);
        Assert.Equal(expected, h);
        Assert.Equal(vertical, v);
    }

    [Fact]
    public void Step_AtRightEdge_ReturnsAtEdge()
    {
        var bounds = new ScrollBounds(ExtentWidth: 2000, ExtentHeight: 1500, ViewportWidth: 800, ViewportHeight: 600);
        var horizontal = bounds.MaxHorizontal; // 2000 - 800 = 1200
        var vertical = 300.0;

        var (result, h, v) = KeyboardPan.Step(dx: 1, dy: 0, horizontal, vertical, bounds);

        // Already at the edge; step would move further but gets clamped
        Assert.Equal(KeyboardPanResult.AtEdge, result);
        Assert.Equal(horizontal, h);
        Assert.Equal(vertical, v);
    }

    [Fact]
    public void Step_NearRightEdge_ClampsToBoundary()
    {
        var bounds = new ScrollBounds(ExtentWidth: 2000, ExtentHeight: 1500, ViewportWidth: 800, ViewportHeight: 600);
        var maxH = bounds.MaxHorizontal; // 1200
        var horizontal = maxH - 50; // 1150
        var vertical = 300.0;

        var (result, h, v) = KeyboardPan.Step(dx: 1, dy: 0, horizontal, vertical, bounds);

        // step = 800 * 0.1 * 1 = 80, but clamped to max
        Assert.Equal(KeyboardPanResult.Panned, result);
        Assert.Equal(maxH, h);
        Assert.Equal(vertical, v);
    }

    [Fact]
    public void Step_AtBottomEdge_ReturnsAtEdge()
    {
        var bounds = new ScrollBounds(ExtentWidth: 2000, ExtentHeight: 1500, ViewportWidth: 800, ViewportHeight: 600);
        var horizontal = 400.0;
        var vertical = bounds.MaxVertical; // 1500 - 600 = 900

        var (result, h, v) = KeyboardPan.Step(dx: 0, dy: 1, horizontal, vertical, bounds);

        Assert.Equal(KeyboardPanResult.AtEdge, result);
        Assert.Equal(horizontal, h);
        Assert.Equal(vertical, v);
    }

    [Fact]
    public void Step_NearBottomEdge_ClampsToBoundary()
    {
        var bounds = new ScrollBounds(ExtentWidth: 2000, ExtentHeight: 1500, ViewportWidth: 800, ViewportHeight: 600);
        var horizontal = 400.0;
        var maxV = bounds.MaxVertical; // 900
        var vertical = maxV - 30; // 870

        var (result, h, v) = KeyboardPan.Step(dx: 0, dy: 1, horizontal, vertical, bounds);

        // step = 600 * 0.1 * 1 = 60, but clamped to max
        Assert.Equal(KeyboardPanResult.Panned, result);
        Assert.Equal(horizontal, h);
        Assert.Equal(maxV, v);
    }

    [Fact]
    public void Step_AtLeftEdge_ReturnsAtEdge()
    {
        var bounds = new ScrollBounds(ExtentWidth: 2000, ExtentHeight: 1500, ViewportWidth: 800, ViewportHeight: 600);
        var horizontal = 0.0;
        var vertical = 300.0;

        var (result, h, v) = KeyboardPan.Step(dx: -1, dy: 0, horizontal, vertical, bounds);

        // Already at left edge; moving further would be clamped
        Assert.Equal(KeyboardPanResult.AtEdge, result);
        Assert.Equal(horizontal, h);
        Assert.Equal(vertical, v);
    }

    [Fact]
    public void Step_NotScrollable_MaxIsSmall()
    {
        // MaxHorizontal = 100 - 800 = -700 -> Math.Max(0, -700) = 0, which is <= 0.5 (Epsilon)
        var bounds = new ScrollBounds(ExtentWidth: 100, ExtentHeight: 1500, ViewportWidth: 800, ViewportHeight: 600);
        var horizontal = 0.0;
        var vertical = 300.0;

        var (result, h, v) = KeyboardPan.Step(dx: 1, dy: 0, horizontal, vertical, bounds);

        Assert.Equal(KeyboardPanResult.NotScrollable, result);
        Assert.Equal(horizontal, h);
        Assert.Equal(vertical, v);
    }

    [Fact]
    public void Step_HorizontalAxis_UsesHorizontalDirection()
    {
        var bounds = new ScrollBounds(ExtentWidth: 2000, ExtentHeight: 1500, ViewportWidth: 800, ViewportHeight: 600);
        var horizontal = 400.0;
        var vertical = 300.0;

        var (_, h, v) = KeyboardPan.Step(dx: 1, dy: 0, horizontal, vertical, bounds);

        // Only horizontal should change; vertical stays the same
        Assert.NotEqual(horizontal, h);
        Assert.Equal(vertical, v);
    }

    [Fact]
    public void Step_VerticalAxis_UsesVerticalDirection()
    {
        var bounds = new ScrollBounds(ExtentWidth: 2000, ExtentHeight: 1500, ViewportWidth: 800, ViewportHeight: 600);
        var horizontal = 400.0;
        var vertical = 300.0;

        var (_, h, v) = KeyboardPan.Step(dx: 0, dy: 1, horizontal, vertical, bounds);

        // Only vertical should change; horizontal stays the same
        Assert.Equal(horizontal, h);
        Assert.NotEqual(vertical, v);
    }
}

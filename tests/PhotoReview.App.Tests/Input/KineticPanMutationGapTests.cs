using PhotoReview.App.Input;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Model;

namespace PhotoReview.App.Tests.Input;

/// <summary>
/// Stryker round 1 (App): the exact boundaries of the pan-velocity window, the glide start/stop thresholds, the
/// keyboard-pan edge slack and the refresh-period tolerance of the glide clock.
/// </summary>
public sealed class KineticPanMutationGapTests
{
    private static ScrollBounds Roomy => new(10_000, 10_000, 800, 600);

    // ---- PanVelocityTracker ----

    [Fact]
    public void GetVelocity_ExactlyTwoSamples_MeasuresTheirVelocity()
    {
        var tracker = new PanVelocityTracker();
        tracker.Add(0, 100, 50);
        tracker.Add(20, 140, 50);

        var (x, y) = tracker.GetVelocity();

        Assert.Equal(2.0, x, 9);
        Assert.Equal(0.0, y, 9);
    }

    [Fact]
    public void GetVelocity_SpanExactlyTheMinimum_StillMeasuresAVelocity()
    {
        var tracker = new PanVelocityTracker();
        tracker.Add(0, 0, 0);
        tracker.Add(PanVelocityTracker.MinSpanMs, 16, 8);

        var (x, y) = tracker.GetVelocity();

        Assert.Equal(2.0, x, 9);
        Assert.Equal(1.0, y, 9);
    }

    [Fact]
    public void GetVelocity_SpanJustBelowTheMinimum_IsZero()
    {
        var tracker = new PanVelocityTracker();
        tracker.Add(0, 0, 0);
        tracker.Add(PanVelocityTracker.MinSpanMs - 0.5, 16, 8);

        Assert.Equal((0.0, 0.0), tracker.GetVelocity());
    }

    [Fact]
    public void GetVelocity_FullRingWithEverySampleInsideTheWindow_UsesTheOldestOne()
    {
        var tracker = new PanVelocityTracker();
        for (var i = 0; i < PanVelocityTracker.Capacity; i++) tracker.Add(i, i * 3, 0); // 3 DIP per ms, all within 31 ms

        var (x, _) = tracker.GetVelocity();

        Assert.Equal(3.0, x, 9);
    }

    // ---- KineticScroller ----

    [Fact]
    public void Start_ReleaseSpeedExactlyTheStartVelocity_Glides()
    {
        var scroller = new KineticScroller();

        // -pointer * PointerReleaseSpeedFactor (0.65) is exactly 0.1 for this value in IEEE double arithmetic.
        Assert.True(scroller.Start(-0.15384615384615385, 0));
        Assert.True(scroller.IsActive);
        Assert.Equal(KineticScroller.StartVelocity, scroller.VelocityX);
    }

    [Fact]
    public void Start_DiagonalReleaseWhoseComponentsAreEachBelowTheThresholdButWhoseSpeedIsNot_Glides()
    {
        var scroller = new KineticScroller();
        var component = -0.08 / KineticScroller.PointerReleaseSpeedFactor;

        Assert.True(scroller.Start(component, component));
        Assert.True(scroller.IsActive);
    }

    [Fact]
    public void Step_VerticalVelocity_DecaysByTheFrictionFactor()
    {
        var scroller = new KineticScroller();
        scroller.AddImpulse(0, 1.0);
        var decay = Math.Exp(-16 / KineticScroller.TimeConstantMs);

        scroller.Step(16, 0, 0, Roomy);

        Assert.Equal(decay, scroller.VelocityY, 9);
    }

    [Fact]
    public void Step_HorizontalSpeedExactlyTheStopVelocityAfterAZeroLengthFrame_KeepsGliding()
    {
        var scroller = new KineticScroller();
        scroller.AddImpulse(KineticScroller.StopVelocity, 0);

        scroller.Step(0, 0, 0, Roomy);

        Assert.True(scroller.IsActive);
        Assert.Equal(KineticScroller.StopVelocity, scroller.VelocityX);
    }

    [Fact]
    public void Step_VerticalSpeedExactlyTheStopVelocityAfterAZeroLengthFrame_KeepsGliding()
    {
        var scroller = new KineticScroller();
        scroller.AddImpulse(0, KineticScroller.StopVelocity);

        scroller.Step(0, 0, 0, Roomy);

        Assert.True(scroller.IsActive);
        Assert.Equal(KineticScroller.StopVelocity, scroller.VelocityY);
    }

    // ---- KeyboardPan ----

    [Fact]
    public void KeyboardPanStep_ScrollRangeExactlyTheSlack_IsNotScrollable()
    {
        var bounds = new ScrollBounds(800.5, 600.5, 800, 600);

        Assert.Equal(KeyboardPanResult.NotScrollable, KeyboardPan.Step(1, 0, 0, 0, bounds).Result);
        Assert.Equal(KeyboardPanResult.NotScrollable, KeyboardPan.Step(0, 1, 0, 0, bounds).Result);
        Assert.False(KeyboardPan.IsZoomed(bounds));
    }

    [Fact]
    public void KeyboardPanStep_ScrollRangeJustAboveTheSlack_IsZoomedAndScrollable()
    {
        var bounds = new ScrollBounds(800.6, 600.0, 800, 600);

        Assert.True(KeyboardPan.IsZoomed(bounds));
        Assert.True(KeyboardPan.IsZoomed(new ScrollBounds(800, 600.6, 800, 600)));
    }

    [Fact]
    public void KeyboardPanStep_StepOfExactlyTheSlack_StillPans()
    {
        var bounds = new ScrollBounds(105, 600, 5, 600); // 10 % of a 5 DIP viewport = 0.5 DIP

        var (result, horizontal, _) = KeyboardPan.Step(1, 0, 0, 0, bounds);

        Assert.Equal(KeyboardPanResult.Panned, result);
        Assert.Equal(0.5, horizontal, 9);
    }

    // ---- GlideFrameClock ----

    [Fact]
    public void Start_ZeroTicksPerMillisecond_FallsBackToOffSmoothing()
    {
        var clock = new GlideFrameClock();

        clock.Start(KineticGlideSmoothing.Predict, 0);

        Assert.False(clock.NeedsDisplayTiming);
    }

    [Fact]
    public void Advance_RefreshPeriodChangedByOnePercent_KeepsTheAnchoredGridInsteadOfReAnchoring()
    {
        const double ticksPerMs = 1000;
        var clock = new GlideFrameClock();
        clock.Start(KineticGlideSmoothing.Predict, ticksPerMs);
        Assert.Equal(0, clock.Advance(0, 0, new DisplayTiming(0, 16667))); // anchors the 16667-tick grid

        var advanced = clock.Advance(16.0, 16667, new DisplayTiming(0, 16833)); // +1 %: within the 2 % tolerance

        Assert.Equal(16.833, advanced, 6); // one refresh of the NEW period, not RenderingTime's 16 ms
    }
}

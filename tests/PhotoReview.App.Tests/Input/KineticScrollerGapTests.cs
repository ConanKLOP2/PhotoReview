using PhotoReview.App.Input;

namespace PhotoReview.App.Tests.Input;

/// <summary>
/// RV-T27 residue (AddImpulse non-finite, the MaxFrameMs cap and the GlideFrameClock period change are already pinned by
/// KineticPanTests / GlideFrameClockTests): a non-finite or negative frame time must not move or accelerate the glide.
/// </summary>
public sealed class KineticScrollerGapTests
{
    private static readonly ScrollBounds Large = new(100_000, 100_000, 1_000, 1_000);

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-50.0)]
    public void Step_NonFiniteOrNegativeElapsed_DoesNotMoveOrDecayTheGlide(double elapsedMs)
    {
        var scroller = new KineticScroller();
        scroller.Start(-2, 1);
        var (vx, vy) = (scroller.VelocityX, scroller.VelocityY);

        var (h, v) = scroller.Step(elapsedMs, 500, 400, Large);

        // +Infinity is a legitimate "very long gap" only after Finite(): it becomes 0 like NaN, so the offset never jumps.
        Assert.Equal((500.0, 400.0), (h, v));
        Assert.Equal(vx, scroller.VelocityX, 9);
        Assert.Equal(vy, scroller.VelocityY, 9);
        Assert.True(scroller.IsActive);
    }

    [Fact]
    public void Step_NonFiniteStartOffset_IsTreatedAsZero()
    {
        var scroller = new KineticScroller();
        scroller.Start(-2, 0);

        var (h, _) = scroller.Step(16, double.NaN, 0, Large);

        Assert.True(double.IsFinite(h));
        Assert.True(h > 0);
    }
}

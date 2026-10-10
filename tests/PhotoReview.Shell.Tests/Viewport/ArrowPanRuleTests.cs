using PhotoReview.App.ViewModels;
using PhotoReview.App.Viewport;

namespace PhotoReview.Shell.Tests.Viewport;

/// <summary>WP-16: quy tắc pan phím mới - bước bằng nhau ở hai trục = phần trăm x cạnh ngắn của viewport.</summary>
public sealed class ArrowPanRuleTests
{
    // 3000x2000 trong 1000x600: viewport 990x590, Max (2010, 1410).
    private static readonly ViewportLayout Zoomed = ViewportLayoutEngine.Compute(new ViewportInput(
        1000, 600, 10, ScrollBarPolicy.Auto, ViewerStretchMode.None, 3000, 2000, double.PositiveInfinity, double.PositiveInfinity, 750, 500));

    [Theory]
    [InlineData(990, 590, 0.1, 59)]
    [InlineData(590, 990, 0.1, 59)]
    [InlineData(1270, 710, 0.25, 177.5)]
    [InlineData(0, 590, 0.1, 0)]
    [InlineData(990, 590, 0, 0)]
    [InlineData(990, 590, -0.1, 0)]
    [InlineData(double.NaN, 590, 0.1, 0)]
    [InlineData(double.PositiveInfinity, double.PositiveInfinity, 0.1, 0)]
    public void StepLength_IsTheFractionOfTheShorterViewportSide(double w, double h, double fraction, double expected) =>
        Assert.Equal(expected, ArrowPanRule.StepLength(w, h, fraction), 9);

    [Fact]
    public void BothAxes_MoveTheSameDistance()
    {
        var right = ArrowPanRule.Step(1, 0, 500, 500, Zoomed);
        var down = ArrowPanRule.Step(0, 1, 500, 500, Zoomed);
        var left = ArrowPanRule.Step(-1, 0, 500, 500, Zoomed);
        var up = ArrowPanRule.Step(0, -1, 500, 500, Zoomed);

        Assert.Equal((ArrowPanOutcome.Panned, 559.0, 500.0), right);
        Assert.Equal((ArrowPanOutcome.Panned, 500.0, 559.0), down);
        Assert.Equal((ArrowPanOutcome.Panned, 441.0, 500.0), left);
        Assert.Equal((ArrowPanOutcome.Panned, 500.0, 441.0), up);
    }

    [Fact]
    public void TheStepIsClampedAtTheEdge_ThenTheEdgeIsReported()
    {
        Assert.Equal((ArrowPanOutcome.Panned, 2010.0, 0.0), ArrowPanRule.Step(1, 0, 1990, 0, Zoomed));
        Assert.Equal((ArrowPanOutcome.AtEdge, 2010.0, 0.0), ArrowPanRule.Step(1, 0, 2010, 0, Zoomed));
        Assert.Equal((ArrowPanOutcome.AtEdge, 2009.7, 0.0), ArrowPanRule.Step(1, 0, 2009.7, 0, Zoomed)); // < 0,5 DIP còn lại
        Assert.Equal((ArrowPanOutcome.AtEdge, 0.0, 0.0), ArrowPanRule.Step(0, -1, 0, 0, Zoomed));
        Assert.Equal((ArrowPanOutcome.Panned, 0.0, 0.0), ArrowPanRule.Step(-1, 0, 30, 0, Zoomed));
        Assert.Equal((ArrowPanOutcome.Panned, 10.6, 0.0), ArrowPanRule.Step(1, 0, 10, 0, Zoomed with { MaxHorizontalOffset = 10.6 })); // còn 0,6 >= 0,5
    }

    [Fact]
    public void ARemainingDistanceOfExactlyHalfADip_StillPans_LikeKeyboardPan()
    {
        // KeyboardPan: Math.Abs(target - current) < 0,5 -> AtEdge; đúng 0,5 vẫn là Panned. 2009,5 + 59 kẹp về Max 2010.
        Assert.Equal((ArrowPanOutcome.Panned, 2010.0, 0.0), ArrowPanRule.Step(1, 0, 2009.5, 0, Zoomed));
    }

    [Fact]
    public void AnAxisThatDoesNotScroll_IsNotScrollable()
    {
        var wide = Zoomed with { MaxVerticalOffset = 0.5 };
        Assert.Equal((ArrowPanOutcome.NotScrollable, 100.0, 0.0), ArrowPanRule.Step(0, 1, 100, 0, wide));
        Assert.Equal(ArrowPanOutcome.Panned, ArrowPanRule.Step(1, 0, 100, 0, wide).Outcome);
        Assert.Equal(ArrowPanOutcome.NotScrollable, ArrowPanRule.Step(1, 0, 0, 0, Zoomed with { MaxHorizontalOffset = 0 }).Outcome);
    }

    [Fact]
    public void TheDefaultFraction_IsTenPercent()
    {
        Assert.Equal(0.1, ArrowPanRule.DefaultStepFraction);
        Assert.Equal(ArrowPanRule.Step(1, 0, 0, 0, Zoomed, 0.1), ArrowPanRule.Step(1, 0, 0, 0, Zoomed));
    }
}

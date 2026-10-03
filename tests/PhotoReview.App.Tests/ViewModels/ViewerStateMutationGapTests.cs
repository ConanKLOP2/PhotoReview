using PhotoReview.App.ViewModels;
using Xunit;

namespace PhotoReview.App.Tests.ViewModels;

/// <summary>
/// Boundary and guard pins for <see cref="ViewerState"/> found by Stryker (mutation gaps): zero/unknown sizes, viewport
/// values at or below 1 DIP or non-finite, the DPI plausibility range, and which size/viewport readings must not touch
/// the Fit-axis bookkeeping.
/// </summary>
[Trait("Category", "HotPath")]
public sealed class ViewerStateMutationGapTests
{
    private static ViewerState Fitted(int sourceWidth, int sourceHeight, double viewportWidth, double viewportHeight)
    {
        var state = new ViewerState();
        state.SetSourceSize(sourceWidth, sourceHeight);
        state.UpdateViewport(viewportWidth, viewportHeight);
        return state;
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(100, 0)]
    [InlineData(0, 0)]
    public void ImageSize_WithSourceDimensionOfZero_StaysAutoNotAZeroSizedElement(int width, int height)
    {
        var state = new ViewerState();
        state.SetZoom(2.0);
        state.SetSourceSize(width, height);

        Assert.True(double.IsNaN(state.ImageWidth));
        Assert.True(double.IsNaN(state.ImageHeight));
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(100, 0)]
    public void FitZoom_WithUnknownSourceAxis_IsUnknownEvenIfTheOtherAxisIsKnown(int width, int height)
    {
        var state = Fitted(width, height, 800, 600);

        Assert.Equal(0, state.FitZoom);
    }

    [Theory]
    [InlineData(1.0, 600.0)] // exactly 1 DIP is not a usable viewport
    [InlineData(800.0, 1.0)]
    [InlineData(double.PositiveInfinity, 600.0)]
    [InlineData(800.0, double.PositiveInfinity)]
    [InlineData(double.NaN, 600.0)]
    [InlineData(800.0, double.NaN)]
    [InlineData(0.5, 600.0)]
    [InlineData(800.0, 0.5)]
    public void FitZoom_WithUnusableViewportLimits_IsUnknown(double maxWidth, double maxHeight)
    {
        var state = new ViewerState();
        state.SetSourceSize(1000, 1000);
        state.MaxImageWidth = maxWidth;
        state.MaxImageHeight = maxHeight;

        Assert.Equal(0, state.FitZoom);
    }

    [Fact]
    public void FitWidthZoom_WithViewportWidthOfExactlyOne_IsUnknown()
    {
        var state = new ViewerState();
        state.SetSourceSize(1000, 1000);
        state.MaxImageWidth = 1.0;

        Assert.Equal(0, state.FitWidthZoom);
    }

    [Fact]
    public void FitHeightZoom_WithViewportHeightOfExactlyOne_IsUnknown()
    {
        var state = new ViewerState();
        state.SetSourceSize(1000, 1000);
        state.MaxImageHeight = 1.0;

        Assert.Equal(0, state.FitHeightZoom);
    }

    [Fact]
    public void FitWidthZoom_WithInfiniteWidthButFiniteHeight_IsUnknownAndHeightZoomStillWorks()
    {
        var state = new ViewerState();
        state.SetSourceSize(1000, 500);
        state.MaxImageHeight = 250;

        Assert.Equal(0, state.FitWidthZoom);
        Assert.Equal(0.5, state.FitHeightZoom, 6);
    }

    [Theory]
    [InlineData(0.25)] // lowest plausible Windows scale is honoured, not replaced by 1.0
    [InlineData(16.0)] // and so is the highest
    public void DpiScale_AtTheEdgesOfThePlausibleRange_IsUsedAsReported(double dpi)
    {
        var state = new ViewerState();
        state.SetSourceSize(1000, 1000);
        state.UpdateViewport(500, 500);
        state.DpiScale = dpi;

        Assert.Equal(0.5 * dpi, state.FitZoom, 9);
        Assert.Equal((1000 / dpi, 1000 / dpi), ViewerState.CalculateDisplaySize(1000, 1000, 1.0, dpi));
    }

    [Theory]
    [InlineData(0.24)]
    [InlineData(16.01)]
    public void DpiScale_JustOutsideThePlausibleRange_ReadsAs100Percent(double dpi)
    {
        var state = new ViewerState();
        state.SetSourceSize(1000, 1000);
        state.UpdateViewport(500, 500);
        state.DpiScale = dpi;

        Assert.Equal(0.5, state.FitZoom, 9);
    }

    [Fact]
    public void SwapSourceSize_WhenOnlyTheHeightChanges_TakesTheNewSize()
    {
        var state = new ViewerState();
        state.SetSourceSize(100, 100);
        state.SetZoom(2.0);

        state.SwapSourceSize(100, 120);

        Assert.Equal(100, state.SourcePixelWidth);
        Assert.Equal(120, state.SourcePixelHeight);
    }

    [Fact]
    public void SwapSourceSize_WhenOnlyTheWidthChanges_TakesTheNewSize()
    {
        var state = new ViewerState();
        state.SetSourceSize(100, 100);
        state.SetZoom(2.0);

        state.SwapSourceSize(120, 100);

        Assert.Equal(120, state.SourcePixelWidth);
        Assert.Equal(100, state.SourcePixelHeight);
    }

    [Theory]
    [InlineData(0, 50, 100, 100)] // the shown image's size was unknown
    [InlineData(50, 0, 100, 100)]
    [InlineData(50, 50, 0, 100)] // the replacement reports no usable size
    [InlineData(50, 50, 100, 0)]
    public void SwapSourceSize_WithAnUnknownSizeOnEitherSide_JustSetsTheSizeWithoutAnchoringTheScroll(
        int oldWidth, int oldHeight, int newWidth, int newHeight)
    {
        var state = new ViewerState();
        state.SetSourceSize(oldWidth, oldHeight);
        state.SetZoom(2.0);
        var swapping = 0;
        state.SourceSizeSwapping += (_, _) => swapping++;

        state.SwapSourceSize(newWidth, newHeight);

        Assert.Equal(0, swapping);
        Assert.Equal(newWidth, state.SourcePixelWidth);
        Assert.Equal(newHeight, state.SourcePixelHeight);
    }

    [Fact]
    public void SwapSourceSize_InFit_JustSetsTheSizeWithoutAnchoringTheScroll()
    {
        var state = Fitted(50, 50, 800, 600);
        var swapping = 0;
        state.SourceSizeSwapping += (_, _) => swapping++;

        state.SwapSourceSize(60, 60);

        Assert.Equal(0, swapping);
        Assert.Equal(60, state.SourcePixelWidth);
    }

    [Fact]
    public void SwapSourceSize_WhileZoomed_AnnouncesTheSwapBeforeTheSizeChanges()
    {
        var state = new ViewerState();
        state.SetSourceSize(50, 50);
        state.SetZoom(2.0);
        var widthAtEvent = -1;
        state.SourceSizeSwapping += (_, _) => widthAtEvent = state.SourcePixelWidth;

        state.SwapSourceSize(60, 60);

        Assert.Equal(50, widthAtEvent);
        Assert.Equal(60, state.SourcePixelWidth);
    }

    // The Fit-width zoom remembers the viewport dimension it filled (1000 here) so a later swap refits against it; a
    // viewport reading that is not usable must not overwrite that memory.
    [Theory]
    [InlineData(1.0, 700.0)]
    [InlineData(1200.0, 1.0)]
    [InlineData(double.PositiveInfinity, 700.0)]
    [InlineData(1200.0, double.PositiveInfinity)]
    [InlineData(double.NaN, 700.0)]
    [InlineData(1200.0, double.NaN)]
    public void UpdateViewport_WhileFitWidth_IgnoresAnUnusableViewportWhenRememberingTheFilledDimension(double viewportWidth, double viewportHeight)
    {
        var state = Fitted(2000, 1000, 1000, 500);
        state.ZoomToFitWidth(); // zoom 0.5, remembers a 1000 DIP viewport width

        state.UpdateViewport(viewportWidth, viewportHeight);
        state.SwapSourceSize(2400, 1200);

        Assert.Equal(1000.0 / 2400, state.Zoom, 9);
    }

    [Fact]
    public void UpdateViewport_WhileFitWidth_RemembersTheNewUsableViewportWidth()
    {
        var state = Fitted(2000, 1000, 1000, 500);
        state.ZoomToFitWidth();

        state.UpdateViewport(1200, 700);
        state.SwapSourceSize(2400, 1200);

        Assert.Equal(1200.0 / 2400, state.Zoom, 9);
    }

    [Theory]
    [InlineData(1.0, 600.0)]
    [InlineData(800.0, 1.0)]
    [InlineData(double.PositiveInfinity, 600.0)]
    [InlineData(800.0, double.PositiveInfinity)]
    [InlineData(double.NaN, 600.0)]
    [InlineData(800.0, double.NaN)]
    public void UpdateViewport_Forced_KeepsTheLimitsWhenTheReadingIsNotUsable(double viewportWidth, double viewportHeight)
    {
        var state = new ViewerState();
        state.UpdateViewport(1000, 500, force: true);

        state.UpdateViewport(viewportWidth, viewportHeight, force: true);

        Assert.Equal(1000, state.MaxImageWidth);
        Assert.Equal(500, state.MaxImageHeight);
    }

    [Fact]
    public void CalculateStepZoom_AtTheTopEdgeTolerance_DoesNothing()
    {
        Assert.Null(ViewerState.CalculateStepZoom(ViewerState.MaxStepZoom - 0.0005, 0.25));
    }

    [Fact]
    public void CalculateStepZoom_AtTheBottomEdgeTolerance_DoesNothing()
    {
        Assert.Null(ViewerState.CalculateStepZoom(ViewerState.MinStepZoom + 0.0005, -0.25));
    }
}

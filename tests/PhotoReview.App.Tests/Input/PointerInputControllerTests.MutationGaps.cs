using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using PhotoReview.App.Input;

namespace PhotoReview.App.Tests.Input;

/// <summary>
/// Stryker round 1 (App): PointerInputController behaviours the existing tests left unpinned: the bitmap-swap
/// re-anchor, the can-the-image-pan thresholds, the scrollbar-correction boundary and the centre of the
/// click-zoom-level menu command. Driven through the same fake surface as the rest of the class.
/// </summary>
public sealed partial class PointerInputControllerTests
{
    private void ZoomedWithSource(int width = 2000, int height = 1000)
    {
        _viewer.SetSourceSize(width, height);
        _viewer.SetZoom(1.0);
        _surface.ExtentWidth = width;
        _surface.ExtentHeight = height;
        _surface.HorizontalOffset = 500;
        _surface.VerticalOffset = 200;
        _surface.OriginFollowsScroll = true;
    }

    [Fact]
    public void SourceSizeSwap_WhileZoomed_ReanchorsOnceAfterTheRenderPass()
    {
        ZoomedWithSource();

        _viewer.SwapSourceSize(2002, 1001);

        Assert.Equal(1, _surface.YieldCount);
        Assert.Single(_surface.Scrolls);
    }

    [Fact]
    public async Task SourceSizeSwap_AfterACompletedZoomGesture_StillReanchors()
    {
        ZoomedWithSource();
        await _controller.OnWheelAsync(120, ctrl: false, new Point(400, 300));
        var scrollsAfterZoom = _surface.Scrolls.Count;
        var yieldsAfterZoom = _surface.YieldCount;

        _viewer.SwapSourceSize(_viewer.SourcePixelWidth + 2, _viewer.SourcePixelHeight + 1);

        Assert.Equal(yieldsAfterZoom + 1, _surface.YieldCount);
        Assert.Equal(scrollsAfterZoom + 1, _surface.Scrolls.Count);
    }

    [Fact]
    public async Task SourceSizeSwap_WhileAZoomGestureAwaitsItsLayout_LeavesTheAnchoringToTheGesture()
    {
        ZoomedWithSource();
        _surface.HoldYields = true;
        var zoom = _controller.OnWheelAsync(120, ctrl: false, new Point(400, 300));

        _viewer.SwapSourceSize(_viewer.SourcePixelWidth + 2, _viewer.SourcePixelHeight + 1);
        _surface.ReleaseYields();
        await zoom;

        Assert.Equal(1, _surface.YieldCount);
        Assert.Single(_surface.Scrolls);
    }

    [Fact]
    public void SourceSizeSwap_ImageNotLoaded_DoesNothing()
    {
        ZoomedWithSource();
        _surface.IsLoaded = false;

        _viewer.SwapSourceSize(2002, 1001);

        Assert.Equal(0, _surface.YieldCount);
        Assert.Empty(_surface.Scrolls);
    }

    [Fact]
    public void SourceSizeSwap_ImageElementHasNoWidth_DoesNothing()
    {
        ZoomedWithSource();
        _surface.ExtentWidth = 0;

        _viewer.SwapSourceSize(2002, 1001);

        Assert.Equal(0, _surface.YieldCount);
        Assert.Empty(_surface.Scrolls);
    }

    [Fact]
    public void SourceSizeSwap_ImageElementHasNoHeight_DoesNothing()
    {
        ZoomedWithSource();
        _surface.ExtentHeight = 0;

        _viewer.SwapSourceSize(2002, 1001);

        Assert.Equal(0, _surface.YieldCount);
        Assert.Empty(_surface.Scrolls);
    }

    [Fact]
    public void SourceSizeSwap_ASeparateOperationStartsBeforeTheRenderPass_DropsTheReanchor()
    {
        ZoomedWithSource();
        _surface.HoldYields = true;

        _viewer.SwapSourceSize(2002, 1001);
        _version.Next();
        _surface.ReleaseYields();

        Assert.Empty(_surface.Scrolls);
    }

    [Theory]
    [InlineData(800.0, 600.0, false)]
    [InlineData(800.5, 600.0, false)]
    [InlineData(800.0, 600.5, false)]
    [InlineData(800.6, 600.0, true)]
    [InlineData(800.0, 600.6, true)]
    [InlineData(900.0, 600.0, true)]
    [InlineData(800.0, 700.0, true)]
    public void Press_ZoomedImageWithClickToZoomOff_IsTakenOnlyWhenTheExtentExceedsTheViewportByMoreThanHalfADip(
        double extentWidth, double extentHeight, bool expectedTaken)
    {
        _settings.ClickToZoomEnabled = false;
        _viewer.SetZoom(2.0);
        _surface.ExtentWidth = extentWidth;
        _surface.ExtentHeight = extentHeight;

        var taken = _controller.OnImagePress(MouseButton.Left, 1, new Point(400, 300), timestamp: 1000);

        Assert.Equal(expectedTaken, taken);
        Assert.Equal(expectedTaken, _surface.PanCursor);
    }

    [Fact]
    public void Press_FitImageWithAnOversizedExtentAndClickToZoomOff_IsNotTaken()
    {
        _settings.ClickToZoomEnabled = false;
        Assert.True(_viewer.IsFit);
        _surface.ExtentWidth = 4000;
        _surface.ExtentHeight = 3000;

        var taken = _controller.OnImagePress(MouseButton.Left, 1, new Point(400, 300), timestamp: 1000);

        Assert.False(taken);
        Assert.False(_surface.PanCursor);
    }

    [Fact]
    public async Task FitWidthAsync_OverflowOfExactlyHalfADip_DoesNotReapply()
    {
        _viewer.SetSourceSize(2000, 4000);
        _viewer.DpiScale = 1.0;
        _surface.ExtentWidth = 800.5;

        await _controller.FitWidthAsync();

        Assert.Equal(1, _surface.YieldCount);
    }

    [Fact]
    public async Task FitHeightAsync_OverflowOfExactlyHalfADip_DoesNotReapply()
    {
        _viewer.SetSourceSize(4000, 2000);
        _viewer.DpiScale = 1.0;
        _surface.ExtentHeight = 600.5;

        await _controller.FitHeightAsync();

        Assert.Equal(1, _surface.YieldCount);
    }

    [Fact]
    public async Task SetClickZoomLevelAsync_AnchorsAtTheViewportCentre()
    {
        _surface.ViewportWidth = 1000;
        _surface.ViewportHeight = 400;

        await _controller.SetClickZoomLevelAsync(200);

        Assert.Equal(new Point(500, 200), Assert.Single(_surface.ToImageElementCalls));
    }

    private static double GlideDistanceOfOneDownArrow(double startOffset)
    {
        var test = new PointerInputControllerTests();
        test._viewer.SetZoom(2.0);
        test._surface.ExtentWidth = 800;
        test._surface.ExtentHeight = 4000;
        test._surface.VerticalOffset = startOffset;
        Assert.True(test._controller.TryPanByArrow(Key.Down, isRepeat: false));
        var handler = Assert.IsType<EventHandler>(test._surface.RenderHandler);
        for (var t = 0; t < 400 && test._surface.RenderHandler is not null; t += 16)
            handler(null, new FrameArgs(TimeSpan.FromMilliseconds(t)));
        return test._surface.VerticalOffset - startOffset;
    }

    [Fact]
    public void Arrow_KineticGlide_TravelsTheSameDistanceFromAnyStartingOffset()
    {
        var fromTop = GlideDistanceOfOneDownArrow(0);
        var fromMiddle = GlideDistanceOfOneDownArrow(1000);

        Assert.True(fromTop > 0);
        Assert.Equal(fromTop, fromMiddle, 6);
    }
}

using PhotoReview.App.Input;
using PhotoReview.Core.Model;
using Xunit;

namespace PhotoReview.App.Tests.Input;

/// <summary>feat/mouse-zoom: wheel mode / notch accumulation, click-vs-drag, click-to-zoom target, zoom-to-point.</summary>
public sealed class MouseGesturesTests
{
    // ---- wheel ----

    [Theory]
    [InlineData(120)]
    [InlineData(-120)]
    [InlineData(15)] // zoom mode: every event is one step, as before the setting existed
    public void Wheel_ZoomMode_EveryEventZooms(int delta)
    {
        var wheel = new WheelGestureInterpreter();

        Assert.Equal(WheelOutcomeKind.Zoom, wheel.Handle(delta, ctrlPressed: false, MouseWheelAction.Zoom));
    }

    [Theory]
    [InlineData(MouseWheelAction.Zoom)]
    [InlineData(MouseWheelAction.Navigate)]
    public void Wheel_CtrlAlwaysZooms(MouseWheelAction mode)
    {
        var wheel = new WheelGestureInterpreter();

        Assert.Equal(WheelOutcomeKind.Zoom, wheel.Handle(-120, ctrlPressed: true, mode));
    }

    [Fact]
    public void Wheel_NavigateMode_DownIsNextUpIsPrevious()
    {
        var wheel = new WheelGestureInterpreter();

        Assert.Equal(WheelOutcomeKind.Next, wheel.Handle(-120, false, MouseWheelAction.Navigate));
        Assert.Equal(WheelOutcomeKind.Previous, wheel.Handle(120, false, MouseWheelAction.Navigate));
    }

    [Fact]
    public void Wheel_NavigateMode_HighResolutionDeltasAccumulateToOneNotch()
    {
        var wheel = new WheelGestureInterpreter();

        for (var i = 0; i < 3; i++)
            Assert.Equal(WheelOutcomeKind.None, wheel.Handle(-30, false, MouseWheelAction.Navigate));
        Assert.Equal(-90, wheel.Accumulated);
        Assert.Equal(WheelOutcomeKind.Next, wheel.Handle(-30, false, MouseWheelAction.Navigate));
        Assert.Equal(0, wheel.Accumulated);
        Assert.Equal(WheelOutcomeKind.None, wheel.Handle(-30, false, MouseWheelAction.Navigate));
    }

    [Fact]
    public void Wheel_NavigateMode_ReversingDropsThePartialNotch()
    {
        var wheel = new WheelGestureInterpreter();

        Assert.Equal(WheelOutcomeKind.None, wheel.Handle(-100, false, MouseWheelAction.Navigate));
        Assert.Equal(WheelOutcomeKind.None, wheel.Handle(40, false, MouseWheelAction.Navigate));
        Assert.Equal(40, wheel.Accumulated);
        Assert.Equal(WheelOutcomeKind.Previous, wheel.Handle(80, false, MouseWheelAction.Navigate));
    }

    [Fact]
    public void Wheel_NavigateMode_CoarseEventMovesOneImageAndDropsTheExcess()
    {
        var wheel = new WheelGestureInterpreter();

        Assert.Equal(WheelOutcomeKind.Next, wheel.Handle(-360, false, MouseWheelAction.Navigate));
        Assert.Equal(0, wheel.Accumulated);
        Assert.Equal(WheelOutcomeKind.None, wheel.Handle(-60, false, MouseWheelAction.Navigate));
    }

    [Fact]
    public void Wheel_CtrlZoomClearsAPartialNavigationNotch()
    {
        var wheel = new WheelGestureInterpreter();
        wheel.Handle(-100, false, MouseWheelAction.Navigate);

        wheel.Handle(-120, true, MouseWheelAction.Navigate);

        Assert.Equal(WheelOutcomeKind.None, wheel.Handle(-40, false, MouseWheelAction.Navigate));
    }

    // ---- click vs drag ----

    [Fact]
    public void Release_WithoutDrag_IsAClickZoom()
    {
        Assert.Equal(PointerReleaseAction.ClickZoom,
            PointerGestures.ClassifyRelease(dragged: false, panned: false, pressWasConsumed: false, clickToZoomEnabled: true, kineticPanEnabled: true));
        Assert.Equal(PointerReleaseAction.ClickZoom,
            PointerGestures.ClassifyRelease(dragged: false, panned: true, pressWasConsumed: false, clickToZoomEnabled: true, kineticPanEnabled: true));
    }

    [Theory]
    [InlineData(true, PointerReleaseAction.StartKinetic)]
    [InlineData(false, PointerReleaseAction.None)]
    public void Release_AfterDragPan_NeverTogglesZoom(bool kinetic, PointerReleaseAction expected)
    {
        Assert.Equal(expected,
            PointerGestures.ClassifyRelease(dragged: true, panned: true, pressWasConsumed: false, clickToZoomEnabled: true, kineticPanEnabled: kinetic));
    }

    [Fact]
    public void Release_DragWithoutPan_DoesNothing()
    {
        // In Fit nothing scrolls: a drag is neither a click nor a glide.
        Assert.Equal(PointerReleaseAction.None,
            PointerGestures.ClassifyRelease(dragged: true, panned: false, pressWasConsumed: false, clickToZoomEnabled: true, kineticPanEnabled: true));
    }

    [Fact]
    public void Release_PressThatStoppedAGlide_IsNotAClick()
    {
        Assert.Equal(PointerReleaseAction.None,
            PointerGestures.ClassifyRelease(dragged: false, panned: true, pressWasConsumed: true, clickToZoomEnabled: true, kineticPanEnabled: true));
    }

    [Fact]
    public void Release_ClickToZoomDisabled_IsNotAClick()
    {
        Assert.Equal(PointerReleaseAction.None,
            PointerGestures.ClassifyRelease(dragged: false, panned: false, pressWasConsumed: false, clickToZoomEnabled: false, kineticPanEnabled: true));
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    public void DoubleClickPress_IsFit(int clickCount, bool expected)
    {
        Assert.Equal(expected, PointerGestures.IsFitDoubleClick(clickCount));
    }

    // ---- click-to-zoom target ----

    [Theory]
    [InlineData(true, 1.0, 1.0, ClickZoomTarget.ClickZoom)]   // Fit (Zoom is 1.0 by convention) -> zoom in
    [InlineData(false, 2.0, 1.0, ClickZoomTarget.ClickZoom)]  // another zoom -> the click zoom
    [InlineData(false, 0.5, 1.0, ClickZoomTarget.ClickZoom)]
    [InlineData(false, 1.0, 1.0, ClickZoomTarget.Fit)]        // at the click zoom -> back to Fit
    [InlineData(false, 2.0, 2.0, ClickZoomTarget.Fit)]
    [InlineData(false, 1.0005, 1.0, ClickZoomTarget.Fit)]
    public void ClickZoom_TogglesBetweenFitAndTheClickZoom(bool isFit, double zoom, double clickZoom, ClickZoomTarget expected)
    {
        Assert.Equal(expected, PointerGestures.DecideClickZoom(isFit, zoom, clickZoom));
    }

    [Theory]
    [InlineData(100, 1.0)]
    [InlineData(250, 2.5)]
    [InlineData(5, 0.10)]
    [InlineData(2000, 8.0)]
    public void ClickZoomFactor_IsPercentOfSourcePixelsClamped(int percent, double expected)
    {
        Assert.Equal(expected, PointerGestures.ClickZoomFactor(percent), 6);
    }

    // ---- zoom to point ----

    [Fact]
    public void ZoomToPoint_FromFit_KeepsTheImagePointUnderTheCursor()
    {
        // Point at 25 % / 50 % of the image is under the cursor (300, 200). After zooming, the 4000 x 2000
        // image is laid out at the viewport origin (offsets still 0).
        var offsets = MainWindowHelpers.CalculateZoomToPointOffsets(
            new MainWindowHelpers.ZoomImagePoint(0.25, 0.5),
            imageLeft: 0, imageTop: 0, imageWidth: 4000, imageHeight: 2000,
            cursorX: 300, cursorY: 200,
            currentHorizontalOffset: 0, currentVerticalOffset: 0,
            extentWidth: 4000, extentHeight: 2000, viewportWidth: 800, viewportHeight: 600);

        Assert.Equal(700, offsets.Horizontal, 6);  // 0.25 * 4000 - 300
        Assert.Equal(800, offsets.Vertical, 6);    // 0.5 * 2000 - 200
    }

    [Fact]
    public void ZoomToPoint_FromScrolledZoom_AccountsForTheCurrentOffsetAndClamps()
    {
        // Already scrolled by (500, 100): the image origin sits at (-500, -100) in the viewport.
        var offsets = MainWindowHelpers.CalculateZoomToPointOffsets(
            new MainWindowHelpers.ZoomImagePoint(0.9, 0.05),
            imageLeft: -500, imageTop: -100, imageWidth: 3000, imageHeight: 1500,
            cursorX: 700, cursorY: 10,
            currentHorizontalOffset: 500, currentVerticalOffset: 100,
            extentWidth: 3000, extentHeight: 1500, viewportWidth: 800, viewportHeight: 600);

        Assert.Equal(2000, offsets.Horizontal, 6); // 0.9 * 3000 - 700
        Assert.Equal(65, offsets.Vertical, 6);     // 0.05 * 1500 - 10
    }

    [Fact]
    public void ZoomToPoint_SmallerThanViewport_StaysAtZero()
    {
        var offsets = MainWindowHelpers.CalculateZoomToPointOffsets(
            new MainWindowHelpers.ZoomImagePoint(0.5, 0.5),
            imageLeft: 250, imageTop: 200, imageWidth: 300, imageHeight: 200,
            cursorX: 400, cursorY: 300,
            currentHorizontalOffset: 0, currentVerticalOffset: 0,
            extentWidth: 800, extentHeight: 600, viewportWidth: 800, viewportHeight: 600);

        Assert.Equal(0, offsets.Horizontal);
        Assert.Equal(0, offsets.Vertical);
    }

    [Fact]
    public void ZoomToPoint_PastTheFarEdge_ClampsToTheMaximumOffset()
    {
        var offsets = MainWindowHelpers.CalculateZoomToPointOffsets(
            new MainWindowHelpers.ZoomImagePoint(1.0, 1.0),
            imageLeft: 0, imageTop: 0, imageWidth: 2000, imageHeight: 1000,
            cursorX: 0, cursorY: 0,
            currentHorizontalOffset: 0, currentVerticalOffset: 0,
            extentWidth: 2000, extentHeight: 1000, viewportWidth: 800, viewportHeight: 600);

        Assert.Equal(1200, offsets.Horizontal, 6);
        Assert.Equal(400, offsets.Vertical, 6);
    }
}

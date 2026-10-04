using PhotoReview.App.Input;

namespace PhotoReview.App.Tests.Input;

/// <summary>Stryker round 1 (App): the click-zoom tolerance is exclusive.</summary>
public sealed class MouseGesturesMutationGapTests
{
    [Fact]
    public void DecideClickZoom_ZoomDifferenceExactlyTheTolerance_ZoomsInInsteadOfReturningToFit()
    {
        // |0.001 - 0| == SameZoomTolerance: not "the same zoom".
        Assert.Equal(ClickZoomTarget.ClickZoom, PointerGestures.DecideClickZoom(isFit: false, currentZoom: PointerGestures.SameZoomTolerance, clickZoom: 0));
    }

    [Fact]
    public void DecideClickZoom_ZoomDifferenceJustInsideTheTolerance_ReturnsToFit()
    {
        Assert.Equal(ClickZoomTarget.Fit, PointerGestures.DecideClickZoom(isFit: false, currentZoom: 2.0005, clickZoom: 2.0));
    }
}

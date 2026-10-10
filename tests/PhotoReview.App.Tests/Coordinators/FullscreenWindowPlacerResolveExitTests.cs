using PhotoReview.App.Coordinators;
using PhotoReview.App.Windowing;

namespace PhotoReview.App.Tests.Coordinators;

/// <summary>
/// Review 2026-10-08 LOW: leaving fullscreen must not put a Normal window back on a monitor that is gone. Pure rect policy:
/// <see cref="FullscreenWindowPlacer.ResolveNormalExitRect"/> with injected work areas (no OS involved).
/// </summary>
public sealed class FullscreenWindowPlacerResolveExitTests
{
    private static readonly ScreenRect Main = new(0, 0, 1920, 1040);
    private static readonly ScreenRect Second = new(1920, 0, 3840, 1040);

    [Fact]
    public void Resolve_RectFullyInsideAWorkArea_IsReturnedExactly()
    {
        var saved = new ScreenRect(300, 200, 1100, 800);
        Assert.Equal(saved, FullscreenWindowPlacer.ResolveNormalExitRect(saved, [Main, Second]));
    }

    [Fact]
    public void Resolve_RectOnTheSecondMonitorThatStillExists_IsReturnedExactly()
    {
        var saved = new ScreenRect(2200, 100, 3000, 700);
        Assert.Equal(saved, FullscreenWindowPlacer.ResolveNormalExitRect(saved, [Main, Second]));
    }

    [Fact]
    public void Resolve_RectOnAMonitorThatDisappeared_MovesOntoTheRemainingMonitorKeepingItsSize()
    {
        var saved = new ScreenRect(2200, 100, 3000, 700); // lived on Second, which was unplugged
        var r = FullscreenWindowPlacer.ResolveNormalExitRect(saved, [Main]);
        Assert.Equal(new ScreenRect(1120, 100, 1920, 700), r); // 800x600 kept, pushed in to the nearest edge
    }

    [Fact]
    public void Resolve_RectLeftOfEverything_MovesToTheNearestMonitorsLeftEdge()
    {
        var saved = new ScreenRect(-4000, 50, -3200, 650);
        Assert.Equal(new ScreenRect(0, 50, 800, 650), FullscreenWindowPlacer.ResolveNormalExitRect(saved, [Main]));
    }

    [Fact]
    public void Resolve_RectBelowTheWorkArea_MovesUpOntoIt()
    {
        var saved = new ScreenRect(100, 1500, 900, 2100);
        Assert.Equal(new ScreenRect(100, 440, 900, 1040), FullscreenWindowPlacer.ResolveNormalExitRect(saved, [Main]));
    }

    [Fact]
    public void Resolve_MovesToTheNearestOfSeveralRemainingMonitors()
    {
        var third = new ScreenRect(-1920, 0, 0, 1040);
        var saved = new ScreenRect(4000, 100, 4800, 700); // right of Second
        var r = FullscreenWindowPlacer.ResolveNormalExitRect(saved, [Main, third, Second]);
        Assert.Equal(new ScreenRect(3040, 100, 3840, 700), r);
    }

    [Fact]
    public void Resolve_RectBiggerThanTheRemainingWorkArea_IsShrunkToFitIt()
    {
        var saved = new ScreenRect(3000, -200, 5000, 1500); // 2000x1700 on a vanished monitor
        Assert.Equal(new ScreenRect(0, 0, 1920, 1040), FullscreenWindowPlacer.ResolveNormalExitRect(saved, [Main]));
    }

    [Fact]
    public void Resolve_PartiallyVisibleWithTheCaptionReachable_IsKeptExactly()
    {
        // 300 px of the left part hangs over the left edge, the caption band is still 500 px wide inside the work area.
        var saved = new ScreenRect(-300, 100, 500, 700);
        Assert.Equal(saved, FullscreenWindowPlacer.ResolveNormalExitRect(saved, [Main]));
    }

    [Fact]
    public void Resolve_OnlyASliverShowsHorizontally_IsMovedBack()
    {
        // 50 px (< 120) of the window is inside the work area: nothing practical to grab, so it is moved in.
        var saved = new ScreenRect(-750, 100, 50, 700);
        Assert.Equal(new ScreenRect(0, 100, 800, 700), FullscreenWindowPlacer.ResolveNormalExitRect(saved, [Main]));
    }

    [Fact]
    public void Resolve_CaptionAboveTheWorkAreaWithOnlyTheBodyShowing_IsMovedDown()
    {
        // Top edge 400 px above the work area: the body shows but the caption cannot be reached to drag it.
        var saved = new ScreenRect(200, -400, 1000, 300);
        Assert.Equal(new ScreenRect(200, 0, 1000, 700), FullscreenWindowPlacer.ResolveNormalExitRect(saved, [Main]));
    }

    [Fact]
    public void Resolve_NoMonitorInformation_LeavesTheRectAlone()
    {
        var saved = new ScreenRect(2200, 100, 3000, 700);
        Assert.Equal(saved, FullscreenWindowPlacer.ResolveNormalExitRect(saved, []));
    }
}

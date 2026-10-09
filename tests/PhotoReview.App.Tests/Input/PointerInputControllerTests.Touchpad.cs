using System.Threading.Tasks;
using System.Windows;
using PhotoReview.App.Input;
using PhotoReview.Core.Model;

namespace PhotoReview.App.Tests.Input;

/// <summary>
/// Q-TOUCHPAD-REFRESH: touchpad two-finger swipe through the real controller and a fake surface -- pan when zoomed, distance-based
/// navigation at Fit (vertical only), pinch still zooms, a notched mouse wheel unchanged, and the feature switch.
/// </summary>
public sealed partial class PointerInputControllerTests
{
    private int _touchpadTime = 10_000;

    /// <summary>One touchpad message, 8 ms after the previous one (a continuous swipe).</summary>
    private Task TouchpadAsync(int delta, bool horizontal = false, bool ctrl = false)
    {
        _touchpadTime += 8;
        return _controller.OnWheelAsync(new WheelInput(delta, horizontal, ctrl, _touchpadTime), new Point(400, 300));
    }

    [Fact]
    public async Task Touchpad_AtFit_AVerticalSwipeChangesImagesByDistance_WithoutZooming()
    {
        _settings.TouchpadSwipeDistancePerImage = 200;

        for (var i = 0; i < 72; i++) await TouchpadAsync(-10); // -720: images at -100, -300, -500, -700

        Assert.Equal(4, _next);
        Assert.Equal(0, _previous);
        Assert.True(_viewer.IsFit);
        Assert.Empty(_surface.Scrolls);
    }

    [Fact]
    public async Task Touchpad_AtFit_SwipingUpGoesBack()
    {
        _settings.TouchpadSwipeDistancePerImage = 200;

        for (var i = 0; i < 10; i++) await TouchpadAsync(10);

        Assert.Equal(1, _previous);
        Assert.Equal(0, _next);
    }

    [Fact]
    public async Task Touchpad_AtFit_TheMouseWheelModeDoesNotMatter()
    {
        _settings.MouseWheelAction = MouseWheelAction.Navigate; // one image per 120 for the mouse, distance-based for the touchpad
        _settings.TouchpadSwipeDistancePerImage = 600;

        for (var i = 0; i < 24; i++) await TouchpadAsync(-10); // -240: two mouse notches, but below the touchpad's first step (300)

        Assert.Equal(0, _next);
    }

    [Fact]
    public async Task Touchpad_AtFit_ASidewaysSwipeIsIgnored()
    {
        for (var i = 0; i < 50; i++) await TouchpadAsync(-10, horizontal: true);

        Assert.Equal(0, _next + _previous);
        Assert.True(_viewer.IsFit);
        Assert.Empty(_surface.Scrolls);
    }

    [Fact]
    public async Task Touchpad_Zoomed_PansBothAxes_AndNeverNavigatesOrZooms()
    {
        ZoomInSoTheImageCanPan(); // offsets (500, 400), extent 4000 x 3000
        var zoom = _viewer.Zoom;

        await TouchpadAsync(-30); // swipe "down": content scrolls down (offset grows)
        Assert.Equal((500, 430), _surface.Scrolls[^1]);
        await TouchpadAsync(45, horizontal: true); // swipe right
        Assert.Equal((545, 430), _surface.Scrolls[^1]);
        await TouchpadAsync(20); // up
        Assert.Equal((545, 410), _surface.Scrolls[^1]);

        Assert.Equal(0, _next + _previous);
        Assert.Equal(zoom, _viewer.Zoom);
    }

    [Fact]
    public async Task Touchpad_Zoomed_PanIsClampedToTheScrollableRange()
    {
        ZoomInSoTheImageCanPan();

        await TouchpadAsync(5000); // far up

        Assert.Equal((500, 0), _surface.Scrolls[^1]);
    }

    [Fact]
    public async Task Touchpad_Pinch_CtrlStillZoomsAtTheCursor()
    {
        await TouchpadAsync(-7, ctrl: true);

        Assert.False(_viewer.IsFit);
        Assert.Equal(0, _next + _previous);
    }

    [Fact]
    public async Task MouseWheel_WholeNotches_KeepTheConfiguredZoom()
    {
        _settings.MouseWheelAction = MouseWheelAction.Zoom;

        await _controller.OnWheelAsync(new WheelInput(-120, false, false, 5000), new Point(400, 300));

        Assert.False(_viewer.IsFit);
        Assert.Equal(0, _next + _previous);
    }

    [Fact]
    public async Task MouseWheel_TiltSideways_IsIgnored()
    {
        await _controller.OnWheelAsync(new WheelInput(120, Horizontal: true, false, 5000), new Point(400, 300));

        Assert.True(_viewer.IsFit);
        Assert.Equal(0, _next + _previous);
        Assert.Empty(_surface.Scrolls);
    }

    [Fact]
    public async Task Touchpad_Disabled_APartialDeltaBehavesLikeBefore_ZoomMode()
    {
        _settings.TouchpadSwipeEnabled = false;
        _settings.MouseWheelAction = MouseWheelAction.Zoom;

        await TouchpadAsync(-10);

        Assert.False(_viewer.IsFit); // one zoom step per message, as before the feature
        Assert.Equal(0, _next);
    }

    [Fact]
    public async Task Touchpad_Disabled_NavigateModeUsesWholeNotches()
    {
        _settings.TouchpadSwipeEnabled = false;
        _settings.MouseWheelAction = MouseWheelAction.Navigate;

        for (var i = 0; i < 12; i++) await TouchpadAsync(-10); // -120 = one notch

        Assert.Equal(1, _next);
    }

    [Fact]
    public async Task Touchpad_TheOsHintMakesAWholeNotchASwipe()
    {
        _settings.TouchpadSwipeDistancePerImage = 200;

        await _controller.OnWheelAsync(new WheelInput(-120, false, false, 5000, WheelDeviceHint.Touchpad), new Point(400, 300));

        Assert.Equal(1, _next); // -120 >= the first step (100), and no zoom
        Assert.True(_viewer.IsFit);
    }

    [Fact]
    public async Task Touchpad_ImageChangesFromTheSwipe_DoNotRestartTheSwipeDistance()
    {
        // The window reports every navigation (OnCurrentIndexChanged); the swipe must keep counting from where it was, or the
        // second image would come after only half a step.
        _settings.TouchpadSwipeDistancePerImage = 200;
        for (var i = 0; i < 10; i++) await TouchpadAsync(-10);
        Assert.Equal(1, _next);
        _controller.OnCurrentIndexChanged(1);

        for (var i = 0; i < 19; i++) await TouchpadAsync(-10); // -190 since the first image: not yet

        Assert.Equal(1, _next);
        await TouchpadAsync(-10);
        Assert.Equal(2, _next);
    }
}

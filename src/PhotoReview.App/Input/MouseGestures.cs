using System;
using PhotoReview.Core.Model;

namespace PhotoReview.App.Input;

/// <summary>What one mouse-wheel event over the main image should do.</summary>
public enum WheelOutcomeKind
{
    /// <summary>Nothing yet (a high-resolution wheel has not reached a full notch).</summary>
    None = 0,

    /// <summary>One zoom step at the cursor; the sign of the event's delta gives the direction.</summary>
    Zoom = 1,

    /// <summary>Go to the next image (wheel down).</summary>
    Next = 2,

    /// <summary>Go to the previous image (wheel up).</summary>
    Previous = 3,
}

/// <summary>
/// feat/mouse-zoom: decides what a wheel event does from <see cref="MouseWheelAction"/> and Ctrl, and turns
/// high-resolution wheel deltas into whole notches for navigation.
/// </summary>
/// <remarks>
/// <para>Zoom (plain wheel in <see cref="MouseWheelAction.Zoom"/>, Ctrl+wheel in either mode) is unchanged from
/// before this setting existed: every event is one step, whatever its delta.</para>
/// <para>Navigation moves one image per notch (<see cref="NotchDelta"/> = 120, WHEEL_DELTA). Partial deltas from
/// smooth / high-resolution wheels accumulate until a notch is reached; reversing direction drops the partial
/// sum so a small back-and-forth never navigates. At most ONE image per event: a coarse event of 240+ (a very
/// fast spin reported as one message) moves one image and drops the excess, so the wheel can never queue a
/// burst of navigations faster than the normal Next/Previous path (and its preload pacing) runs them.</para>
/// </remarks>
internal sealed class WheelGestureInterpreter
{
    public const int NotchDelta = 120;

    private int _accumulated;

    /// <summary>Partial navigation delta carried to the next event (test seam).</summary>
    public int Accumulated => _accumulated;

    public WheelOutcomeKind Handle(int delta, bool ctrlPressed, MouseWheelAction mode)
    {
        if (ctrlPressed || mode != MouseWheelAction.Navigate)
        {
            _accumulated = 0;
            return delta == 0 ? WheelOutcomeKind.None : WheelOutcomeKind.Zoom;
        }

        if (delta == 0) return WheelOutcomeKind.None;
        if (_accumulated != 0 && Math.Sign(_accumulated) != Math.Sign(delta)) _accumulated = 0;
        _accumulated += delta;
        if (Math.Abs(_accumulated) < NotchDelta) return WheelOutcomeKind.None;

        var down = _accumulated < 0; // WPF: negative delta = wheel rotated toward the user = "down"
        _accumulated = 0;            // one image per event; any excess is dropped (no burst)
        return down ? WheelOutcomeKind.Next : WheelOutcomeKind.Previous;
    }

    /// <summary>Forget a partial notch (navigation by other means, mode change).</summary>
    public void Reset() => _accumulated = 0;
}

/// <summary>What releasing the left button over the main image should do.</summary>
public enum PointerReleaseAction
{
    None = 0,

    /// <summary>A click: toggle between Fit and the click zoom at the cursor.</summary>
    ClickZoom = 1,

    /// <summary>The end of a drag-pan: glide on with the release velocity.</summary>
    StartKinetic = 2,
}

/// <summary>Which zoom a click-to-zoom goes to.</summary>
public enum ClickZoomTarget
{
    /// <summary>Zoom to <c>ClickZoomPercent</c> keeping the point under the cursor.</summary>
    ClickZoom = 0,

    /// <summary>Back to Fit.</summary>
    Fit = 1,
}

/// <summary>
/// feat/mouse-zoom: pure click / drag / double-click decisions for the main image. The WPF handlers in
/// <c>MainWindow</c> only gather the inputs and apply the result.
/// </summary>
/// <remarks>
/// Click vs double-click (no timer, no waiting): a click acts on mouse-UP at once; the second press of a
/// double-click (ClickCount &gt;= 2) always applies Fit and swallows its own mouse-up. So from Fit a double-click
/// briefly zooms in on the first click and returns to Fit on the second; from any zoom it ends in Fit too
/// (DF03). The alternative -- delaying every single click by the system double-click time (~500 ms) to see if a
/// second one follows -- would make the common single click feel laggy, so it was not chosen.
/// </remarks>
internal static class PointerGestures
{
    /// <summary>Zooms closer than this (0.1 %) count as "already at the click zoom".</summary>
    public const double SameZoomTolerance = 0.001;

    /// <summary>A second (or later) press of a multi-click always means Fit (DF03).</summary>
    public static bool IsFitDoubleClick(int clickCount) => clickCount >= 2;

    /// <summary>
    /// Classifies a left-button release. <paramref name="dragged"/>: the pointer crossed the system drag
    /// threshold during the press. <paramref name="panned"/>: the press actually scrolled the image (pan was
    /// possible). <paramref name="pressWasConsumed"/>: the press only stopped a running glide, or was the second
    /// press of a double-click -- it never toggles zoom.
    /// </summary>
    public static PointerReleaseAction ClassifyRelease(
        bool dragged,
        bool panned,
        bool pressWasConsumed,
        bool clickToZoomEnabled,
        bool kineticPanEnabled)
    {
        if (dragged)
            return panned && kineticPanEnabled ? PointerReleaseAction.StartKinetic : PointerReleaseAction.None;
        if (pressWasConsumed || !clickToZoomEnabled) return PointerReleaseAction.None;
        return PointerReleaseAction.ClickZoom;
    }

    /// <summary>In Fit, or at any zoom other than the click zoom, a click zooms to it; at the click zoom it returns to Fit.</summary>
    public static ClickZoomTarget DecideClickZoom(bool isFit, double currentZoom, double clickZoom) =>
        !isFit && Math.Abs(currentZoom - clickZoom) < SameZoomTolerance ? ClickZoomTarget.Fit : ClickZoomTarget.ClickZoom;

    /// <summary>Converts the <c>ClickZoomPercent</c> setting to an original-relative zoom factor (ADR 0008), clamped to 10 %..800 %.</summary>
    public static double ClickZoomFactor(int percent) =>
        Math.Clamp(percent, PhotoReview.Core.Settings.AppSettings.MinClickZoomPercent, PhotoReview.Core.Settings.AppSettings.MaxClickZoomPercent) / 100.0;
}

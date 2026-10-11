namespace PhotoReview.App.Input;

/// <summary>What Windows says produced the wheel message being handled (<c>GetCurrentInputMessageSource</c>).</summary>
public enum WheelDeviceHint
{
    /// <summary>Not reported (most wheel messages, including many from precision touchpads): decide from the deltas.</summary>
    Unknown = 0,

    /// <summary>The OS reported a touchpad as the source.</summary>
    Touchpad = 1,
}

/// <summary>One wheel message over the main image.</summary>
/// <param name="Delta">Wheel delta (WHEEL_DELTA = 120 per notch). Vertical: positive = up. Horizontal (WM_MOUSEHWHEEL): positive = right.</param>
/// <param name="Horizontal">True for WM_MOUSEHWHEEL (a sideways swipe or a tilted wheel).</param>
/// <param name="Ctrl">Ctrl held. A touchpad pinch arrives as Ctrl+wheel.</param>
/// <param name="Timestamp">Message time in milliseconds (Environment.TickCount clock; it wraps).</param>
/// <param name="Hint">The OS device hint for this message.</param>
internal readonly record struct WheelInput(int Delta, bool Horizontal, bool Ctrl, int Timestamp, WheelDeviceHint Hint = WheelDeviceHint.Unknown);

/// <summary>
/// Q-TOUCHPAD-REFRESH: tells a precision-touchpad two-finger swipe apart from an ordinary notched mouse wheel. Windows delivers
/// both as WM_MOUSEWHEEL/WM_MOUSEHWHEEL; a touchpad sends many small deltas that are not multiples of
/// <see cref="WheelGestureInterpreter.NotchDelta"/>, a classic wheel exactly one notch per detent.
/// </summary>
/// <remarks>
/// <para>A message is a touchpad one when the OS says so (<see cref="WheelDeviceHint.Touchpad"/>), when its delta is not a whole
/// number of notches, or when it follows a touchpad message within <see cref="GestureGapMs"/> (a swipe can contain an exact
/// 120 by chance; it must not flip that one message to the mouse behaviour). Otherwise it is a mouse wheel.</para>
/// <para>Known limit: a "smooth"/high-resolution mouse wheel that also reports partial notches is classified as a touchpad
/// (the messages are indistinguishable); the setting <c>TouchpadSwipeEnabled</c> turns the whole feature off.</para>
/// </remarks>
internal sealed class TouchpadWheelClassifier
{
    /// <summary>Messages closer together than this belong to one gesture (a touchpad sends one every ~8-16 ms while moving).</summary>
    public const int GestureGapMs = 250;

    private bool _inGesture;
    private int _lastTimestamp;

    public bool IsTouchpad(int delta, int timestamp, WheelDeviceHint hint)
    {
        var touchpad = hint == WheelDeviceHint.Touchpad
            || delta % WheelGestureInterpreter.NotchDelta != 0
            || (_inGesture && IsWithinGap(_lastTimestamp, timestamp));
        _inGesture = touchpad;
        if (touchpad) _lastTimestamp = timestamp;
        return touchpad;
    }

    /// <summary>The next message starts a new decision (no gesture in progress).</summary>
    public void Reset() => _inGesture = false;

    /// <summary>True when <paramref name="now"/> is at most <see cref="GestureGapMs"/> after <paramref name="last"/> (tick-count wrap safe).</summary>
    internal static bool IsWithinGap(int last, int now)
    {
        var elapsed = unchecked(now - last);
        return elapsed >= 0 && elapsed <= GestureGapMs;
    }
}

/// <summary>
/// Q-TOUCHPAD-REFRESH: turns the vertical deltas of a touchpad swipe at Fit into image changes by DISTANCE: the first image of a
/// swipe after half of <c>distancePerImage</c> (so a short, light swipe changes exactly one image), then one more image per full
/// <c>distancePerImage</c>. Pure; time is the message timestamp.
/// </summary>
/// <remarks>
/// <para>At most one image per message, and a message larger than a whole step leaves no queued remainder (a coarse burst never
/// becomes several navigations). Reversing direction, or a pause longer than <see cref="TouchpadWheelClassifier.GestureGapMs"/>,
/// starts a fresh swipe (back to the half-distance first step).</para>
/// <para>WPF wheel sign: a negative delta is "down" = the next image, as for the mouse wheel in Navigate mode.</para>
/// </remarks>
internal sealed class TouchpadSwipeNavigator
{
    /// <summary>The first image of a swipe changes after this fraction of the per-image distance.</summary>
    public const double FirstStepFraction = 0.5;

    private bool _active;
    private bool _stepped;
    private int _accumulated;
    private int _lastTimestamp;

    /// <summary>Total delta of the swipe in progress (diagnostics: lets the user's log calibrate the distance setting).</summary>
    public int GestureTotal { get; private set; }

    /// <summary>Images changed by the swipe in progress.</summary>
    public int GestureImages { get; private set; }

    public WheelOutcomeKind Handle(int delta, int timestamp, int distancePerImage)
    {
        var distance = Math.Max(1, distancePerImage);
        var fresh = !_active || !TouchpadWheelClassifier.IsWithinGap(_lastTimestamp, timestamp);
        _active = true;
        _lastTimestamp = timestamp;
        if (delta == 0) return WheelOutcomeKind.None;
        if (fresh || (_accumulated != 0 && Math.Sign(_accumulated) != Math.Sign(delta)))
        {
            _accumulated = 0;
            _stepped = false;
            GestureTotal = 0;
            GestureImages = 0;
        }

        _accumulated += delta;
        GestureTotal += delta;
        var required = _stepped ? distance : Math.Max(1, (int)Math.Round(distance * FirstStepFraction));
        if (Math.Abs(_accumulated) < required) return WheelOutcomeKind.None;

        var down = _accumulated < 0; // WPF: negative delta = "down" = next image
        var remainder = _accumulated - (Math.Sign(_accumulated) * required);
        _accumulated = Math.Abs(remainder) >= distance ? 0 : remainder; // one image per message, never a queued burst
        _stepped = true;
        GestureImages++;
        return down ? WheelOutcomeKind.Next : WheelOutcomeKind.Previous;
    }

    /// <summary>Forget the swipe in progress.</summary>
    public void Reset()
    {
        _active = false;
        _accumulated = 0;
        _stepped = false;
    }
}

/// <summary>What one touchpad wheel message does.</summary>
public enum TouchpadWheelAction
{
    /// <summary>Not a touchpad swipe: the mouse-wheel path (<see cref="WheelGestureInterpreter"/>) handles it.</summary>
    MouseWheel = 0,

    /// <summary>Ignored (a sideways swipe at Fit).</summary>
    Ignore = 1,

    /// <summary>Scroll the zoomed image.</summary>
    Pan = 2,

    /// <summary>Feed the vertical delta to <see cref="TouchpadSwipeNavigator"/>.</summary>
    Navigate = 3,
}

/// <summary>Q-TOUCHPAD-REFRESH: the pure routing rule for a touchpad wheel message.</summary>
internal static class TouchpadGestureRules
{
    /// <summary>
    /// Scroll distance, in device-independent pixels, per wheel-delta unit of a touchpad swipe on a zoomed image (120 units = one
    /// notch = 120 DIP, close to the ~100 px per notch browsers scroll).
    /// </summary>
    public const double PanDipPerDeltaUnit = 1.0;

    /// <summary>
    /// <paramref name="isTouchpad"/>: the classifier's verdict. <paramref name="canPan"/>: the image is zoomed and larger than the
    /// viewport. Ctrl (a pinch) always keeps the mouse/zoom path. A disabled feature keeps the mouse path for vertical messages and
    /// ignores horizontal ones (WPF never delivered those before this feature).
    /// </summary>
    public static TouchpadWheelAction Route(WheelInput input, bool enabled, bool isTouchpad, bool canPan)
    {
        if (input.Ctrl || !enabled || !isTouchpad)
            return input.Horizontal ? TouchpadWheelAction.Ignore : TouchpadWheelAction.MouseWheel;
        if (canPan) return TouchpadWheelAction.Pan;
        return input.Horizontal ? TouchpadWheelAction.Ignore : TouchpadWheelAction.Navigate;
    }

    /// <summary>
    /// The scroll-offset change for a touchpad pan: a vertical delta &gt; 0 (up) scrolls towards the top (offset decreases), a
    /// horizontal delta &gt; 0 (right) towards the right (offset increases) -- the same directions the OS applies everywhere else.
    /// </summary>
    public static (double Horizontal, double Vertical) PanOffsetDelta(WheelInput input) =>
        input.Horizontal
            ? (input.Delta * PanDipPerDeltaUnit, 0)
            : (0, -input.Delta * PanDipPerDeltaUnit);
}

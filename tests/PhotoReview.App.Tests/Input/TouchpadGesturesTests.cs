using PhotoReview.App.Input;

namespace PhotoReview.App.Tests.Input;

/// <summary>
/// Q-TOUCHPAD-REFRESH: the pure touchpad rules -- classifier (touchpad vs notched mouse wheel), the distance-based image
/// navigator (delta sequence -> image changes) and the routing/pan-direction rules. No hardware: sequences of deltas and
/// message timestamps stand in for a precision touchpad.
/// </summary>
[Trait("Category", "HotPath")]
public sealed class TouchpadGesturesTests
{
    // ---- classifier ----

    [Theory]
    [InlineData(120)]
    [InlineData(-120)]
    [InlineData(240)]
    [InlineData(-360)]
    public void Classifier_WholeNotches_AreAMouseWheel(int delta)
    {
        var classifier = new TouchpadWheelClassifier();

        Assert.False(classifier.IsTouchpad(delta, 1000, WheelDeviceHint.Unknown));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-7)]
    [InlineData(30)]
    [InlineData(-119)]
    [InlineData(121)]
    public void Classifier_PartialNotches_AreATouchpad(int delta)
    {
        var classifier = new TouchpadWheelClassifier();

        Assert.True(classifier.IsTouchpad(delta, 1000, WheelDeviceHint.Unknown));
    }

    [Fact]
    public void Classifier_TheOsTouchpadHint_WinsEvenForAWholeNotch()
    {
        var classifier = new TouchpadWheelClassifier();

        Assert.True(classifier.IsTouchpad(120, 1000, WheelDeviceHint.Touchpad));
    }

    [Fact]
    public void Classifier_AnExactNotchInsideATouchpadGesture_StaysTouchpad_UntilThePauseExceedsTheGap()
    {
        var classifier = new TouchpadWheelClassifier();
        Assert.True(classifier.IsTouchpad(-13, 1000, WheelDeviceHint.Unknown));

        Assert.True(classifier.IsTouchpad(-120, 1000 + TouchpadWheelClassifier.GestureGapMs, WheelDeviceHint.Unknown));
        // A pause longer than the gap: the next whole notch is the mouse again.
        Assert.False(classifier.IsTouchpad(-120, 1000 + (2 * TouchpadWheelClassifier.GestureGapMs) + 1, WheelDeviceHint.Unknown));
        // ...and the mouse message ended the gesture: a later notch close behind it is still the mouse.
        Assert.False(classifier.IsTouchpad(-120, 1000 + (2 * TouchpadWheelClassifier.GestureGapMs) + 2, WheelDeviceHint.Unknown));
    }

    [Fact]
    public void Classifier_Reset_EndsTheGesture()
    {
        var classifier = new TouchpadWheelClassifier();
        Assert.True(classifier.IsTouchpad(-13, 1000, WheelDeviceHint.Unknown));

        classifier.Reset();

        Assert.False(classifier.IsTouchpad(-120, 1001, WheelDeviceHint.Unknown));
    }

    [Theory]
    [InlineData(1000, 1000, true)]
    [InlineData(1000, 1250, true)]
    [InlineData(1000, 1251, false)]
    [InlineData(1000, 999, false)] // a timestamp going backwards is not "within the gap"
    [InlineData(int.MaxValue - 10, int.MinValue + 10, true)] // TickCount wrap: 21 ms later
    public void IsWithinGap_UsesWrapSafeElapsedTime(int last, int now, bool expected)
    {
        Assert.Equal(expected, TouchpadWheelClassifier.IsWithinGap(last, now));
    }

    // ---- navigator ----

    private static List<WheelOutcomeKind> Swipe(TouchpadSwipeNavigator navigator, int deltaPerMessage, int messages, int distance, int startTime = 1000, int stepMs = 8)
    {
        var outcomes = new List<WheelOutcomeKind>();
        for (var i = 0; i < messages; i++)
        {
            var outcome = navigator.Handle(deltaPerMessage, startTime + (i * stepMs), distance);
            if (outcome != WheelOutcomeKind.None) outcomes.Add(outcome);
        }
        return outcomes;
    }

    [Fact]
    public void Navigator_ALightSwipe_ChangesExactlyOneImage_AfterHalfTheDistance()
    {
        var navigator = new TouchpadSwipeNavigator();

        // 9 x -10 = -90: below half of 200 -> nothing.
        Assert.Empty(Swipe(navigator, -10, 9, distance: 200));
        // the 10th message reaches -100 = half -> the next image.
        Assert.Equal(WheelOutcomeKind.Next, navigator.Handle(-10, 1000 + (9 * 8), 200));
    }

    [Fact]
    public void Navigator_ALongSwipe_ChangesOneImagePerFullDistanceAfterTheFirst()
    {
        var navigator = new TouchpadSwipeNavigator();

        // -720 in total: first image at -100, then -300, -500, -700 -> 4 images (not 7, not a runaway).
        var outcomes = Swipe(navigator, -10, 72, distance: 200);

        Assert.Equal(4, outcomes.Count);
        Assert.All(outcomes, o => Assert.Equal(WheelOutcomeKind.Next, o));
        Assert.Equal(-720, navigator.GestureTotal);
        Assert.Equal(4, navigator.GestureImages);
    }

    [Fact]
    public void Navigator_SwipingUp_GoesToThePreviousImage()
    {
        var navigator = new TouchpadSwipeNavigator();

        var outcomes = Swipe(navigator, 10, 30, distance: 200); // +300: images at +100 and +300

        Assert.Equal([WheelOutcomeKind.Previous, WheelOutcomeKind.Previous], outcomes);
    }

    [Fact]
    public void Navigator_ABiggerDistanceSetting_NeedsALongerSwipe()
    {
        var navigator = new TouchpadSwipeNavigator();

        Assert.Single(Swipe(navigator, -10, 72, distance: 600)); // -720: only the first image (at -300); the second needs -900
    }

    [Fact]
    public void Navigator_OneHugeMessage_ChangesOneImageAndQueuesNothing()
    {
        var navigator = new TouchpadSwipeNavigator();

        Assert.Equal(WheelOutcomeKind.Next, navigator.Handle(-5000, 1000, 200));
        // The excess was dropped: the next small message of the same swipe does not fire another image.
        Assert.Equal(WheelOutcomeKind.None, navigator.Handle(-10, 1008, 200));
    }

    [Fact]
    public void Navigator_ARemainderBelowAFullStep_IsKeptForTheNextImage()
    {
        var navigator = new TouchpadSwipeNavigator();

        Assert.Equal(WheelOutcomeKind.Next, navigator.Handle(-150, 1000, 200)); // first step 100, remainder -50
        Assert.Equal(WheelOutcomeKind.None, navigator.Handle(-140, 1008, 200)); // -190 < 200
        Assert.Equal(WheelOutcomeKind.Next, navigator.Handle(-10, 1016, 200)); // -200 reached
    }

    [Fact]
    public void Navigator_ReversingDirection_StartsAFreshSwipe()
    {
        var navigator = new TouchpadSwipeNavigator();
        Assert.Equal(WheelOutcomeKind.Next, navigator.Handle(-100, 1000, 200));
        Assert.Equal(WheelOutcomeKind.None, navigator.Handle(-90, 1008, 200));

        // Reversed: the -90 partial is dropped and the fresh swipe's first image needs only half again.
        Assert.Equal(WheelOutcomeKind.None, navigator.Handle(90, 1016, 200));
        Assert.Equal(WheelOutcomeKind.Previous, navigator.Handle(10, 1024, 200));
    }

    [Fact]
    public void Navigator_APauseLongerThanTheGap_StartsAFreshSwipe()
    {
        var navigator = new TouchpadSwipeNavigator();
        Assert.Equal(WheelOutcomeKind.Next, navigator.Handle(-100, 1000, 200));
        Assert.Equal(WheelOutcomeKind.None, navigator.Handle(-90, 1008, 200));

        // After the pause the -90 is forgotten, and the first image of the new swipe needs half (100), not a full 200.
        Assert.Equal(WheelOutcomeKind.None, navigator.Handle(-90, 1008 + TouchpadWheelClassifier.GestureGapMs + 1, 200));
        Assert.Equal(WheelOutcomeKind.Next, navigator.Handle(-10, 1008 + TouchpadWheelClassifier.GestureGapMs + 9, 200));
    }

    [Fact]
    public void Navigator_WithinTheGap_ContinuesTheSameSwipe()
    {
        var navigator = new TouchpadSwipeNavigator();
        Assert.Equal(WheelOutcomeKind.Next, navigator.Handle(-100, 1000, 200));

        // Same swipe (exactly at the gap): the next image needs the full 200, not half.
        Assert.Equal(WheelOutcomeKind.None, navigator.Handle(-150, 1000 + TouchpadWheelClassifier.GestureGapMs, 200));
        Assert.Equal(WheelOutcomeKind.Next, navigator.Handle(-50, 1000 + TouchpadWheelClassifier.GestureGapMs + 8, 200));
    }

    [Fact]
    public void Navigator_Reset_ForgetsThePartialSwipe()
    {
        var navigator = new TouchpadSwipeNavigator();
        Assert.Equal(WheelOutcomeKind.None, navigator.Handle(-90, 1000, 200));

        navigator.Reset();

        Assert.Equal(WheelOutcomeKind.None, navigator.Handle(-90, 1008, 200)); // not -180
        Assert.Equal(WheelOutcomeKind.Next, navigator.Handle(-10, 1016, 200));
    }

    [Fact]
    public void Navigator_ZeroDelta_DoesNothing_AndANonPositiveDistanceIsTreatedAsOne()
    {
        var navigator = new TouchpadSwipeNavigator();
        Assert.Equal(WheelOutcomeKind.None, navigator.Handle(0, 1000, 200));
        Assert.Equal(WheelOutcomeKind.Next, navigator.Handle(-1, 1008, 0));
    }

    // ---- routing and pan direction ----

    [Theory]
    // ctrl (pinch): vertical -> mouse/zoom path, horizontal -> ignored
    [InlineData(false, true, true, true, false, TouchpadWheelAction.MouseWheel)]
    [InlineData(true, true, true, true, false, TouchpadWheelAction.Ignore)]
    // feature off
    [InlineData(false, false, false, true, true, TouchpadWheelAction.MouseWheel)]
    [InlineData(true, false, false, true, true, TouchpadWheelAction.Ignore)]
    // a notched mouse wheel
    [InlineData(false, false, true, false, true, TouchpadWheelAction.MouseWheel)]
    [InlineData(true, false, true, false, true, TouchpadWheelAction.Ignore)]
    // touchpad on a zoomed, pannable image: both axes pan
    [InlineData(false, false, true, true, true, TouchpadWheelAction.Pan)]
    [InlineData(true, false, true, true, true, TouchpadWheelAction.Pan)]
    // touchpad at Fit (or smaller): vertical navigates, horizontal is ignored
    [InlineData(false, false, true, true, false, TouchpadWheelAction.Navigate)]
    [InlineData(true, false, true, true, false, TouchpadWheelAction.Ignore)]
    public void Route_FollowsTheRules(bool horizontal, bool ctrl, bool enabled, bool isTouchpad, bool canPan, TouchpadWheelAction expected)
    {
        var input = new WheelInput(-30, horizontal, ctrl, 0);

        Assert.Equal(expected, TouchpadGestureRules.Route(input, enabled, isTouchpad, canPan));
    }

    [Fact]
    public void PanOffsetDelta_SwipeUpScrollsTowardsTheTop_SwipeRightTowardsTheRight()
    {
        Assert.Equal((0, -30 * TouchpadGestureRules.PanDipPerDeltaUnit), TouchpadGestureRules.PanOffsetDelta(new WheelInput(30, false, false, 0)));
        Assert.Equal((0, 30 * TouchpadGestureRules.PanDipPerDeltaUnit), TouchpadGestureRules.PanOffsetDelta(new WheelInput(-30, false, false, 0)));
        Assert.Equal((30 * TouchpadGestureRules.PanDipPerDeltaUnit, 0), TouchpadGestureRules.PanOffsetDelta(new WheelInput(30, true, false, 0)));
        Assert.Equal((-30 * TouchpadGestureRules.PanDipPerDeltaUnit, 0), TouchpadGestureRules.PanOffsetDelta(new WheelInput(-30, true, false, 0)));
    }

    // ---- wheel message decoding (WM_MOUSEHWHEEL packing) ----

    [Fact]
    public void WheelMessageSource_DecodesTheSignedDeltaAndTheScreenPoint()
    {
        Assert.Equal(-12, WheelMessageSource.Delta(new IntPtr(unchecked((ushort)-12) << 16 | 0x0008)));
        Assert.Equal(120, WheelMessageSource.Delta(new IntPtr(120 << 16)));
        var point = WheelMessageSource.ScreenPoint(new IntPtr((unchecked((ushort)-5) << 16) | 300));
        Assert.Equal(300, point.X);
        Assert.Equal(-5, point.Y); // a monitor left of / above the primary has negative coordinates
    }
}

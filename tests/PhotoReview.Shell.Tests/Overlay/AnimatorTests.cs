using PhotoReview.App.Input;
using PhotoReview.Shell.Win32.Overlay;

namespace PhotoReview.Shell.Tests.Overlay;

/// <summary>Đồng hồ khung giả: test tự phát tick với Timestamp chọn trước (1 tick = 1 ms khi ticksPerSecond = 1000).</summary>
internal sealed class FakeFrameClock : IFrameClock
{
    private EventHandler<FrameTickEventArgs>? _frame;

    public int RequestFrameCount { get; private set; }

    public int SubscriberCount => _frame?.GetInvocationList().Length ?? 0;

    public event EventHandler<FrameTickEventArgs>? Frame
    {
        add => _frame += value;
        remove => _frame -= value;
    }

    public void RequestFrame() => RequestFrameCount++;

    /// <summary>Phát một khung tại <paramref name="timestampMs"/> (chỉ khi có subscriber, như đồng hồ thật).</summary>
    public void Tick(long timestampMs) =>
        _frame?.Invoke(this, new FrameTickEventArgs(new FrameTick(timestampMs, 0, null)));
}

internal sealed class FakeOpacityTarget : IOpacityTarget
{
    public float Opacity { get; set; } = 1f;
}

/// <summary>
/// WP-17 (C-11 IAnimator): fade theo đồng hồ khung tiêm được - opacity đúng tại 0/50/100/200 ms, FadeTo mới huỷ tween cũ
/// (như FadeTargetGate), hết tween thì thôi nghe đồng hồ.
/// </summary>
[Trait("Category", "HotPath")]
public sealed class AnimatorTests
{
    private const long Ms = 1000; // ticksPerSecond của đồng hồ giả: 1 tick = 1 ms

    private static (Animator Animator, FakeFrameClock Clock, Dictionary<string, FakeOpacityTarget> Targets) Create()
    {
        var clock = new FakeFrameClock();
        var targets = new Dictionary<string, FakeOpacityTarget>
        {
            ["Toolbar"] = new(),
            ["Status"] = new(),
        };
        var animator = new Animator(clock, id => targets.GetValueOrDefault(id), Ms);
        return (animator, clock, targets);
    }

    [Fact]
    public void FadeOut_Linear200ms_HitsExpectedOpacityAt0_50_100_200()
    {
        (Animator animator, FakeFrameClock clock, var targets) = Create();
        FakeOpacityTarget toolbar = targets["Toolbar"];

        animator.FadeTo("Toolbar", 0f, TimeSpan.FromMilliseconds(200), Easing.Linear);
        Assert.True(animator.IsAnimating);

        clock.Tick(10_000);
        Assert.Equal(1f, toolbar.Opacity);
        clock.Tick(10_050);
        Assert.Equal(0.75f, toolbar.Opacity, 3);
        clock.Tick(10_100);
        Assert.Equal(0.5f, toolbar.Opacity, 3);
        clock.Tick(10_200);
        Assert.Equal(0f, toolbar.Opacity);
        Assert.False(animator.IsAnimating);
    }

    [Fact]
    public void FadeIn_150ms_QuadraticOut_FollowsEasing()
    {
        (Animator animator, FakeFrameClock clock, var targets) = Create();
        FakeOpacityTarget status = targets["Status"];
        status.Opacity = 0f;

        animator.FadeTo("Status", 1f, TimeSpan.FromMilliseconds(150), Easing.QuadraticOut);
        clock.Tick(0);
        clock.Tick(75); // t = 0,5 -> 1 - 0,25 = 0,75
        Assert.Equal(0.75f, status.Opacity, 3);
        clock.Tick(150);
        Assert.Equal(1f, status.Opacity);
    }

    [Fact]
    public void NewFadeTo_CancelsOldTween_AndStartsFromCurrentOpacity()
    {
        (Animator animator, FakeFrameClock clock, var targets) = Create();
        FakeOpacityTarget toolbar = targets["Toolbar"];
        animator.FadeTo("Toolbar", 0f, TimeSpan.FromMilliseconds(200), Easing.Linear);
        clock.Tick(0);
        clock.Tick(100);
        Assert.Equal(0.5f, toolbar.Opacity, 3);

        animator.FadeTo("Toolbar", 1f, TimeSpan.FromMilliseconds(100), Easing.Linear);
        Assert.Equal(1, animator.ActiveCount);
        clock.Tick(120);  // mốc 0 của tween mới
        Assert.Equal(0.5f, toolbar.Opacity, 3);
        clock.Tick(170);  // một nửa
        Assert.Equal(0.75f, toolbar.Opacity, 3);
        clock.Tick(220);
        Assert.Equal(1f, toolbar.Opacity);
        Assert.False(animator.IsAnimating);
        clock.Tick(400); // tween cũ không còn ghi đè
        Assert.Equal(1f, toolbar.Opacity);
    }

    [Fact]
    public void SameTarget_WhileRunning_DoesNotRestartTween()
    {
        (Animator animator, FakeFrameClock clock, var targets) = Create();
        FakeOpacityTarget toolbar = targets["Toolbar"];
        animator.FadeTo("Toolbar", 0f, TimeSpan.FromMilliseconds(200), Easing.Linear);
        clock.Tick(0);
        clock.Tick(100);
        int requests = clock.RequestFrameCount;

        animator.FadeTo("Toolbar", 0f, TimeSpan.FromMilliseconds(200), Easing.Linear); // FadeTargetGate: cùng đích -> bỏ qua
        clock.Tick(150);

        Assert.Equal(requests, clock.RequestFrameCount);
        Assert.Equal(0.25f, toolbar.Opacity, 3);
    }

    [Fact]
    public void AlreadyAtTarget_DoesNothingAndDoesNotListen()
    {
        (Animator animator, FakeFrameClock clock, _) = Create();

        animator.FadeTo("Toolbar", 1f, TimeSpan.FromMilliseconds(200), Easing.Linear);

        Assert.False(animator.IsAnimating);
        Assert.Equal(0, clock.SubscriberCount);
        Assert.Equal(0, clock.RequestFrameCount);
    }

    [Fact]
    public void ZeroDuration_AppliesImmediately()
    {
        (Animator animator, FakeFrameClock clock, var targets) = Create();

        animator.FadeTo("Toolbar", 0.25f, TimeSpan.Zero, Easing.Linear);

        Assert.Equal(0.25f, targets["Toolbar"].Opacity);
        Assert.False(animator.IsAnimating);
        Assert.Equal(0, clock.SubscriberCount);
    }

    [Fact]
    public void ZeroDuration_CancelsRunningTween()
    {
        (Animator animator, FakeFrameClock clock, var targets) = Create();
        animator.FadeTo("Toolbar", 0f, TimeSpan.FromMilliseconds(200), Easing.Linear);
        clock.Tick(0);
        clock.Tick(50);

        animator.FadeTo("Toolbar", 1f, TimeSpan.Zero, Easing.Linear);
        clock.Tick(100);

        Assert.Equal(1f, targets["Toolbar"].Opacity);
        Assert.False(animator.IsAnimating);
    }

    [Fact]
    public void StopsListeningToTheClock_WhenNoTweenIsLeft()
    {
        (Animator animator, FakeFrameClock clock, _) = Create();
        animator.FadeTo("Toolbar", 0f, TimeSpan.FromMilliseconds(100), Easing.Linear);
        Assert.Equal(1, clock.SubscriberCount);
        Assert.Equal(1, clock.RequestFrameCount);

        clock.Tick(0);
        clock.Tick(100);

        Assert.Equal(0, clock.SubscriberCount);
    }

    [Fact]
    public void TwoElements_AnimateIndependently()
    {
        (Animator animator, FakeFrameClock clock, var targets) = Create();
        animator.FadeTo("Toolbar", 0f, TimeSpan.FromMilliseconds(100), Easing.Linear);
        animator.FadeTo("Status", 0.5f, TimeSpan.FromMilliseconds(200), Easing.Linear);

        clock.Tick(0);
        clock.Tick(100);

        Assert.Equal(0f, targets["Toolbar"].Opacity);
        Assert.Equal(0.75f, targets["Status"].Opacity, 3);
        Assert.Equal(1, animator.ActiveCount);
    }

    [Fact]
    public void UnknownElement_IsIgnored()
    {
        (Animator animator, FakeFrameClock clock, _) = Create();

        animator.FadeTo("Nope", 0f, TimeSpan.FromMilliseconds(100), Easing.Linear);

        Assert.False(animator.IsAnimating);
        Assert.Equal(0, clock.SubscriberCount);
    }

    [Fact]
    public void ElementRemovedMidTween_EndsTheTween()
    {
        var clock = new FakeFrameClock();
        FakeOpacityTarget? target = new();
        var animator = new Animator(clock, _ => target, Ms);
        animator.FadeTo("x", 0f, TimeSpan.FromMilliseconds(100), Easing.Linear);
        clock.Tick(0);

        target = null;
        clock.Tick(50);

        Assert.False(animator.IsAnimating);
        Assert.Equal(0, clock.SubscriberCount);
    }

    [Fact]
    public void Changed_FiresOncePerFrameWithAChange()
    {
        (Animator animator, FakeFrameClock clock, _) = Create();
        int changed = 0;
        animator.Changed += (_, _) => changed++;
        animator.FadeTo("Toolbar", 0f, TimeSpan.FromMilliseconds(100), Easing.Linear);

        clock.Tick(0);   // opacity không đổi ở mốc 0
        Assert.Equal(0, changed);
        clock.Tick(50);
        clock.Tick(100);

        Assert.Equal(2, changed);
    }

    [Fact]
    public void TargetIsClamped_AndNaNMeansZero()
    {
        (Animator animator, FakeFrameClock clock, var targets) = Create();

        animator.FadeTo("Toolbar", 5f, TimeSpan.Zero, Easing.Linear);
        Assert.Equal(1f, targets["Toolbar"].Opacity);
        animator.FadeTo("Toolbar", float.NaN, TimeSpan.Zero, Easing.Linear);
        Assert.Equal(0f, targets["Toolbar"].Opacity);
        animator.FadeTo("Toolbar", -1f, TimeSpan.FromMilliseconds(10), Easing.Linear);
        Assert.False(animator.IsAnimating);
        Assert.Equal(0, clock.SubscriberCount);
    }

    [Fact]
    public void TickBeforeStart_ClampsToStartValue_AndRealStopwatchFrequencyIsDefault()
    {
        var clock = new FakeFrameClock();
        var target = new FakeOpacityTarget();
        var animator = new Animator(clock, _ => target); // Stopwatch.Frequency
        animator.FadeTo("x", 0f, TimeSpan.FromMilliseconds(200), Easing.Linear);
        long f = System.Diagnostics.Stopwatch.Frequency;

        clock.Tick(1_000_000);
        clock.Tick(1_000_000 - 5);                      // thời gian lùi: không vượt quá giá trị đầu
        Assert.Equal(1f, target.Opacity);
        clock.Tick(1_000_000 + (f / 10));               // 100 ms
        Assert.Equal(0.5f, target.Opacity, 2);
        clock.Tick(1_000_000 + (f / 5));                // 200 ms
        Assert.Equal(0f, target.Opacity);
    }

    [Fact]
    public void Dispose_StopsListeningAndRejectsNewFades()
    {
        (Animator animator, FakeFrameClock clock, _) = Create();
        animator.FadeTo("Toolbar", 0f, TimeSpan.FromMilliseconds(100), Easing.Linear);

        animator.Dispose();

        Assert.Equal(0, clock.SubscriberCount);
        Assert.False(animator.IsAnimating);
        Assert.Throws<ObjectDisposedException>(() => animator.FadeTo("Toolbar", 0f, TimeSpan.FromMilliseconds(1), Easing.Linear));
    }

    [Theory]
    [InlineData(Easing.Linear, 0.25, 0.25)]
    [InlineData(Easing.Linear, 1.0, 1.0)]
    [InlineData(Easing.QuadraticOut, 0.0, 0.0)]
    [InlineData(Easing.QuadraticOut, 0.5, 0.75)]
    [InlineData(Easing.QuadraticOut, 1.0, 1.0)]
    public void Ease_Values(Easing easing, double t, double expected) =>
        Assert.Equal(expected, Animator.Ease(easing, t), 6);

    [Fact]
    public void FadeEnd_LandsExactlyOnTarget_NotOnAFloatRoundedInterpolation()
    {
        // Mốc kết thúc phải gán ĐÚNG đích: nội suy 0,02 + (0,1 - 0,02) làm tròn float lệch 1 ulp so với 0,1.
        (Animator animator, FakeFrameClock clock, var targets) = Create();
        FakeOpacityTarget toolbar = targets["Toolbar"];
        toolbar.Opacity = 0.02f;
        animator.FadeTo("Toolbar", 0.1f, TimeSpan.FromMilliseconds(100), Easing.Linear);

        clock.Tick(0);
        clock.Tick(100);

        Assert.Equal(0.1f, toolbar.Opacity);
        Assert.False(animator.IsAnimating);
    }
}

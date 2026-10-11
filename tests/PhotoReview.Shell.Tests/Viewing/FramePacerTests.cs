using PhotoReview.Shell.Win32.Viewing;

namespace PhotoReview.Shell.Tests.Viewing;

/// <summary>
/// WP-21: chính sách nhịp khung. Cửa sổ thường = Immediate (Present(0): trên máy dev Present(1) chặn P50 222 ms, Present(0) 1,2 ms);
/// phủ cả màn hình = VSync có chờ waitable không chặn; VSync mà vẫn chặn thì hạ cấp vĩnh viễn.
/// </summary>
[Trait("Category", "HotPath")]
public sealed class FramePacerTests
{
    [Theory]
    [InlineData(false, false, false)]   // cửa sổ thường
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]     // khung ảnh đầu: Present(1) chặn 250-500 ms sau khi bỏ cloak (WP-20)
    [InlineData(true, false, true)]     // F11
    public void ModeFor_IsImmediateExceptWhenFullscreenAndTheFirstImageIsDrawn(bool fullscreen, bool awaitingFirstImage, bool expectVSync)
    {
        var expected = expectVSync ? PresentMode.VSync : PresentMode.Immediate;
        Assert.Equal(expected, new FramePacer().ModeFor(fullscreen, awaitingFirstImage));
    }

    [Fact]
    public void ReadySwapChain_IsNeverDeferred()
    {
        var pacer = new FramePacer();

        Assert.False(pacer.ShouldDefer(swapChainReady: true));
        Assert.Equal(0, pacer.DeferredTotal);
    }

    [Fact]
    public void BusySwapChain_DefersAtMostThreeFramesInARowThenDrawsAnyway()
    {
        var pacer = new FramePacer();

        Assert.True(pacer.ShouldDefer(false));
        Assert.True(pacer.ShouldDefer(false));
        Assert.True(pacer.ShouldDefer(false));
        Assert.False(pacer.ShouldDefer(false));   // cửa sổ bị che không bao giờ báo sẵn sàng: vẽ luôn, không đói
        Assert.True(pacer.ShouldDefer(false));    // chu kỳ mới
        Assert.Equal(4, pacer.DeferredTotal);
    }

    [Fact]
    public void ReadyFrame_ResetsTheDeferralStreak()
    {
        var pacer = new FramePacer();
        Assert.True(pacer.ShouldDefer(false));
        Assert.True(pacer.ShouldDefer(false));

        Assert.False(pacer.ShouldDefer(true));
        Assert.True(pacer.ShouldDefer(false));
        Assert.True(pacer.ShouldDefer(false));
        Assert.True(pacer.ShouldDefer(false));
        Assert.False(pacer.ShouldDefer(false));
    }

    [Fact]
    public void VSyncThatBlocksTwiceInARow_DegradesToImmediateForTheSession()
    {
        var pacer = new FramePacer();
        var raised = 0;
        pacer.Degraded += (_, _) => raised++;

        pacer.ObservePresent(PresentMode.VSync, FramePacer.BlockThresholdMs);
        Assert.False(pacer.IsDegraded);
        pacer.ObservePresent(PresentMode.VSync, 300);

        Assert.True(pacer.IsDegraded);
        Assert.Equal(1, raised);
        Assert.Equal(PresentMode.Immediate, pacer.ModeFor(fullscreen: true, awaitingFirstImage: false));
        pacer.ObservePresent(PresentMode.VSync, 500);
        Assert.Equal(1, raised);                   // chỉ báo một lần
    }

    [Fact]
    public void AFastFrame_BreaksTheBlockedStreak()
    {
        var pacer = new FramePacer();

        pacer.ObservePresent(PresentMode.VSync, 300);
        pacer.ObservePresent(PresentMode.VSync, FramePacer.BlockThresholdMs - 0.1);
        pacer.ObservePresent(PresentMode.VSync, 300);

        Assert.False(pacer.IsDegraded);
    }

    [Fact]
    public void ImmediateFrames_NeverCountTowardsDegrading()
    {
        var pacer = new FramePacer();

        pacer.ObservePresent(PresentMode.VSync, 300);
        pacer.ObservePresent(PresentMode.Immediate, 900);   // đo ở Immediate không liên quan tới VSync
        pacer.ObservePresent(PresentMode.VSync, 300);

        Assert.False(pacer.IsDegraded);
    }
}

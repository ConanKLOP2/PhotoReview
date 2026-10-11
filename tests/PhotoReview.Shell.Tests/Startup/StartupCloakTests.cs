using PhotoReview.Core.Abstractions;
using PhotoReview.Shell.Win32.Startup;

namespace PhotoReview.Shell.Tests.Startup;

[Trait("Category", "HotPath")]
public sealed class StartupCloakTests
{
    private sealed class InlineDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;

        public void Post(Action action) => action();

        public void Post(Action action, UiPriority priority) => action();

        public Task InvokeAsync(Action action)
        {
            action();
            return Task.CompletedTask;
        }

        public Task InvokeAsync(Action action, UiPriority priority) => InvokeAsync(action);

        public ValueTask YieldAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask YieldAsync(UiPriority priority, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private sealed class Recorder
    {
        public List<(nint Hwnd, bool Cloaked)> Calls { get; } = [];

        public bool Accept { get; set; } = true;

        public bool Set(nint hwnd, bool cloaked)
        {
            lock (Calls)
            {
                Calls.Add((hwnd, cloaked));
            }

            return Accept;
        }
    }

    [Fact]
    public void Hide_CloaksTheWindowOnceAndKeepsItHiddenUntilTheFirstPresent()
    {
        var recorder = new Recorder();
        using var cloak = new StartupCloak(new InlineDispatcher(), setCloaked: recorder.Set);

        cloak.Hide(42);
        cloak.Hide(42);

        Assert.True(cloak.IsHidden);
        Assert.Equal([((nint)42, true)], recorder.Calls);
    }

    [Fact]
    public void NoteFirstPresent_UncloaksOnceAndReportsTheReveal()
    {
        var recorder = new Recorder();
        var revealed = 0;
        using var cloak = new StartupCloak(new InlineDispatcher(), onRevealed: () => revealed++, setCloaked: recorder.Set);
        cloak.Hide(42);

        cloak.NoteFirstPresent();
        cloak.NoteFirstPresent();

        Assert.False(cloak.IsHidden);
        Assert.True(cloak.IsRevealed);
        Assert.Equal(1, revealed);
        Assert.Equal([((nint)42, true), ((nint)42, false)], recorder.Calls);
    }

    [Fact]
    public void Hide_WhenDwmRefuses_LeavesTheWindowVisibleAndNeverReports()
    {
        var recorder = new Recorder { Accept = false };
        var revealed = 0;
        using var cloak = new StartupCloak(new InlineDispatcher(), onRevealed: () => revealed++, setCloaked: recorder.Set);

        cloak.Hide(42);
        cloak.NoteFirstPresent();

        Assert.False(cloak.IsHidden);
        Assert.Equal(0, revealed);
        Assert.Single(recorder.Calls); // không có lời gọi uncloak thừa
    }

    [Fact]
    public void Hide_WithNoHandleOrAfterReveal_DoesNothing()
    {
        var recorder = new Recorder();
        using var cloak = new StartupCloak(new InlineDispatcher(), setCloaked: recorder.Set);

        cloak.Hide(0);
        cloak.Reveal();
        cloak.Hide(42);

        Assert.Empty(recorder.Calls);
        Assert.False(cloak.IsHidden);
    }

    [Fact]
    public void SafetyTimeout_RevealsAWindowThatNeverGetsAFrame()
    {
        var recorder = new Recorder();
        using var revealed = new ManualResetEventSlim();
        using var cloak = new StartupCloak(new InlineDispatcher(), onRevealed: revealed.Set, setCloaked: recorder.Set,
            safetyTimeout: TimeSpan.FromMilliseconds(50));

        cloak.Hide(7);

        Assert.True(revealed.Wait(TimeSpan.FromSeconds(10)), "hạn chót an toàn không bỏ cloak");
        Assert.False(cloak.IsHidden);
    }

    [Fact]
    public void Stop_CancelsTheSafetyTimeoutSoAClosedWindowIsNeverTouched()
    {
        var recorder = new Recorder();
        var cloak = new StartupCloak(new InlineDispatcher(), setCloaked: recorder.Set, safetyTimeout: TimeSpan.FromMilliseconds(40));
        cloak.Hide(7);

        cloak.Stop();
        Thread.Sleep(250);

        Assert.True(cloak.IsHidden); // vẫn cloak: cửa sổ đang đóng, hạn chót không được đụng vào nó
        Assert.Single(recorder.Calls);
    }

    [Fact]
    public void SafetyTimeoutDefault_IsThreeSeconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(3), StartupCloak.SafetyTimeout);
    }
}

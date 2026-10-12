using PhotoReview.App.Windowing;
using PhotoReview.Shell.Win32.Windowing;

namespace PhotoReview.Shell.Tests.Windowing;

public sealed class ShellFullscreenTests
{
    private sealed class FakeNative : IFullscreenNative
    {
        public List<string> Calls { get; } = [];

        public WindowShowState State { get; set; } = WindowShowState.Normal;

        public ScreenRect? Monitor { get; set; } = new(0, 0, 1920, 1080);

        public ScreenRect? Window { get; set; } = new(100, 100, 700, 500);

        public WindowShowState CurrentState(nint hwnd) => State;

        public bool TryGetMonitorRect(nint hwnd, out ScreenRect monitor)
        {
            monitor = Monitor.GetValueOrDefault();
            return Monitor is not null;
        }

        public bool TryGetWindowRect(nint hwnd, out ScreenRect window)
        {
            window = Window.GetValueOrDefault();
            return Window is not null;
        }

        public void MoveTo(nint hwnd, ScreenRect rect) => Calls.Add("move " + rect);

        public void Show(nint hwnd, WindowShowState state) => Calls.Add("show " + state);
    }

    private static ShellFullscreen Create(FakeNative native) => new(new FullscreenController(), native);

    [Fact]
    public void NotFullscreen_HasNoRestoreStateAndIgnoresRefitAndExit()
    {
        var native = new FakeNative();
        var fullscreen = Create(native);

        Assert.False(fullscreen.IsActive);
        Assert.Null(fullscreen.RestoreState);
        Assert.Null(fullscreen.RestoreBounds);
        Assert.False(fullscreen.Apply(0x10, false));
        Assert.False(fullscreen.Refit(0x10));
        Assert.Empty(native.Calls);
    }

    [Fact]
    public void WithoutAHandle_NothingHappens()
    {
        var native = new FakeNative();
        var fullscreen = Create(native);

        Assert.False(fullscreen.Apply(0, true));
        Assert.False(fullscreen.IsActive);
        Assert.Empty(native.Calls);
    }

    [Fact]
    public void Reset_DropsTheFullscreenState()
    {
        var native = new FakeNative();
        var fullscreen = Create(native);

        fullscreen.Reset();

        Assert.False(fullscreen.IsActive);
    }
}

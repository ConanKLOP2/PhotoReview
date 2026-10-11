using PhotoReview.App.Windowing;
using PhotoReview.Shell.Interop;
using PhotoReview.Shell.Win32.Startup;

namespace PhotoReview.Shell.Tests.Startup;

[Trait("Category", "HotPath")]
public sealed class ShellPlacementTests
{
    private static readonly WindowPlacementData Normal = new(44, 0, 1, -1, -1, -1, -1, 100, 80, 900, 600);
    private static readonly WindowPlacementData Maximized = Normal with { ShowCommand = 3 };

    private sealed class FakeNative : IWindowPlacementNative
    {
        public List<(nint Hwnd, WindowPlacementData Data)> SetCalls { get; } = [];

        public bool SetResult { get; set; } = true;

        public WindowPlacementData? Current { get; set; }

        public bool SetHidden(nint hwnd, WindowPlacementData data)
        {
            SetCalls.Add((hwnd, data));
            return SetResult;
        }

        public bool TryGet(nint hwnd, out WindowPlacementData data)
        {
            data = Current!;
            return Current is not null;
        }
    }

    private sealed class FakeStore : IWindowPlacementStore
    {
        public List<(string Path, WindowPlacementData Data)> Saved { get; } = [];

        public bool Throw { get; set; }

        public Task<WindowPlacementData?> Prefetch(string placementPath) => Task.FromResult<WindowPlacementData?>(null);

        public WindowPlacementData? Read(string placementPath) => null;

        public void Save(string placementPath, WindowPlacementData data)
        {
            if (Throw)
            {
                throw new IOException("disk full");
            }

            Saved.Add((placementPath, data));
        }

        public WindowPlacementData? TakePrefetched(string placementPath) => null;
    }

    [Fact]
    public void ApplyBeforeShow_VisibleNormalPlacement_IsSetHiddenAndPlansNormal()
    {
        var native = new FakeNative();

        var state = ShellPlacement.ApplyBeforeShow(5, Normal, _ => true, native);

        Assert.Equal(WindowShowState.Normal, state);
        Assert.Equal([((nint)5, Normal)], native.SetCalls);
    }

    [Fact]
    public void ApplyBeforeShow_VisibleMaximizedPlacement_PlansMaximized()
    {
        var native = new FakeNative();

        Assert.Equal(WindowShowState.Maximized, ShellPlacement.ApplyBeforeShow(5, Maximized, _ => true, native));
        Assert.Single(native.SetCalls);
    }

    [Fact]
    public void ApplyBeforeShow_NoSavedPlacement_AppliesNothing()
    {
        var native = new FakeNative();

        Assert.Null(ShellPlacement.ApplyBeforeShow(5, null, _ => true, native));
        Assert.Empty(native.SetCalls);
    }

    [Fact]
    public void ApplyBeforeShow_OffScreenNormal_KeepsTheDefaultSize()
    {
        var native = new FakeNative();

        Assert.Null(ShellPlacement.ApplyBeforeShow(5, Normal, _ => false, native));
        Assert.Empty(native.SetCalls);
    }

    [Fact]
    public void ApplyBeforeShow_OffScreenMaximized_StillOpensMaximizedWithoutMovingTheWindow()
    {
        var native = new FakeNative();

        Assert.Equal(WindowShowState.Maximized, ShellPlacement.ApplyBeforeShow(5, Maximized, _ => false, native));
        Assert.Empty(native.SetCalls); // toạ độ Normal nằm ngoài mọi monitor: không được đặt
    }

    [Fact]
    public void ApplyBeforeShow_MinimizedOrGarbageShowCommand_OpensNormal()
    {
        var native = new FakeNative();

        Assert.Equal(WindowShowState.Normal, ShellPlacement.ApplyBeforeShow(5, Normal with { ShowCommand = 2 }, _ => true, native));
        Assert.Equal(WindowShowState.Normal, ShellPlacement.ApplyBeforeShow(5, Normal with { ShowCommand = 0 }, _ => true, native));
        Assert.Equal(WindowShowState.Normal, ShellPlacement.ApplyBeforeShow(5, Normal with { ShowCommand = 99 }, _ => true, native));
    }

    [Fact]
    public void ApplyBeforeShow_WhenSetFails_AppliesNothing()
    {
        var native = new FakeNative { SetResult = false };

        Assert.Null(ShellPlacement.ApplyBeforeShow(5, Normal, _ => true, native));
    }

    [Fact]
    public void ApplyBeforeShow_WithNoHwnd_AppliesNothing()
    {
        var native = new FakeNative();

        Assert.Null(ShellPlacement.ApplyBeforeShow(0, Normal, _ => true, native));
        Assert.Empty(native.SetCalls);
    }

    [Fact]
    public void Save_WritesTheCurrentPlacementWithANormalisedShowCommand()
    {
        var native = new FakeNative { Current = Normal with { ShowCommand = 2 } }; // đóng khi đang thu nhỏ
        var store = new FakeStore();

        var saved = ShellPlacement.Save(5, @"C:\p\window-placement.json", store, native);

        Assert.True(saved);
        var (path, data) = Assert.Single(store.Saved);
        Assert.Equal(@"C:\p\window-placement.json", path);
        Assert.Equal(1, data.ShowCommand); // thu nhỏ không phải trạng thái khôi phục có nghĩa (R2-F-26)
        Assert.Equal((100, 80, 900, 600), (data.NormalLeft, data.NormalTop, data.NormalRight, data.NormalBottom));
    }

    [Fact]
    public void Save_WhileFullscreen_StoresTheStateBeforeFullscreen()
    {
        var native = new FakeNative { Current = Maximized }; // F11 đọc lại là Maximized không viền
        var store = new FakeStore();

        ShellPlacement.Save(5, "p", store, native, fullscreenRestoreState: WindowShowState.Normal);

        Assert.Equal(1, store.Saved.Single().Data.ShowCommand); // R7-10
    }

    [Fact]
    public void Save_WhileFullscreen_StoresTheNormalRectFromBeforeF11NotTheMonitorRect()
    {
        // F11 đặt cửa sổ Normal lên cả monitor mà không đổi trạng thái: GetWindowPlacement báo rect monitor (WP-25, như
        // WindowPlacementService.Save của bản WPF).
        var native = new FakeNative { Current = Normal with { NormalLeft = 0, NormalTop = 0, NormalRight = 1920, NormalBottom = 1080 } };
        var store = new FakeStore();

        ShellPlacement.Save(5, "p", store, native, WindowShowState.Normal, fullscreenNormalBounds: (120, 90, 920, 590));

        var data = store.Saved.Single().Data;
        Assert.Equal((120, 90, 920, 590), (data.NormalLeft, data.NormalTop, data.NormalRight, data.NormalBottom));
        Assert.Equal(1, data.ShowCommand);
    }

    [Fact]
    public void Save_NotFullscreen_IgnoresTheNormalBoundsOverride()
    {
        var native = new FakeNative { Current = Normal };
        var store = new FakeStore();

        ShellPlacement.Save(5, "p", store, native, fullscreenRestoreState: null, fullscreenNormalBounds: (1, 2, 3, 4));

        var data = store.Saved.Single().Data;
        Assert.Equal((100, 80, 900, 600), (data.NormalLeft, data.NormalTop, data.NormalRight, data.NormalBottom));
    }

    [Fact]
    public void Save_WhileFullscreenFromMaximized_StoresMaximizedWithTheRestoreRect()
    {
        var native = new FakeNative { Current = Maximized with { NormalLeft = -8, NormalTop = -8, NormalRight = 1928, NormalBottom = 1088 } };
        var store = new FakeStore();

        ShellPlacement.Save(5, "p", store, native, WindowShowState.Maximized, fullscreenNormalBounds: (50, 60, 850, 560));

        var data = store.Saved.Single().Data;
        Assert.Equal(3, data.ShowCommand);
        Assert.Equal((50, 60, 850, 560), (data.NormalLeft, data.NormalTop, data.NormalRight, data.NormalBottom));
    }

    [Fact]
    public void Save_WhenThePlacementCannotBeRead_WritesNothing()
    {
        var store = new FakeStore();

        Assert.False(ShellPlacement.Save(5, "p", store, new FakeNative { Current = null }));
        Assert.False(ShellPlacement.Save(0, "p", store, new FakeNative { Current = Normal }));
        Assert.Empty(store.Saved);
    }

    [Fact]
    public void Save_WhenTheDiskFails_DoesNotThrow()
    {
        var store = new FakeStore { Throw = true };

        Assert.False(ShellPlacement.Save(5, "p", store, new FakeNative { Current = Normal }));
    }

    [Theory]
    [InlineData(WindowShowState.Maximized, true, WindowMessages.SwShowMaximized)]
    [InlineData(WindowShowState.Maximized, false, WindowMessages.SwShowMaximized)]
    [InlineData(WindowShowState.Normal, true, WindowMessages.SwShowNormal)]
    [InlineData(WindowShowState.Normal, false, WindowMessages.SwShowNoActivate)]
    [InlineData(null, true, WindowMessages.SwShowNormal)]
    [InlineData(null, false, WindowMessages.SwShowNoActivate)]
    public void ShowCommandFor_MapsStateAndActivation(WindowShowState? state, bool activate, int expected)
    {
        Assert.Equal(expected, ShellPlacement.ShowCommandFor(state, activate));
    }
}

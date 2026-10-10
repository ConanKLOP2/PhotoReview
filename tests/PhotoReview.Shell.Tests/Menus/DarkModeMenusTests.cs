using System.Runtime.InteropServices;
using PhotoReview.Shell.Win32.Menus;

namespace PhotoReview.Shell.Tests.Menus;

/// <summary>
/// WP-18: dark mode menu qua uxtheme (ordinal) với "hàm xuất" giả; không chạm uxtheme.dll thật. Rơi về sáng (false) khi
/// thiếu API hoặc build cũ, không ném lỗi.
/// </summary>
[Trait("Category", "HotPath")]
public sealed unsafe class DarkModeMenusTests
{
    private static int s_lastMode = -1;
    private static int s_setModeCalls;
    private static int s_flushCalls;
    private static nint s_allowWindow;

    [UnmanagedCallersOnly]
    private static int FakeSetPreferredAppMode(int mode)
    {
        s_lastMode = mode;
        s_setModeCalls++;
        return 0;
    }

    [UnmanagedCallersOnly]
    private static void FakeFlushMenuThemes() => s_flushCalls++;

    [UnmanagedCallersOnly]
    private static int FakeAllowDarkModeForWindow(nint hwnd, int allow)
    {
        s_allowWindow = hwnd;
        return allow;
    }

    private static void Reset()
    {
        s_lastMode = -1;
        s_setModeCalls = 0;
        s_flushCalls = 0;
        s_allowWindow = 0;
    }

    private static nint Resolve(int ordinal) => ordinal switch
    {
        DarkModeMenus.SetPreferredAppModeOrdinal => (nint)(delegate* unmanaged<int, int>)&FakeSetPreferredAppMode,
        DarkModeMenus.FlushMenuThemesOrdinal => (nint)(delegate* unmanaged<void>)&FakeFlushMenuThemes,
        DarkModeMenus.AllowDarkModeForWindowOrdinal => (nint)(delegate* unmanaged<nint, int, int>)&FakeAllowDarkModeForWindow,
        _ => 0,
    };

    [Fact]
    public void TryEnable_AllExportsPresent_ForcesDarkAndFlushesMenuThemes()
    {
        Reset();
        var dark = new DarkModeMenus(Resolve, DarkModeMenus.MinimumBuild);

        var enabled = dark.TryEnable();

        Assert.True(enabled);
        Assert.Equal(DarkModeMenus.ForceDark, s_lastMode);
        Assert.Equal(1, s_flushCalls);
    }

    [Fact]
    public void TryEnable_CalledTwice_AppliesOnlyOnce()
    {
        Reset();
        var dark = new DarkModeMenus(Resolve, DarkModeMenus.MinimumBuild);

        dark.TryEnable();
        var second = dark.TryEnable();

        Assert.True(second);
        Assert.Equal(1, s_setModeCalls);
    }

    [Fact]
    public void TryEnable_OldWindowsBuild_FallsBackToLightWithoutCallingExports()
    {
        Reset();
        var dark = new DarkModeMenus(Resolve, DarkModeMenus.MinimumBuild - 1);

        Assert.False(dark.TryEnable());
        Assert.Equal(0, s_setModeCalls);
    }

    [Fact]
    public void TryEnable_OrdinalMissing_FallsBackToLight()
    {
        Reset();
        var dark = new DarkModeMenus(_ => 0, DarkModeMenus.MinimumBuild);

        Assert.False(dark.TryEnable());
        Assert.False(dark.TryAllowForWindow(42));
    }

    [Fact]
    public void TryEnable_FlushMissing_StillEnables()
    {
        Reset();
        var dark = new DarkModeMenus(o => o == DarkModeMenus.FlushMenuThemesOrdinal ? 0 : Resolve(o), DarkModeMenus.MinimumBuild);

        Assert.True(dark.TryEnable());
        Assert.Equal(0, s_flushCalls);
    }

    [Fact]
    public void TryAllowForWindow_AfterEnable_PassesTheWindow()
    {
        Reset();
        var dark = new DarkModeMenus(Resolve, DarkModeMenus.MinimumBuild);
        dark.TryEnable();

        Assert.True(dark.TryAllowForWindow(1234));
        Assert.Equal(1234, s_allowWindow);
    }

    [Fact]
    public void TryAllowForWindow_BeforeEnable_DoesNothing()
    {
        Reset();
        var dark = new DarkModeMenus(Resolve, DarkModeMenus.MinimumBuild);

        Assert.False(dark.TryAllowForWindow(1234));
        Assert.Equal(0, s_allowWindow);
    }
}

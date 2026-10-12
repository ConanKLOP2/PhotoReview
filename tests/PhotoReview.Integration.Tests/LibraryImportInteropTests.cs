using PhotoReview.Platform.Windows;

namespace PhotoReview.Integration.Tests;

/// <summary>WP-34: the Platform.Windows P/Invokes are source-generated ([LibraryImport]); the real calls below are read-only and harmless.</summary>
[Trait("Category", "Integration")]
public sealed class LibraryImportInteropTests
{
    [Fact(DisplayName = "WP-34: MONITORINFOEXW marshals as 104 bytes (40 header + WCHAR[32])")]
    public void MonitorInfoEx_IsTheNativeSize() => Assert.Equal(104, NativeVBlankDriver.MonitorInfoExSize);

    [Fact(DisplayName = "WP-34: DWM_TIMING_INFO is passed packed (no padding), at the size dwmapi.h declares")]
    public void TimingInfo_IsPackedAtTheDeclaredSize() => Assert.Equal(TIMING_INFO_SIZE, NativeVBlankDriver.TimingInfoSize);

    private const int TIMING_INFO_SIZE = 292; // unchanged by WP-34 (Pack = 1, as dwmapi.h declares it); a pack change would break cbSize

    [Theory(DisplayName = "WP-34: the device name ends at the first NUL and never exceeds 32 characters")]
    [InlineData("DISPLAY1\0garbage", "DISPLAY1")]
    [InlineData("", "")]
    [InlineData("\0abc", "")]
    [InlineData("12345678901234567890123456789012XYZ", "12345678901234567890123456789012")]
    [InlineData("abc", "abc")]
    public void DeviceNameFrom_StopsAtNulAndCapsAt32(string buffer, string expected) =>
        Assert.Equal(expected, NativeVBlankDriver.DeviceNameFrom(buffer.AsSpan()));

    [Fact(DisplayName = "WP-34: StrCmpLogicalW through LibraryImport sorts digits numerically and compares non-ASCII text as UTF-16")]
    public void NaturalComparer_UsesTheUtf16Overload()
    {
        var c = WindowsNaturalComparer.Instance;

        Assert.True(c.Compare("img2.jpg", "img10.jpg") < 0);
        Assert.True(c.Compare("éa.jpg", "éb.jpg") < 0);
        Assert.True(c.Compare("中1.jpg", "中2.jpg") < 0);
        Assert.NotEqual(0, c.Compare("é.jpg", "e.jpg"));
    }

    [Fact(DisplayName = "WP-34: GlobalMemoryStatusEx through LibraryImport reports physical memory and a sane load")]
    public void MemoryProbe_ReturnsAPlausibleSnapshot()
    {
        var snapshot = WindowsMemoryProbe.Instance.GetSnapshot();

        Assert.NotNull(snapshot);
        Assert.InRange(snapshot.Value.LoadPercent, 0u, 100u);
        Assert.True(snapshot.Value.AvailableBytes > 0);
    }

    [Fact(DisplayName = "WP-34: DWM attribute calls on a null handle fail closed (false), they do not throw")]
    public void DwmCalls_OnNullHandle_ReturnFalse()
    {
        Assert.False(WindowsDarkTitleBar.TryEnableDarkMode(IntPtr.Zero));
        Assert.False(WindowsDarkTitleBar.TrySetCaptionColor(IntPtr.Zero, 0));
        Assert.False(WindowsDwmCloak.TrySetCloaked(IntPtr.Zero, true));
        // A handle that is not a window: DWM rejects it with a failing HRESULT -> false (the P/Invoke itself is intact).
        Assert.False(WindowsDwmCloak.TrySetCloaked(new IntPtr(0x7FFF0), true));
        Assert.False(WindowsDarkTitleBar.TryEnableDarkMode(new IntPtr(0x7FFF0)));
    }

    [Fact(DisplayName = "WP-34: the real vblank driver's user32/gdi32/dwmapi imports resolve and fail closed on a bogus monitor")]
    public void NativeVBlankDriver_ImportsResolve()
    {
        var driver = NativeVBlankDriver.Instance;

        Assert.False(driver.TryOpen(new IntPtr(0x7FFF0), out var adapter, out var source));
        Assert.Equal((0u, 0u), (adapter, source));
        _ = driver.MonitorFromWindow(IntPtr.Zero);
        _ = driver.DwmTiming(); // null or a timing, never an exception
    }
}

using System.Runtime.InteropServices;
using PhotoReview.App.Input;
using PhotoReview.Shell.Win32.Input;

namespace PhotoReview.Shell.Tests.Input;

/// <summary>WP-22: các đọc hệ thống của nhập liệu (phím chỉnh, ngưỡng kéo/double-click, con trỏ) và giải mã lParam/wParam.</summary>
public sealed partial class InputNativeTests
{
    [LibraryImport("user32.dll", EntryPoint = "GetKeyboardState")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetKeyboardState(byte[] state);

    [LibraryImport("user32.dll", EntryPoint = "SetKeyboardState")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetKeyboardState(byte[] state);

    [Theory]
    [InlineData(InputNative.VkControl, KeyModifiers.Control)]
    [InlineData(InputNative.VkMenu, KeyModifiers.Alt)]
    [InlineData(InputNative.VkShift, KeyModifiers.Shift)]
    [InlineData(InputNative.VkLWin, KeyModifiers.Windows)]
    [InlineData(InputNative.VkRWin, KeyModifiers.Windows)]
    public void ReadModifiers_MapsEachKeyStateToItsFlag(int virtualKey, KeyModifiers expected)
    {
        var state = new byte[256];
        Assert.True(GetKeyboardState(state));
        var saved = (byte[])state.Clone();
        try
        {
            Array.Clear(state);
            state[virtualKey] = 0x80;
            Assert.True(SetKeyboardState(state));

            Assert.Equal(expected, InputNative.ReadModifiers());

            state[virtualKey] = 0;
            state[InputNative.VkControl] = 0x80;
            state[InputNative.VkShift] = 0x80;
            Assert.True(SetKeyboardState(state));
            Assert.Equal(KeyModifiers.Control | KeyModifiers.Shift, InputNative.ReadModifiers());
        }
        finally
        {
            SetKeyboardState(saved);
        }
    }

    [Fact]
    public void ReadModifiers_NoKeyDown_IsNone()
    {
        var saved = new byte[256];
        Assert.True(GetKeyboardState(saved));
        try
        {
            Assert.True(SetKeyboardState(new byte[256]));
            Assert.Equal(KeyModifiers.None, InputNative.ReadModifiers());
        }
        finally
        {
            SetKeyboardState(saved);
        }
    }

    [Fact]
    public void SystemMetrics_ComeFromWindows_ScaledByDpi()
    {
        var (dragX, dragY) = InputNative.DragMetricsPixels(96);
        var (dragX2, _) = InputNative.DragMetricsPixels(192);
        Assert.True(dragX > 0 && dragY > 0);
        Assert.True(dragX2 >= dragX);

        var (clickW, clickH) = InputNative.DoubleClickSizePixels(96);
        Assert.True(clickW > 0 && clickH > 0);
        Assert.True(InputNative.GetDoubleClickTime() >= 200);
    }

    [Fact]
    public void CursorScreenPixel_IsReadable()
    {
        Assert.NotNull(InputNative.CursorScreenPixel());
    }

    [Theory]
    [InlineData(1.0, 96u)]
    [InlineData(1.25, 120u)]
    [InlineData(1.5, 144u)]
    [InlineData(2.0, 192u)]
    [InlineData(0.0, 1u)]
    public void DpiOf_RoundsTheScaleToWholeDpi(double scale, uint expected) => Assert.Equal(expected, InputNative.DpiOf(scale));

    [Theory]
    [InlineData(0x0000_0064, 100)]
    [InlineData(0x0000_FFFF, -1)]
    [InlineData(0x0000_8000, -32768)]
    public void SignedLowWord_ReadsAnInt16(int value, int expected) => Assert.Equal(expected, InputNative.SignedLowWord(value));

    [Theory]
    [InlineData(0x0064_0000, 100)]
    [InlineData(unchecked((int)0xFFFF_0000), -1)]
    [InlineData(unchecked((int)0x8000_0000), -32768)]
    public void SignedHighWord_ReadsAnInt16(int value, int expected)
    {
        Assert.Equal(expected, InputNative.SignedHighWord(value));
        Assert.Equal(expected, InputNative.WheelDelta(value));
    }

    [Fact]
    public void LowWord_IsUnsigned()
    {
        Assert.Equal(0xFFFF, InputNative.LowWord((nint)0x1_FFFF));
    }
}

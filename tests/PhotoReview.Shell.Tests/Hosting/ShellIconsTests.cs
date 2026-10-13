using System.Runtime.InteropServices;
using PhotoReview.Shell.Interop;
using PhotoReview.Shell.Win32.Hosting;

namespace PhotoReview.Shell.Tests.Hosting;

/// <summary>Icon của app (PhotoReview.ico nhúng làm ApplicationIcon) nạp được và đặt lên cửa sổ theo DPI.</summary>
[Trait("Category", "Native")]
public sealed partial class ShellIconsTests
{
    private const uint WmGetIcon = 0x007F;
    private const uint WsPopup = 0x80000000;

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    private static partial nint SendMessage(nint hwnd, uint message, nint wParam, nint lParam);

    [Fact]
    public void Load_ReturnsAnIconHandle_ForBigAndSmallSizes()
    {
        Assert.NotEqual(0, ShellIcons.LoadBig(96));
        Assert.NotEqual(0, ShellIcons.LoadSmall(96));
        Assert.NotEqual(0, ShellIcons.LoadBig(192));
    }

    [Fact]
    public void Load_ReturnsZeroForAnInvalidSize()
    {
        Assert.Equal(0, ShellIcons.Load(0, 16));
        Assert.Equal(0, ShellIcons.Load(16, -1));
    }

    [Theory]
    [InlineData(96u)]
    [InlineData(144u)]
    public void Apply_SetsTheBigAndSmallIconOfTheWindow(uint dpi)
    {
        var hwnd = User32.CreateWindowEx(0, "STATIC", "icon test", WsPopup, 0, 0, 10, 10, 0, 0, Kernel32.GetModuleHandle(0), 0);
        Assert.NotEqual(0, hwnd);
        try
        {
            Assert.True(ShellIcons.Apply(hwnd, dpi));

            Assert.NotEqual(0, SendMessage(hwnd, WmGetIcon, 1, 0)); // ICON_BIG
            Assert.NotEqual(0, SendMessage(hwnd, WmGetIcon, 0, 0)); // ICON_SMALL
        }
        finally
        {
            User32.DestroyWindow(hwnd);
        }
    }

    [Fact]
    public void Apply_ToNoWindow_ReturnsFalse()
    {
        Assert.False(ShellIcons.Apply(0, 96));
    }
}

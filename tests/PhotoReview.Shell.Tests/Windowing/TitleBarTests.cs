using PhotoReview.Shell.Win32.Windowing;

namespace PhotoReview.Shell.Tests.Windowing;

public sealed class TitleBarTests
{
    private sealed class FakeDwm : IDwmWindowAttributes
    {
        public List<(nint Hwnd, uint Attribute, uint Value)> Calls { get; } = [];

        public HashSet<uint> Rejected { get; } = [];

        public Exception? Throw { get; set; }

        public bool Set(nint hwnd, uint attribute, uint value)
        {
            Calls.Add((hwnd, attribute, value));
            if (Throw is not null)
            {
                throw Throw;
            }

            return !Rejected.Contains(attribute);
        }
    }

    [Fact]
    public void Apply_SetsImmersiveDarkModeAndTheDarkWindowCaptionColour()
    {
        var dwm = new FakeDwm();

        Assert.True(TitleBar.Apply(0x42, dwm));

        Assert.Equal([(0x42, 20u, 1u), (0x42, 35u, 0x00171717u)], dwm.Calls);
        Assert.Equal(0x00171717u, TitleBar.CaptionColorBgr); // COLORREF 0x00BBGGRR của Dark.Window #171717
    }

    [Fact]
    public void Apply_FallsBackToTheLegacyDarkModeAttributeOnOlderWindows10()
    {
        var dwm = new FakeDwm();
        dwm.Rejected.Add(20);

        Assert.True(TitleBar.Apply(0x42, dwm));

        Assert.Contains((0x42, 19u, 1u), dwm.Calls);
    }

    [Fact]
    public void Apply_WhenNothingIsSupported_ReturnsFalseAndDoesNotThrow()
    {
        var dwm = new FakeDwm();
        dwm.Rejected.UnionWith([19u, 20u, 35u]);

        Assert.False(TitleBar.Apply(0x42, dwm));
    }

    [Fact]
    public void Apply_WithoutAHandle_DoesNothing()
    {
        var dwm = new FakeDwm();

        Assert.False(TitleBar.Apply(0, dwm));
        Assert.Empty(dwm.Calls);
    }

    [Fact]
    public void Apply_WhenDwmapiIsMissing_IsSwallowed()
    {
        var dwm = new FakeDwm { Throw = new DllNotFoundException("dwmapi") };

        Assert.False(TitleBar.Apply(0x42, dwm));
    }

    [Fact]
    public void Apply_OnARealWindowHandleThatDoesNotExist_NeverThrows()
    {
        // DWM thật từ chối HWND lạ bằng HRESULT lỗi: không ném (Windows cũ/không hỗ trợ cũng chỉ là no-op).
        Assert.Null(Record.Exception(() => TitleBar.Apply(0x7FFF0)));
    }
}

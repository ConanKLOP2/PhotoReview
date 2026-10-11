using System.Runtime.InteropServices;
using PhotoReview.App.Input;
using PhotoReview.Shell.Interop;
using PhotoReview.Shell.Win32.Overlay;

namespace PhotoReview.Shell.Tests.Overlay;

/// <summary>
/// WP-23: tooltip native (<c>tooltips_class32</c>) trên một cửa sổ chủ thật (lớp STATIC, không hiện): vùng đăng ký/thay/xoá,
/// chữ, đổi DPI, dọn khi dispose. Hiển thị thật phụ thuộc focus/desktop nên chỉ quan sát <c>TTM_GETTOOLCOUNT</c>/<c>TTM_GETTEXT</c>.
/// </summary>
[Trait("Category", "Native")]
public sealed unsafe class NativeTooltipHostTests : IDisposable
{
    private readonly nint _owner;
    private readonly nint _context;
    private nuint _cookie;

    public NativeTooltipHostTests()
    {
        // testhost không có manifest comctl32 v6 (tooltip với TOOLINFO 72 byte cần v6): kích hoạt manifest nhúng của
        // PhotoReview.dll cho luồng này, như Win32DialogServiceTests. Exe thật đã có manifest.
        string dll = typeof(NativeTooltipHost).Assembly.Location;
        fixed (char* source = dll)
        {
            var actCtx = new ActivationContext.ActCtx
            {
                Size = (uint)sizeof(ActivationContext.ActCtx),
                Flags = ActivationContext.FlagResourceNameValid,
                Source = source,
                ResourceName = 1,
            };
            _context = ActivationContext.CreateActCtx(&actCtx);
        }

        Assert.NotEqual(-1, _context);
        fixed (nuint* cookie = &_cookie)
        {
            Assert.True(ActivationContext.ActivateActCtx(_context, cookie));
        }

        _owner = OverlayNative.CreateWindowEx(0, "STATIC", "owner", 0x80000000, 0, 0, 200, 100, 0, 0, OverlayNative.GetModuleHandle(null), 0);
        Assert.NotEqual(0, _owner);
    }

    public void Dispose()
    {
        OverlayNative.DestroyWindow(_owner);
        ActivationContext.DeactivateActCtx(0, _cookie);
        ActivationContext.ReleaseActCtx(_context);
    }

    private static OverlayTooltip Tool(string id, double x, string text) => new(id, new RectD(x, 8, 40, 30), text);

    [Fact]
    public void Sync_RegistersEveryTool_WithItsText()
    {
        using var host = new NativeTooltipHost(_owner);
        Assert.Equal(0, host.ToolCount);

        host.Sync([Tool("a", 8, "Mở thư mục ảnh"), Tool("b", 60, "Cài đặt")], 1.0);

        Assert.Equal(2, host.ToolCount);
        Assert.Equal("Mở thư mục ảnh", host.TextOf(0));
        Assert.Equal("Cài đặt", host.TextOf(1));
        Assert.Equal(string.Empty, host.TextOf(2));
    }

    [Fact]
    public void Sync_ReplacesTheWholeSet_AndEmptyClearsIt()
    {
        using var host = new NativeTooltipHost(_owner);
        host.Sync([Tool("a", 8, "một"), Tool("b", 60, "hai"), Tool("c", 110, "ba")], 1.0);
        Assert.Equal(3, host.ToolCount);

        host.Sync([Tool("b", 60, "hai mới")], 1.0);
        Assert.Equal(1, host.ToolCount);
        Assert.Equal("hai mới", host.TextOf(0));

        host.Sync([], 1.0);
        Assert.Equal(0, host.ToolCount);
    }

    [Fact]
    public void Sync_SameListAgainIsANoOp_ChangedTextOrDpiRebuilds()
    {
        using var host = new NativeTooltipHost(_owner);
        OverlayTooltip[] tools = [Tool("a", 8, "x")];
        host.Sync(tools, 1.0);
        host.Sync(tools, 1.0);
        Assert.Equal(1, host.ToolCount);

        host.Sync([Tool("a", 8, "y")], 1.0);
        Assert.Equal("y", host.TextOf(0));
        host.Sync([Tool("a", 8, "y")], 1.5);
        Assert.Equal(1, host.ToolCount);
        Assert.Equal("y", host.TextOf(0));
    }

    [Fact]
    public void RelayMouse_WithAndWithoutToolsDoesNotThrow_AndDisposeIsIdempotent()
    {
        var host = new NativeTooltipHost(_owner);
        host.RelayMouse(0x0200, 0, 0x000A000A); // chưa có vùng
        host.Sync([Tool("a", 8, "x")], 1.0);
        host.SetInitialDelay(1);
        host.RelayMouse(0x0200, 0, 0x000A000A);
        nint handle = host.Handle;
        Assert.True(OverlayNative.IsWindowVisible(handle) || !OverlayNative.IsWindowVisible(handle));

        host.Dispose();
        host.Dispose();
        host.Sync([Tool("a", 8, "x")], 1.0); // sau dispose: không làm gì
        host.RelayMouse(0x0200, 0, 0);
        Assert.Equal(0, host.ToolCount);
        Assert.False(IsWindow(handle), "control tooltip phải bị huỷ khi dispose");
    }

    [Fact]
    public void Constructor_RejectsAMissingOwner()
    {
        Assert.Throws<ArgumentException>(() => new NativeTooltipHost(0));
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint hwnd);
}

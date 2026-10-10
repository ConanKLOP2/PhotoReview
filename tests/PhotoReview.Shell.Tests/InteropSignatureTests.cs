using System.Runtime.InteropServices;
using PhotoReview.Shell.Interop;

namespace PhotoReview.Shell.Tests;

/// <summary>
/// WP-13a (NO-WPF-EXEC-PLAN-WP): chữ ký P/Invoke và bố cục struct của Shell.Interop phải khớp Win32 SDK (x64).
/// Giá trị kích thước/offset viết tay theo định nghĩa SDK (máy này không có Windows SDK headers); test bắt lệch bố cục.
/// Test gọi OS (Native) đều tự dọn: chỉ đăng ký/huỷ một window class duy nhất tên ngẫu nhiên.
/// </summary>
public sealed unsafe class InteropSignatureTests
{
    // --- Kích thước struct (x64) ---

    [Trait("Category", "HotPath")]
    [Fact]
    public void Rect_Size_Is16()
    {
        Assert.Equal(16, sizeof(Rect)); // RECT: 4 x LONG
    }

    [Trait("Category", "HotPath")]
    [Fact]
    public void Point_Size_Is8()
    {
        Assert.Equal(8, sizeof(Point)); // POINT: 2 x LONG
    }

    [Trait("Category", "HotPath")]
    [Fact]
    public void WndClassEx_Size_Is80()
    {
        // WNDCLASSEXW x64: cbSize+style (8), lpfnWndProc (8), 2 int (8), 6 handle/ptr (48), lpszMenuName, lpszClassName, hIconSm (24) = 80
        Assert.Equal(80, sizeof(WndClassEx));
        Assert.Equal(8, Marshal.OffsetOf<WndClassEx>(nameof(WndClassEx.WndProc)).ToInt32());
        Assert.Equal(64, Marshal.OffsetOf<WndClassEx>(nameof(WndClassEx.ClassName)).ToInt32());
        Assert.Equal(72, Marshal.OffsetOf<WndClassEx>(nameof(WndClassEx.IconSmall)).ToInt32());
    }

    [Trait("Category", "HotPath")]
    [Fact]
    public void Msg_Size_Is48()
    {
        // MSG x64: hwnd(8) message(4+4 pad) wParam(8) lParam(8) time(4) pt(8) lPrivate(4) + pad(4) = 48
        Assert.Equal(48, sizeof(Msg));
        Assert.Equal(32, Marshal.OffsetOf<Msg>(nameof(Msg.Time)).ToInt32());
        Assert.Equal(36, Marshal.OffsetOf<Msg>(nameof(Msg.PointX)).ToInt32());
    }

    [Trait("Category", "HotPath")]
    [Fact]
    public void MonitorInfo_Size_Is40()
    {
        Assert.Equal(40, sizeof(MonitorInfo)); // MONITORINFO: cbSize + RECT + RECT + DWORD
    }

    [Trait("Category", "HotPath")]
    [Fact]
    public void MonitorInfoEx_Size_Is104()
    {
        Assert.Equal(104, sizeof(MonitorInfoEx)); // MONITORINFOEXW: MONITORINFO (40) + WCHAR szDevice[32] (64)
    }

    [Trait("Category", "HotPath")]
    [Fact]
    public void WindowPlacement_Size_Is44()
    {
        Assert.Equal(44, sizeof(WindowPlacement)); // WINDOWPLACEMENT: 3 x UINT + 2 x POINT + RECT
    }

    [Trait("Category", "HotPath")]
    [Fact]
    public void InputMessageSource_Size_Is8()
    {
        Assert.Equal(8, sizeof(InputMessageSource)); // INPUT_MESSAGE_SOURCE: 2 x DWORD
    }

    [Trait("Category", "HotPath")]
    [Fact]
    public void TaskDialogButton_Size_Is12()
    {
        Assert.Equal(12, sizeof(TaskDialogButton)); // TASKDIALOG_BUTTON x64 (pshpack1): int + PCWSTR, không pad
    }

    [Trait("Category", "HotPath")]
    [Fact]
    public void TaskDialogConfig_Size_Is160AndFieldOffsetsMatch()
    {
        // TASKDIALOGCONFIG x64 (pshpack1.h: đóng gói 1 byte, không pad): 8 DWORD/int + 16 trường cỡ con trỏ... = 160.
        Assert.Equal(160, sizeof(TaskDialogConfig));
        Assert.Equal(4, Marshal.OffsetOf<TaskDialogConfig>(nameof(TaskDialogConfig.ParentWindow)).ToInt32());
        Assert.Equal(28, Marshal.OffsetOf<TaskDialogConfig>(nameof(TaskDialogConfig.WindowTitle)).ToInt32());
        Assert.Equal(60, Marshal.OffsetOf<TaskDialogConfig>(nameof(TaskDialogConfig.ButtonCount)).ToInt32());
        Assert.Equal(64, Marshal.OffsetOf<TaskDialogConfig>(nameof(TaskDialogConfig.Buttons)).ToInt32());
        Assert.Equal(140, Marshal.OffsetOf<TaskDialogConfig>(nameof(TaskDialogConfig.Callback)).ToInt32());
        Assert.Equal(156, Marshal.OffsetOf<TaskDialogConfig>(nameof(TaskDialogConfig.Width)).ToInt32());
    }

    // --- Gọi thử hàm an toàn (thật sự chạm OS) ---

    [Trait("Category", "Native")]
    [Fact]
    public void GetDpiForSystem_Call_ReturnsAtLeast96()
    {
        // DPI hệ thống tối thiểu là 96 (100 %).
        Assert.True(User32.GetDpiForSystem() >= 96);
    }

    [Trait("Category", "Native")]
    [Fact]
    public void GetSystemMetrics_Calls_ReturnPositiveDragThreshold()
    {
        Assert.True(User32.GetSystemMetrics(WindowMessages.SmCxDrag) > 0);
        Assert.True(User32.GetSystemMetricsForDpi(WindowMessages.SmCxDrag, 96) > 0);
    }

    [Trait("Category", "Native")]
    [Fact]
    public void MonitorFromPointAndGetMonitorInfoEx_PrimaryMonitor_FillsDeviceName()
    {
        // Điểm (0,0) với MONITOR_DEFAULTTOPRIMARY luôn trả về màn hình chính trên desktop đang hoạt động.
        var monitor = User32.MonitorFromPoint(new Point { X = 0, Y = 0 }, WindowMessages.MonitorDefaultToPrimary);
        Assert.NotEqual(0, monitor);

        var info = new MonitorInfoEx { Size = (uint)sizeof(MonitorInfoEx) };
        Assert.True(User32.GetMonitorInfoEx(monitor, &info));
        Assert.Equal(WindowMessages.MonitorInfoFPrimary, info.Flags & WindowMessages.MonitorInfoFPrimary);
        Assert.NotEqual('\0', info.Device[0]);
    }

    [Trait("Category", "Native")]
    [Fact]
    public void RegisterClassEx_ThenUnregisterClass_RoundTrips()
    {
        // Tên ngẫu nhiên: không đụng class của app; luôn huỷ trong finally.
        var className = "PhotoReviewInteropTest_" + Guid.NewGuid().ToString("N");
        var instance = Kernel32.GetModuleHandle(0);
        fixed (char* name = className)
        {
            var windowClass = new WndClassEx
            {
                Size = (uint)sizeof(WndClassEx),
                WndProc = &DefProcStub,
                Instance = instance,
                ClassName = name,
            };
            var atom = User32.RegisterClassEx(&windowClass);
            Assert.NotEqual((ushort)0, atom);
            Assert.True(User32.UnregisterClass(className, instance));
        }
    }

    [UnmanagedCallersOnly]
    private static nint DefProcStub(nint hwnd, uint message, nint wParam, nint lParam)
    {
        return User32.DefWindowProc(hwnd, message, wParam, lParam);
    }
}


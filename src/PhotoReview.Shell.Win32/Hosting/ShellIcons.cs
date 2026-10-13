using System.Runtime.InteropServices;
using PhotoReview.Shell.Interop;

namespace PhotoReview.Shell.Win32.Hosting;

/// <summary>
/// Icon của app cho cửa sổ shell: nạp từ tài nguyên RT_GROUP_ICON của exe (<c>ApplicationIcon</c> = PhotoReview.ico, id 32512 do
/// trình biên dịch gán), không phụ thuộc WPF, AOT-an toàn (LoadImage). Lớp cửa sổ có icon lớn/nhỏ theo DPI hệ thống; mỗi cửa sổ
/// được <c>WM_SETICON</c> theo DPI của chính nó (thanh tác vụ, Alt-Tab, tiêu đề). Thiếu tài nguyên thì không đặt gì (cửa sổ dùng
/// icon mặc định) - không bao giờ ném.
/// </summary>
internal static unsafe partial class ShellIcons
{
    /// <summary>RT_GROUP_ICON id của icon chính (32512 = IDI_APPLICATION; csc /win32icon).</summary>
    internal const int ResourceId = 32512;

    internal const int SmCxIcon = 11;
    internal const int SmCyIcon = 12;
    internal const int SmCxSmIcon = 49;
    internal const int SmCySmIcon = 50;

    private const uint ImageIcon = 1;
    private const uint LrShared = 0x8000;
    private const uint WmSetIcon = 0x0080;
    private const nint IconSmall = 0;
    private const nint IconBig = 1;

    [LibraryImport("user32.dll", EntryPoint = "LoadImageW", SetLastError = true)]
    private static partial nint LoadImage(nint instance, nint name, uint type, int cx, int cy, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    private static partial nint SendMessage(nint hwnd, uint message, nint wParam, nint lParam);

    /// <summary>Nạp icon kích thước cx x cy (dùng chung, hệ thống giữ handle: không cần huỷ); 0 nếu không có tài nguyên.</summary>
    internal static nint Load(int cx, int cy)
    {
        if (cx <= 0 || cy <= 0) return 0;
        var icon = LoadImage(Kernel32.GetModuleHandle(0), ResourceId, ImageIcon, cx, cy, LrShared);
        if (icon != 0) return icon;
        // Khi chạy dưới host khác (test: testhost.exe) tài nguyên nằm trong assembly PhotoReview.dll.
#pragma warning disable IL3002 // GetHINSTANCE trả -1 trong single-file: LoadImage thất bại -> 0 (icon mặc định), vô hại; exe đã có icon ở nhánh trên.
        return LoadImage(Marshal.GetHINSTANCE(typeof(ShellIcons).Module), ResourceId, ImageIcon, cx, cy, LrShared);
#pragma warning restore IL3002
    }

    /// <summary>Icon lớn của lớp cửa sổ theo DPI hệ thống (0 nếu thiếu).</summary>
    internal static nint LoadBig(uint dpi) => Load(Metric(SmCxIcon, dpi), Metric(SmCyIcon, dpi));

    internal static nint LoadSmall(uint dpi) => Load(Metric(SmCxSmIcon, dpi), Metric(SmCySmIcon, dpi));

    /// <summary>Đặt icon lớn + nhỏ của <paramref name="hwnd"/> theo <paramref name="dpi"/> (gọi lại khi DPI đổi). Trả true nếu có icon.</summary>
    internal static bool Apply(nint hwnd, uint dpi)
    {
        if (hwnd == 0) return false;
        var big = LoadBig(dpi);
        var small = LoadSmall(dpi);
        if (big != 0) SendMessage(hwnd, WmSetIcon, IconBig, big);
        if (small != 0) SendMessage(hwnd, WmSetIcon, IconSmall, small);
        return big != 0 || small != 0;
    }

    private static int Metric(int index, uint dpi) =>
        dpi == 0 ? User32.GetSystemMetrics(index) : User32.GetSystemMetricsForDpi(index, dpi);
}

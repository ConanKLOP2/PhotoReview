using PhotoReview.Shell.Interop;
using PhotoReview.Shell.Win32.Menus;
using PhotoReview.Shell.Win32.Overlay;
using PhotoReview.Shell.Win32.Windowing;

namespace PhotoReview.Shell.Win32.Dialogs.Native;

/// <summary>Một điều khiển con đã tạo (cho <see cref="INativeDialogChrome"/>).</summary>
internal readonly record struct DialogChromeControl(nint Hwnd, DialogControlClass Class);

/// <summary>
/// Seam của giao diện tối (title bar + chủ đề điều khiển): <see cref="DarkDialogChrome"/> gọi DWM/uxtheme thật, test thay bằng bản
/// ghi lại để kiểm "cờ dark" mà không cần nhìn bằng mắt. Gọi ĐÚNG MỘT LẦN mỗi hộp thoại, ở WM_INITDIALOG.
/// </summary>
internal interface INativeDialogChrome
{
    void Apply(nint dialog, IReadOnlyList<DialogChromeControl> controls);
}

/// <summary>Màu COLORREF (0x00BBGGRR) lấy từ <see cref="DarkPalette"/> (cùng bảng màu với overlay và cửa sổ WPF).</summary>
internal static class DialogColors
{
    public static uint Window => FromKey("Dark.Window");

    public static uint Input => FromKey("Dark.Input");

    public static uint Text => FromKey("Dark.WindowText");

    public static uint TextStrong => FromKey("Dark.TextStrong");

    public static uint TextSecondary => FromKey("Dark.TextSecondary");

    public static uint Warning => FromKey("Dark.Warning");

    public static uint FromKey(string key)
    {
        var color = DarkPalette.Get(key);
        return ToColorRef(color.R, color.G, color.B);
    }

    internal static uint ToColorRef(float r, float g, float b)
        => (uint)Math.Round(r * 255) | ((uint)Math.Round(g * 255) << 8) | ((uint)Math.Round(b * 255) << 16);
}

/// <summary>
/// Giao diện tối của hộp thoại Win32 (spike S3 mục 2): title bar <c>DWMWA_USE_IMMERSIVE_DARK_MODE</c> (qua <see cref="TitleBar"/>),
/// chế độ ứng dụng tối của uxtheme (ordinal 135, như menu native: <see cref="DarkModeMenus"/>) và chủ đề điều khiển
/// <c>DarkMode_Explorer</c> (nút, thanh cuộn, listbox) / <c>DarkMode_CFD</c> (ô nhập). Không bao giờ ném: thiếu hàm = nền sáng.
/// Nền hộp thoại, chữ và ô nhập tô qua WM_CTLCOLOR* của <see cref="NativeDialog"/>.
/// </summary>
internal sealed class DarkDialogChrome : INativeDialogChrome
{
    internal const string ButtonTheme = "DarkMode_Explorer";
    internal const string EditTheme = "DarkMode_CFD";
    internal const string ListTheme = "DarkMode_Explorer";

    private static readonly Lazy<DarkModeMenus> Menus = new(DarkModeMenus.CreateForCurrentSystem);

    public static DarkDialogChrome Instance { get; } = new();

    public void Apply(nint dialog, IReadOnlyList<DialogChromeControl> controls)
    {
        try
        {
            TitleBar.Apply(dialog);
            var menus = Menus.Value;
            if (menus.TryEnable())
            {
                menus.TryAllowForWindow(dialog);
                foreach (var control in controls)
                {
                    menus.TryAllowForWindow(control.Hwnd);
                }
            }

            foreach (var control in controls)
            {
                var theme = control.Class switch
                {
                    DialogControlClass.Button => ButtonTheme,
                    DialogControlClass.Edit => EditTheme,
                    DialogControlClass.ListBox => ListTheme,
                    _ => null,
                };
                if (theme is not null)
                {
                    Uxtheme.SetWindowTheme(control.Hwnd, theme, null);
                }
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Windows không có hàm: hộp thoại hiện nền sáng, không phải lỗi.
        }
    }
}

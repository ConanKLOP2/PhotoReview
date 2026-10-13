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

    // WP-35 GĐ2: combobox (ô chọn + nút thả = DarkMode_CFD như ô nhập; danh sách thả là một listbox riêng = DarkMode_Explorer),
    // listview report (thân = DarkMode_Explorer, header = DarkMode_ItemsView), progress/trackbar (DarkMode_Explorer).
    internal const string ComboTheme = "DarkMode_CFD";
    internal const string ComboListTheme = "DarkMode_Explorer";
    internal const string ListViewTheme = "DarkMode_Explorer";
    internal const string HeaderTheme = "DarkMode_ItemsView";
    internal const string ProgressTheme = "DarkMode_Explorer";
    internal const string TrackbarTheme = "DarkMode_Explorer";
    internal const string TooltipTheme = "DarkMode_Explorer";
    internal const string ClassicTheme = "";

    private static bool IsRadioOrGroupBox(nint hwnd)
    {
        var type = (uint)User32.GetWindowLongPtr(hwnd, WindowMessages.GwlStyle) & 0xF;
        return type is 4 /* BS_RADIOBUTTON */ or 9 /* BS_AUTORADIOBUTTON */ or 7 /* BS_GROUPBOX */;
    }

    private static readonly Lazy<DarkModeMenus> Menus = new(DarkModeMenus.CreateForCurrentSystem);

    public static DarkDialogChrome Instance { get; } = new();

    /// <summary>Chủ đề uxtheme của một lớp điều khiển (null = không đổi).</summary>
    internal static string? ThemeFor(DialogControlClass controlClass) => controlClass switch
    {
        DialogControlClass.Button => ButtonTheme,
        DialogControlClass.Edit => EditTheme,
        DialogControlClass.ListBox => ListTheme,
        DialogControlClass.ComboBox => ComboTheme,
        DialogControlClass.ListView => ListViewTheme,
        DialogControlClass.Progress => ClassicTheme,
        DialogControlClass.Trackbar => TrackbarTheme,
        _ => null,
    };

    /// <summary>
    /// Chủ đề của một điều khiển cụ thể: như <see cref="ThemeFor"/>, trừ radio/group box dùng dáng cổ điển (<see cref="ClassicTheme"/>):
    /// chủ đề tối vẽ chữ của chúng xám đậm gần như vô hình trên nền tối (thấy ở ảnh chụp GĐ2), còn dáng cổ điển để chữ do
    /// WM_CTLCOLORSTATIC tô sáng.
    /// </summary>
    internal static string? ThemeForControl(nint hwnd, DialogControlClass controlClass)
        => controlClass == DialogControlClass.Button && IsRadioOrGroupBox(hwnd) ? ClassicTheme : ThemeFor(controlClass);

    /// <summary>Phần con có cửa sổ riêng: danh sách thả của combobox, header của listview.</summary>
    private static unsafe void ApplyParts(DarkModeMenus menus, DialogChromeControl control)
    {
        if (control.Class == DialogControlClass.ComboBox)
        {
            var list = ComboBoxControl.ListWindow(control.Hwnd);
            if (list != 0)
            {
                menus.TryAllowForWindow(list);
                Uxtheme.SetWindowTheme(list, ComboListTheme, null);
            }
        }
        else if (control.Class == DialogControlClass.ListView)
        {
            var header = (nint)User32.SendMessage(control.Hwnd, ControlConstants.LvmGetHeader, 0, 0);
            if (header != 0)
            {
                menus.TryAllowForWindow(header);
                Uxtheme.SetWindowTheme(header, HeaderTheme, null);
            }
        }
    }

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
                var theme = ThemeForControl(control.Hwnd, control.Class);
                if (theme is not null)
                {
                    // "" + "" = không chủ đề (dáng cổ điển); chỉ "" cho sub-app name sẽ rơi về chủ đề sáng mặc định.
                    Uxtheme.SetWindowTheme(control.Hwnd, theme, theme.Length == 0 ? string.Empty : null);
                }

                ApplyParts(menus, control);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Windows không có hàm: hộp thoại hiện nền sáng, không phải lỗi.
        }
    }
}

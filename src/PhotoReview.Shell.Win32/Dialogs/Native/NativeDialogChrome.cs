using PhotoReview.Shell.Interop;
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

    /// <summary>
    /// WP-35 GĐ3a: điều khiển tạo thêm SAU <see cref="Apply"/> (trang Settings tạo lười): chỉ chủ đề điều khiển, không đụng title bar.
    /// Mặc định gọi <see cref="Apply"/> (bản ghi lại của test).
    /// </summary>
    void ApplyControls(nint dialog, IReadOnlyList<DialogChromeControl> controls) => Apply(dialog, controls);
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
/// Giao diện tối của hộp thoại Win32 KHÔNG phụ thuộc chủ đề hệ thống (sửa nguy cơ "nền trắng + chữ sáng" như lỗi menu của PR #492
/// trên Windows 11 build 26300): title bar <c>DWMWA_USE_IMMERSIVE_DARK_MODE</c> (qua <see cref="TitleBar"/>, API công bố) + mọi điều
/// khiển bị xoá chủ đề (<c>SetWindowTheme("", "")</c> = dáng cổ điển) rồi vẽ tay bằng bảng màu <c>Dark.*</c>
/// (<see cref="NativeDialogChromePainter"/>). KHÔNG còn gọi uxtheme ordinal (<c>SetPreferredAppMode</c>/<c>FlushMenuThemes</c>/
/// <c>AllowDarkModeForWindow</c>) và KHÔNG dùng chủ đề <c>DarkMode_*</c>. Nền hộp thoại, chữ và ô nhập tô qua WM_CTLCOLOR* của
/// <see cref="NativeDialog"/>. Không bao giờ ném: thiếu hàm = giữ dáng cổ điển.
/// </summary>
internal sealed class DarkDialogChrome : INativeDialogChrome
{
    /// <summary>"" = không chủ đề (dáng cổ điển): sub-app name VÀ sub-id-list đều rỗng (chỉ "" cho sub-app sẽ rơi về chủ đề sáng mặc định).</summary>
    internal const string ClassicTheme = "";

    public static DarkDialogChrome Instance { get; } = new();

    private readonly Action<nint, string, string?> _setTheme;

    public DarkDialogChrome()
        : this(static (hwnd, app, ids) => Uxtheme.SetWindowTheme(hwnd, app, ids))
    {
    }

    /// <param name="setTheme">Test: ghi lại (HWND, chủ đề, danh sách phụ) thay cho <c>SetWindowTheme</c> thật.</param>
    internal DarkDialogChrome(Action<nint, string, string?> setTheme)
    {
        _setTheme = setTheme ?? throw new ArgumentNullException(nameof(setTheme));
    }

    /// <summary>
    /// Chủ đề uxtheme của một lớp điều khiển: <see cref="ClassicTheme"/> cho mọi lớp có bộ vẽ tay hoặc viền phẳng; null = không đổi
    /// (Static: chữ do WM_CTLCOLORSTATIC tô).
    /// </summary>
    internal static string? ThemeFor(DialogControlClass controlClass) => controlClass switch
    {
        DialogControlClass.Button or DialogControlClass.Edit or DialogControlClass.ListBox or DialogControlClass.ScrollBar
            or DialogControlClass.ComboBox or DialogControlClass.ListView or DialogControlClass.Progress
            or DialogControlClass.Trackbar => ClassicTheme,
        _ => null,
    };

    /// <summary>Chủ đề của một điều khiển cụ thể (hiện chỉ phụ thuộc lớp; giữ chữ ký để trang Settings gọi được).</summary>
    internal static string? ThemeForControl(nint hwnd, DialogControlClass controlClass) => ThemeFor(controlClass);

    public void Apply(nint dialog, IReadOnlyList<DialogChromeControl> controls)
    {
        try
        {
            TitleBar.Apply(dialog);
            NativeDialogChromePainter.AttachDialog(dialog);
            ApplyControlThemes(controls);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Windows không có hàm: hộp thoại giữ dáng cổ điển, không phải lỗi.
        }
    }

    /// <summary>Giao diện cho điều khiển tạo thêm sau khi hộp thoại đã dựng (trang Settings tạo lười).</summary>
    public void ApplyControls(nint dialog, IReadOnlyList<DialogChromeControl> controls)
    {
        try
        {
            ApplyControlThemes(controls);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Windows không có hàm: giữ dáng cổ điển, không phải lỗi.
        }
    }

    private void ApplyControlThemes(IReadOnlyList<DialogChromeControl> controls)
    {
        foreach (var control in controls)
        {
            var theme = ThemeForControl(control.Hwnd, control.Class);
            if (theme is not null)
            {
                _setTheme(control.Hwnd, theme, theme);
            }

            NativeDialogChromePainter.Attach(control.Hwnd, NativeDialogChromePainter.KindFor(control.Hwnd, control.Class));
            ApplyParts(control);
        }
    }

    /// <summary>Phần con có cửa sổ riêng: danh sách thả của combobox, header của listview (cũng bỏ chủ đề, tránh chữ xám của chủ đề).</summary>
    private unsafe void ApplyParts(DialogChromeControl control)
    {
        if (control.Class == DialogControlClass.ComboBox)
        {
            var list = ComboBoxControl.ListWindow(control.Hwnd);
            if (list != 0)
            {
                _setTheme(list, ClassicTheme, ClassicTheme);
            }
        }
        else if (control.Class == DialogControlClass.ListView)
        {
            var header = (nint)User32.SendMessage(control.Hwnd, ControlConstants.LvmGetHeader, 0, 0);
            if (header != 0)
            {
                _setTheme(header, ClassicTheme, ClassicTheme);
                NativeDialogChromePainter.Attach(header, ChromePaintKind.HeaderFiller);
            }
        }
    }
}

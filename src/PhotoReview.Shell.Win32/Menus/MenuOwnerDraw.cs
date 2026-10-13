using PhotoReview.Shell.Win32.Overlay;

namespace PhotoReview.Shell.Win32.Menus;

/// <summary>
/// Phần thuần (không gọi OS) của menu native vẽ tay (owner-draw): màu theo trạng thái và kích thước theo DPI. Menu tự vẽ bằng bảng màu
/// tối của app (<see cref="DarkPalette"/>) nên KHÔNG phụ thuộc uxtheme ordinal 133/135/136 không công bố (trên Windows 11 build 26300
/// bật ForceDark nửa vời cho chữ sáng trên nền trắng: "chỉ hiện màu trắng, rê chuột vào mới hiện nội dung").
/// </summary>
internal static class MenuOwnerDraw
{
    // DRAWITEMSTRUCT.itemState
    internal const uint OdsSelected = 0x0001;
    internal const uint OdsGrayed = 0x0002;
    internal const uint OdsDisabled = 0x0004;
    internal const uint OdsChecked = 0x0008;

    private const double BaseDpi = 96.0;

    /// <summary>Số pixel của <paramref name="px96"/> (kích thước ở 96 DPI) theo <paramref name="dpi"/>; dpi 0 coi như 96. Tối thiểu 1.</summary>
    internal static int Scale(int px96, uint dpi)
    {
        var scale = dpi == 0 ? 1.0 : dpi / BaseDpi;
        return Math.Max(1, (int)Math.Round(px96 * scale));
    }

    /// <summary>Cao một mục thường: chữ + đệm, không thấp hơn 28 px (ở 96 DPI) để chạm chuột dễ.</summary>
    internal static int ItemHeight(int textHeight, uint dpi) => Math.Max(textHeight + Scale(10, dpi), Scale(28, dpi));

    internal static int SeparatorHeight(uint dpi) => Scale(9, dpi);

    /// <summary>Rộng một mục: lề trái cho dấu chọn + chữ + (khoảng cách + phím tắt) + lề phải cho mũi tên submenu.</summary>
    internal static int ItemWidth(int textWidth, int shortcutWidth, uint dpi) =>
        Scale(32, dpi) + textWidth + (shortcutWidth > 0 ? Scale(32, dpi) + shortcutWidth : 0) + Scale(32, dpi);

    internal static int LeftGutter(uint dpi) => Scale(32, dpi);

    internal static int RightPad(uint dpi) => Scale(16, dpi);

    /// <summary>COLORREF (0x00BBGGRR) của khoá "Dark.*".</summary>
    internal static uint ColorRef(string paletteKey)
    {
        var c = DarkPalette.Get(paletteKey);
        static uint Channel(float v) => (uint)Math.Clamp((int)Math.Round(v * 255f), 0, 255);
        return Channel(c.R) | (Channel(c.G) << 8) | (Channel(c.B) << 16);
    }

    /// <summary>Màu nền menu (cũng đặt làm hbrBack của menu để viền/khoảng đệm cùng màu).</summary>
    internal static uint Background => ColorRef("Dark.Panel");

    internal static uint Separator => ColorRef("Dark.Border");

    /// <summary>Nền của một mục theo trạng thái: đang chọn/hover thì sáng hơn nền menu.</summary>
    internal static uint ItemBackground(uint state) => (state & OdsSelected) != 0 ? ColorRef("Dark.ButtonPressed") : Background;

    /// <summary>Chữ của một mục: xám khi tắt (MF_GRAYED/MF_DISABLED), sáng khi bình thường.</summary>
    internal static uint ItemText(uint state) =>
        (state & (OdsGrayed | OdsDisabled)) != 0 ? ColorRef("Dark.TextFaint") : ColorRef("Dark.Text");
}

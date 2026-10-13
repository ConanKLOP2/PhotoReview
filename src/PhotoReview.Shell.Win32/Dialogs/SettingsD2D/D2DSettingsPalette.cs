using PhotoReview.Shell.Rendering;
using PhotoReview.Shell.Win32.Overlay;

namespace PhotoReview.Shell.Win32.Dialogs.SettingsD2D;

/// <summary>
/// SPIKE (WP-35 D2D Settings): bảng màu của trang tự vẽ. Chế độ tối lấy ĐÚNG khoá <c>Dark.*</c> của <see cref="DarkPalette"/> (cùng bảng
/// với <c>DarkDialogChrome</c>/<c>DialogColors</c>); chế độ sáng là màu hệ thống cổ điển (COLOR_BTNFACE...) vì hộp thoại hiện tại ở chế
/// độ "không áp chủ đề" cũng dùng chúng.
/// </summary>
internal sealed record D2DSettingsPalette(
    ColorF Window, ColorF Text, ColorF TextMuted, ColorF TextDisabled, ColorF Input, ColorF InputBorder, ColorF Border, ColorF Button,
    ColorF ButtonHover, ColorF ButtonPressed, ColorF Accent, ColorF Selection, ColorF ScrollTrack, ColorF ScrollThumb, ColorF Focus,
    ColorF Warning)
{
    public static D2DSettingsPalette Dark { get; } = new(
        Window: DarkPalette.Get("Dark.Window"),
        Text: DarkPalette.Get("Dark.WindowText"),
        TextMuted: DarkPalette.Get("Dark.TextSecondary"),
        TextDisabled: DarkPalette.Get("Dark.TextFaint"),
        Input: DarkPalette.Get("Dark.Input"),
        InputBorder: DarkPalette.Get("Dark.BorderStrong"),
        Border: DarkPalette.Get("Dark.Border"),
        Button: DarkPalette.Get("Dark.Button"),
        ButtonHover: DarkPalette.Get("Dark.ButtonHover"),
        ButtonPressed: DarkPalette.Get("Dark.ButtonPressed"),
        Accent: DarkPalette.Get("Dark.Accent"),
        Selection: DarkPalette.Get("Dark.Selection"),
        ScrollTrack: DarkPalette.Get("Dark.ScrollTrack"),
        ScrollThumb: DarkPalette.Get("Dark.BorderStrong"),
        Focus: DarkPalette.Get("Dark.TextStrong"),
        Warning: DarkPalette.Get("Dark.Warning"));

    public static D2DSettingsPalette Classic { get; } = new(
        Window: Rgb(0xF0, 0xF0, 0xF0),
        Text: Rgb(0, 0, 0),
        TextMuted: Rgb(0x60, 0x60, 0x60),
        TextDisabled: Rgb(0x6D, 0x6D, 0x6D),
        Input: Rgb(0xFF, 0xFF, 0xFF),
        InputBorder: Rgb(0x7A, 0x7A, 0x7A),
        Border: Rgb(0xDC, 0xDC, 0xDC),
        Button: Rgb(0xE1, 0xE1, 0xE1),
        ButtonHover: Rgb(0xE5, 0xF1, 0xFB),
        ButtonPressed: Rgb(0xCC, 0xE4, 0xF7),
        Accent: Rgb(0x00, 0x78, 0xD7),
        Selection: Rgb(0x00, 0x78, 0xD7),
        ScrollTrack: Rgb(0xF0, 0xF0, 0xF0),
        ScrollThumb: Rgb(0xCD, 0xCD, 0xCD),
        Focus: Rgb(0, 0, 0),
        Warning: Rgb(0xB0, 0x60, 0x00));

    private static ColorF Rgb(int r, int g, int b) => new(r / 255f, g / 255f, b / 255f, 1f);
}

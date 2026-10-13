using PhotoReview.Shell.Win32.Menus;

namespace PhotoReview.Shell.Tests.Menus;

/// <summary>
/// Menu native tự vẽ (owner-draw): màu và kích thước thuần. Bảo đảm menu luôn nền TỐI + chữ SÁNG theo bảng màu của app, không phụ thuộc
/// uxtheme ordinal/build Windows (bug "chỉ hiện màu trắng, rê chuột vào mới thấy chữ" trên build 26300).
/// </summary>
public sealed class MenuOwnerDrawTests
{
    private static double Luma(uint colorRef)
    {
        var r = colorRef & 0xFF;
        var g = (colorRef >> 8) & 0xFF;
        var b = (colorRef >> 16) & 0xFF;
        return ((0.2126 * r) + (0.7152 * g) + (0.0722 * b)) / 255.0;
    }

    [Fact]
    public void Background_IsDark_AndNormalTextIsLightWithStrongContrast()
    {
        Assert.True(Luma(MenuOwnerDraw.Background) < 0.2);
        Assert.True(Luma(MenuOwnerDraw.ItemText(0)) > 0.8);
        Assert.True(Luma(MenuOwnerDraw.ItemText(0)) - Luma(MenuOwnerDraw.ItemBackground(0)) > 0.6);
    }

    [Fact]
    public void ColorRef_IsBlueGreenRedOrder()
    {
        // Dark.Text = #E5E5E5 (xám nên thứ tự không lộ) -> dùng Dark.Accent #3A7BD5: R=3A G=7B B=D5 => 0xD57B3A.
        Assert.Equal(0xD57B3Au, MenuOwnerDraw.ColorRef("Dark.Accent"));
    }

    [Fact]
    public void SelectedItem_UsesALighterBackgroundThanTheMenu_AndKeepsTextReadable()
    {
        var hover = MenuOwnerDraw.ItemBackground(MenuOwnerDraw.OdsSelected);

        Assert.NotEqual(MenuOwnerDraw.Background, hover);
        Assert.True(Luma(hover) > Luma(MenuOwnerDraw.Background));
        Assert.True(Luma(MenuOwnerDraw.ItemText(MenuOwnerDraw.OdsSelected)) - Luma(hover) > 0.4);
    }

    [Theory]
    [InlineData(MenuOwnerDraw.OdsGrayed)]
    [InlineData(MenuOwnerDraw.OdsDisabled)]
    [InlineData(MenuOwnerDraw.OdsGrayed | MenuOwnerDraw.OdsSelected)]
    public void DisabledItem_IsDimmerThanNormalButStillVisibleOnTheBackground(uint state)
    {
        var text = Luma(MenuOwnerDraw.ItemText(state));

        Assert.True(text < Luma(MenuOwnerDraw.ItemText(0)));
        Assert.True(text - Luma(MenuOwnerDraw.ItemBackground(state)) > 0.15);
    }

    [Fact]
    public void Separator_IsVisibleAgainstTheBackground()
    {
        Assert.True(Luma(MenuOwnerDraw.Separator) > Luma(MenuOwnerDraw.Background));
    }

    [Theory]
    [InlineData(28, 96u, 28)]
    [InlineData(28, 120u, 35)]
    [InlineData(28, 144u, 42)]
    [InlineData(28, 192u, 56)]
    [InlineData(28, 0u, 28)]
    [InlineData(0, 96u, 1)]
    public void Scale_FollowsDpi_AndNeverReturnsLessThanOnePixel(int px96, uint dpi, int expected)
    {
        Assert.Equal(expected, MenuOwnerDraw.Scale(px96, dpi));
    }

    [Theory]
    [InlineData(16, 96u, 28)] // chữ thấp: sàn 28 px
    [InlineData(24, 96u, 34)] // chữ cao: chữ + đệm 10 px
    [InlineData(16, 192u, 56)]
    public void ItemHeight_IsTextPlusPadding_WithATouchFriendlyFloor(int textHeight, uint dpi, int expected)
    {
        Assert.Equal(expected, MenuOwnerDraw.ItemHeight(textHeight, dpi));
    }

    [Fact]
    public void ItemWidth_AddsGutterShortcutGapAndArrowPad_OnlyWhenThereIsAShortcut()
    {
        Assert.Equal(32 + 100 + 32, MenuOwnerDraw.ItemWidth(100, 0, 96));
        Assert.Equal(32 + 100 + 32 + 40 + 32, MenuOwnerDraw.ItemWidth(100, 40, 96));
        Assert.Equal(64 + 200 + 64, MenuOwnerDraw.ItemWidth(200, 0, 192));
    }

    [Fact]
    public void SeparatorHeight_IsShorterThanAnItem()
    {
        Assert.Equal(9, MenuOwnerDraw.SeparatorHeight(96));
        Assert.True(MenuOwnerDraw.SeparatorHeight(96) < MenuOwnerDraw.ItemHeight(0, 96));
    }
}

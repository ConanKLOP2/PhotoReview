using PhotoReview.App.Input;
using PhotoReview.Shell.Rendering;
using PhotoReview.Shell.Win32.Overlay;

namespace PhotoReview.Shell.Tests.Overlay;

/// <summary>
/// WP-17: bố cục (đo -> đặt), căn lề, lề/đệm, MaxWidth, cắt chữ, ẩn = collapsed, hit-test bỏ qua phần tử opacity 0,
/// thứ tự vẽ - với bộ đo chữ giả tất định (không GPU, không DirectWrite).
/// </summary>
[Trait("Category", "HotPath")]
public sealed class OverlayLayoutTests
{
    private static OverlayLayoutContext Context(FakeTextRenderer text, double width = 800, double height = 600, double dpi = 1) =>
        new(new SizeD(width, height), dpi, text);

    private static TextElement Text(string id, string text, double size = 10) =>
        new(id) { Text = text, Style = new TextStyle("Segoe UI", size) };

    private static PanelElement Badge(string id, string text, OverlayHorizontalAlignment h, OverlayVerticalAlignment v) =>
        new(id)
        {
            HorizontalAlignment = h,
            VerticalAlignment = v,
            Margin = new OverlayThickness(8),
            Padding = new OverlayThickness(6, 4),
            CornerRadius = 4,
        };

    [Fact]
    public void Panel_SizesToTextPlusPadding_AndAnchorsToCorners()
    {
        var fake = new FakeTextRenderer();
        // "ABCDEFGHIJ" = 10 ký tự x 5 = 50 rộng, 15 cao.
        PanelElement bottomLeft = Badge("A", "", OverlayHorizontalAlignment.Left, OverlayVerticalAlignment.Bottom).Add(Text("a", "ABCDEFGHIJ"));
        PanelElement topRight = Badge("B", "", OverlayHorizontalAlignment.Right, OverlayVerticalAlignment.Top).Add(Text("b", "ABCDEFGHIJ"));
        PanelElement center = Badge("C", "", OverlayHorizontalAlignment.Center, OverlayVerticalAlignment.Top).Add(Text("c", "ABCDEFGHIJ"));
        center.Margin = new OverlayThickness(8, 44, 8, 8);

        foreach (PanelElement p in new[] { bottomLeft, topRight, center })
        {
            p.Arrange(Context(fake));
        }

        Assert.Equal(new RectD(8, 600 - 8 - 23, 62, 23), bottomLeft.Bounds);
        Assert.Equal(new RectD(800 - 8 - 62, 8, 62, 23), topRight.Bounds);
        Assert.Equal(new RectD(369, 44, 62, 23), center.Bounds);
    }

    [Fact]
    public void StretchPanel_FillsClientMinusMargin()
    {
        var fake = new FakeTextRenderer();
        var compare = new PanelElement("ComparePanel") { Margin = new OverlayThickness(8) };

        compare.Arrange(Context(fake, 1366, 728));

        Assert.Equal(new RectD(8, 8, 1350, 712), compare.Bounds);
    }

    [Fact]
    public void VerticalStack_WidthIsWidestChild_HeightIsSumWithMargins()
    {
        var fake = new FakeTextRenderer();
        var status = Text("StatusText", "IMG_0042.JPG", 12);                    // 12 ký tự x 6 = 72, cao 18
        var exif = Text("ExifInfoText", "Canon EOS R5 85 mm f/1.8", 11);       // 24 x 5,5 = 132, cao 16,5
        exif.Margin = new OverlayThickness(0, 2, 0, 0);
        var panel = Badge("StatusPanel", "", OverlayHorizontalAlignment.Left, OverlayVerticalAlignment.Bottom).Add(status).Add(exif);

        panel.Arrange(Context(fake));

        Assert.Equal(132 + 12, panel.Bounds.Width, 6);
        Assert.Equal(18 + 2 + 16.5 + 8, panel.Bounds.Height, 6);
        Assert.Equal(8 + 6, status.Bounds.X, 6);
        Assert.Equal(panel.Bounds.Y + 4, status.Bounds.Y, 6);
        Assert.Equal(panel.Bounds.Y + 4 + 18 + 2, exif.Bounds.Y, 6);
        // Con Stretch: rộng bằng vùng nội dung của panel.
        Assert.Equal(132, status.Bounds.Width, 6);
    }

    [Fact]
    public void MaxWidth_CapsThePanel_AndTextIsTrimmedToTheRemainder()
    {
        var fake = new FakeTextRenderer();
        var folder = Text("FolderInfoText", new string('x', 400), 10);          // 400 x 5 = 2000 rộng
        folder.Trimming = TextTrimming.CharacterEllipsis;
        var panel = Badge("FolderInfoPanel", "", OverlayHorizontalAlignment.Right, OverlayVerticalAlignment.Bottom).Add(folder);
        panel.MaxWidth = 640;

        panel.Arrange(Context(fake, 1920, 1080));

        // Chữ cắt còn 625 (làm tròn xuống theo ký tự 5 DIP của bộ đo giả) + 12 đệm; không vượt MaxWidth.
        Assert.Equal(637, panel.Bounds.Width, 6);
        Assert.Equal(1920 - 8 - 637, panel.Bounds.X, 6);
        // Chữ được đo với maxWidth = 640 - 12 (đệm).
        Assert.Equal(628, fake.Requests[^1].MaxWidth, 6);
        Assert.Equal(TextTrimming.CharacterEllipsis, fake.Requests[^1].Trimming);
        Assert.True(folder.Bounds.Width <= 628);
    }

    [Fact]
    public void PanelNarrowerThanClient_TextGetsOnlyAvailableWidth()
    {
        var fake = new FakeTextRenderer();
        var text = Text("t", new string('y', 100), 10);                          // 500 rộng
        text.Trimming = TextTrimming.CharacterEllipsis;
        var panel = Badge("P", "", OverlayHorizontalAlignment.Left, OverlayVerticalAlignment.Top).Add(text);

        panel.Arrange(Context(fake, 300, 200));

        // 300 - 16 (lề) - 12 (đệm) = 272.
        Assert.Equal(272, fake.Requests[^1].MaxWidth, 6);
        Assert.Equal(270 + 12, panel.Bounds.Width, 6);
        Assert.True(panel.Bounds.Right <= 300 - 8);
    }

    [Fact]
    public void HiddenElement_IsCollapsed_NoSpaceNoDrawNoHit()
    {
        var fake = new FakeTextRenderer();
        var first = Text("a", "AAAA", 10);
        var hidden = Text("b", "BBBBBBBB", 10);
        hidden.IsVisible = false;
        var panel = Badge("P", "", OverlayHorizontalAlignment.Left, OverlayVerticalAlignment.Top).Add(first).Add(hidden);
        var dc = new RecordingDrawContext();

        panel.Arrange(Context(fake));
        panel.Render(dc);

        Assert.Equal(20 + 12, panel.Bounds.Width, 6);
        Assert.Equal(15 + 8, panel.Bounds.Height, 6);
        Assert.Single(dc.Texts);
        Assert.Equal("AAAA", dc.Texts[0].Text);
        Assert.Equal(0, hidden.Bounds.Width);
        Assert.False(hidden.HitTest(new PointD(hidden.Bounds.X, hidden.Bounds.Y)));
    }

    [Fact]
    public void HiddenPanel_DrawsNothing_AndBalancesNothing()
    {
        var fake = new FakeTextRenderer();
        var panel = Badge("P", "", OverlayHorizontalAlignment.Left, OverlayVerticalAlignment.Top).Add(Text("a", "AAAA"));
        panel.IsVisible = false;
        var dc = new RecordingDrawContext();

        panel.Arrange(Context(fake));
        panel.Render(dc);

        Assert.Empty(dc.Calls);
        Assert.Equal(0, panel.Bounds.Width);
    }

    [Fact]
    public void Render_FillsRoundedBackground_ThenText_WithBalancedOpacityAndClip()
    {
        var fake = new FakeTextRenderer();
        var panel = Badge("P", "", OverlayHorizontalAlignment.Left, OverlayVerticalAlignment.Top).Add(Text("a", "AAAA"));
        panel.Opacity = 0.5f;
        var dc = new RecordingDrawContext();

        panel.Arrange(Context(fake));
        panel.Render(dc);

        Assert.Equal(["PushOpacity", "FillRoundedRectangle", "PushOpacity", "PushClip", "DrawText", "PopClip", "PopOpacity", "PopOpacity"], dc.Calls);
        Assert.Equal(4, dc.Fills[0].Radius);
        Assert.Equal(DarkPalette.Overlay, dc.Fills[0].Color);
        Assert.Equal(0.5f, dc.Fills[0].Opacity);
        Assert.Equal(0, dc.OpacityDepth);
        Assert.Equal(0, dc.ClipDepth);
        Assert.Equal(DarkPalette.TextBright, dc.Texts[0].Color);
        Assert.Equal(new PointD(14, 12), dc.Texts[0].Origin);
    }

    [Fact]
    public void ZeroOpacity_SkipsDrawing()
    {
        var fake = new FakeTextRenderer();
        var panel = Badge("P", "", OverlayHorizontalAlignment.Left, OverlayVerticalAlignment.Top).Add(Text("a", "AAAA"));
        panel.Opacity = 0f;
        var dc = new RecordingDrawContext();

        panel.Arrange(Context(fake));
        panel.Render(dc);

        Assert.Empty(dc.Calls);
    }

    [Fact]
    public void Layout_IsIndependentOfDpiScale_ButTextRendererStaysTheSame()
    {
        var fake = new FakeTextRenderer();
        PanelElement Build() => Badge("P", "", OverlayHorizontalAlignment.Right, OverlayVerticalAlignment.Bottom).Add(Text("a", "ZZZZZZ", 12));
        PanelElement at1 = Build();
        PanelElement at15 = Build();

        at1.Arrange(Context(fake, 800, 600, 1.0));
        at15.Arrange(Context(fake, 800, 600, 1.5));

        Assert.Equal(at1.Bounds, at15.Bounds);
    }

    [Fact]
    public void TextElement_ReusesLayout_UntilStyleTextOrWidthChanges_AndReleasesOnDispose()
    {
        var fake = new FakeTextRenderer();
        var text = Text("a", "hello", 10);
        var panel = Badge("P", "", OverlayHorizontalAlignment.Left, OverlayVerticalAlignment.Top).Add(text);
        var ctx = Context(fake);

        panel.Arrange(ctx);
        panel.Arrange(ctx);
        Assert.Single(fake.Requests);
        Assert.Equal(1, fake.Live);

        text.Text = "hello!";
        Assert.Equal(0, fake.Live);   // layout cũ trả ngay khi đổi chữ
        panel.Arrange(ctx);
        Assert.Equal(2, fake.Requests.Count);

        panel.Arrange(Context(fake, 400, 300));   // đổi chiều rộng -> đo lại
        Assert.Equal(3, fake.Requests.Count);
        Assert.Equal(1, fake.Live);

        text.Text = "hello!"; // cùng chuỗi: không đổi
        panel.Arrange(Context(fake, 400, 300));
        Assert.Equal(3, fake.Requests.Count);

        panel.ReleaseResources();
        Assert.Equal(0, fake.Live);
    }

    [Fact]
    public void Button_SizeIncludesPaddingAndBorder_AndHitTestFollowsVisibility()
    {
        var fake = new FakeTextRenderer();
        var button = new ButtonElement("Fit") { Content = "Fit", Style = new TextStyle("Segoe UI", 10), HorizontalAlignment = OverlayHorizontalAlignment.Left, VerticalAlignment = OverlayVerticalAlignment.Top };
        int clicks = 0;
        button.Clicked += (_, _) => clicks++;

        button.Arrange(Context(fake));

        // "Fit" = 15 x 15; + padding 8,4 + viền 1.
        Assert.Equal(new RectD(0, 0, 15 + 16 + 2, 15 + 8 + 2), button.Bounds);
        Assert.True(button.HitTest(new PointD(5, 5)));
        Assert.False(button.HitTest(new PointD(50, 5)));
        Assert.True(button.Click());
        button.IsEnabled = false;
        Assert.False(button.Click());
        button.IsEnabled = true;
        button.Opacity = 0f;
        Assert.False(button.HitTest(new PointD(5, 5)));
        Assert.False(button.Click());
        Assert.Equal(1, clicks);
    }

    [Fact]
    public void Button_Render_PicksBackgroundByState()
    {
        var fake = new FakeTextRenderer();
        var button = new ButtonElement("B") { Content = "OK", Style = new TextStyle("Segoe UI", 10) };
        button.Arrange(Context(fake));

        ColorF Background(bool hot, bool pressed, bool enabled = true)
        {
            button.IsHot = hot;
            button.IsPressed = pressed;
            button.IsEnabled = enabled;
            var dc = new RecordingDrawContext();
            button.Render(dc);
            return dc.Fills[0].Color;
        }

        Assert.Equal(DarkPalette.Button, Background(false, false));
        Assert.Equal(DarkPalette.ButtonHover, Background(true, false));
        Assert.Equal(DarkPalette.ButtonPressed, Background(true, true));
        Assert.Equal(DarkPalette.Button, Background(true, true, enabled: false));
    }

    [Fact]
    public void VerticalCenter_PlacesPanelInTheMiddleOfTheClient()
    {
        var fake = new FakeTextRenderer();
        PanelElement panel = Badge("P", "", OverlayHorizontalAlignment.Left, OverlayVerticalAlignment.Center).Add(Text("a", "ABCDEFGHIJ"));

        panel.Arrange(Context(fake));

        // Cao 23 (15 chữ + 8 đệm), lề 8: y = 8 + (584 - 23) / 2.
        Assert.Equal(new RectD(8, 288.5, 62, 23), panel.Bounds);
    }

    [Fact]
    public void MaxWidth_CapsTheDesiredWidth_EvenWhenAChildCannotShrink()
    {
        var fake = new FakeTextRenderer();
        // Nút luôn một dòng không cắt: 40 x 5 + 16 + 2 = 218 DIP bất kể chỗ cho phép.
        var button = new ButtonElement("B") { Content = new string('x', 40), Style = new TextStyle("Segoe UI", 10) };
        var panel = new PanelElement("P")
        {
            HorizontalAlignment = OverlayHorizontalAlignment.Left,
            VerticalAlignment = OverlayVerticalAlignment.Top,
            MaxWidth = 100,
        }.Add(button);

        panel.Arrange(Context(fake));

        Assert.Equal(100, panel.DesiredSize.Width, 6);
        Assert.Equal(100, panel.Bounds.Width, 6);
    }

    [Fact]
    public void StretchPanel_WithMaxWidth_IsCappedInAWiderClient()
    {
        var fake = new FakeTextRenderer();
        var panel = new PanelElement("P") { MaxWidth = 300 };

        panel.Arrange(Context(fake, 800, 600));

        Assert.Equal(new RectD(0, 0, 300, 600), panel.Bounds);
    }

    [Fact]
    public void HorizontalPanel_PlacesChildrenSideBySide()
    {
        var fake = new FakeTextRenderer();
        TextElement a = Text("a", "AAAA");      // 20 x 15
        TextElement b = Text("b", "BBBBBB");    // 30 x 15
        var panel = new PanelElement("P")
        {
            Orientation = OverlayOrientation.Horizontal,
            HorizontalAlignment = OverlayHorizontalAlignment.Left,
            VerticalAlignment = OverlayVerticalAlignment.Top,
        }.Add(a).Add(b);

        panel.Arrange(Context(fake));

        Assert.Equal(new RectD(0, 0, 20, 15), a.Bounds);
        Assert.Equal(new RectD(20, 0, 30, 15), b.Bounds);
        Assert.Equal(new RectD(0, 0, 50, 15), panel.Bounds);
    }

    [Fact]
    public void FindAt_PrefersTheLaterSibling_WhereChildrenOverlap()
    {
        var fake = new FakeTextRenderer();
        var lower = new ButtonElement("Lower") { Content = "AAAA", Style = new TextStyle("Segoe UI", 10) };
        var upper = new ButtonElement("Upper")
        {
            Content = "BBBB",
            Style = new TextStyle("Segoe UI", 10),
            Margin = new OverlayThickness(0, -10, 0, 0),   // lùi 10 DIP: chồng lên nút dưới ở y 15..25
        };
        var panel = new PanelElement("P")
        {
            HorizontalAlignment = OverlayHorizontalAlignment.Left,
            VerticalAlignment = OverlayVerticalAlignment.Top,
        }.Add(lower).Add(upper);

        panel.Arrange(Context(fake));

        var overlap = new PointD(2, 20);
        Assert.True(lower.HitTest(overlap));
        Assert.True(upper.HitTest(overlap));
        Assert.Same(upper, panel.FindAt(overlap));   // phần tử thêm sau nằm trên
    }

    [Fact]
    public void TextElement_IsClickThroughByDefault()
    {
        var fake = new FakeTextRenderer();
        TextElement text = Text("t", "hello");

        text.Arrange(Context(fake));

        Assert.False(text.IsHitTestVisible);
        Assert.False(text.HitTest(new PointD(1, 1)));
        Assert.Null(text.FindAt(new PointD(1, 1)));
    }

    [Fact]
    public void TextElement_RemeasuresWithANewRenderer_AndReleasesTheOldLayout()
    {
        var first = new FakeTextRenderer();
        var second = new FakeTextRenderer();
        TextElement text = Text("a", "hello");

        text.Arrange(Context(first));
        Assert.Equal(1, first.Live);
        text.Arrange(Context(second));

        Assert.Equal(0, first.Live);
        Assert.Single(second.Requests);
        Assert.Equal(1, second.Live);
    }
}

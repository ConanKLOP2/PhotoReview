using PhotoReview.App.Input;
using PhotoReview.Shell.Rendering;
using PhotoReview.Shell.Win32.Overlay;

namespace PhotoReview.Shell.Tests.Overlay;

/// <summary>WP-17: z-order, hit-test (click xuyên khi ẩn/opacity 0), tra theo Id, Animator + host.</summary>
[Trait("Category", "HotPath")]
public sealed class OverlayHostTests
{
    private static OverlayLayoutContext Context(FakeTextRenderer fake) => new(new SizeD(800, 600), 1, fake);

    private static PanelElement Corner(string id, OverlayHorizontalAlignment h, OverlayVerticalAlignment v, string text) =>
        new PanelElement(id)
        {
            HorizontalAlignment = h,
            VerticalAlignment = v,
            Margin = new OverlayThickness(8),
            Padding = new OverlayThickness(6, 4),
        }.Add(new TextElement(id + ".Text") { Text = text, Style = new TextStyle("Segoe UI", 10) });

    [Fact]
    public void Add_RejectsDuplicateIds_AcrossTrees()
    {
        using var host = new OverlayHost();
        host.Add(Corner("StatusPanel", OverlayHorizontalAlignment.Left, OverlayVerticalAlignment.Bottom, "a"));

        Assert.Throws<ArgumentException>(() => host.Add(Corner("StatusPanel", OverlayHorizontalAlignment.Left, OverlayVerticalAlignment.Top, "b")));
        Assert.Throws<ArgumentException>(() => host.Add(new PanelElement("StatusPanel.Text")));
    }

    [Fact]
    public void HitTest_ReturnsTopmostDeepestElement_AndNullOverImage()
    {
        var fake = new FakeTextRenderer();
        using var host = new OverlayHost();
        PanelElement status = Corner("StatusPanel", OverlayHorizontalAlignment.Left, OverlayVerticalAlignment.Bottom, "status");
        var button = new ButtonElement("Fit") { Content = "Fit", Style = new TextStyle("Segoe UI", 10) };
        PanelElement toolbar = new PanelElement("ToolbarPanel") { Margin = new OverlayThickness(8), HorizontalAlignment = OverlayHorizontalAlignment.Left, VerticalAlignment = OverlayVerticalAlignment.Top }.Add(button);
        host.Add(status);
        host.Add(toolbar);
        host.Arrange(Context(fake));

        Assert.Same(button, host.HitTest(new PointD(button.Bounds.X + 1, button.Bounds.Y + 1)));
        PointD insideStatus = new(status.Bounds.X + 1, status.Bounds.Y + 1);
        Assert.Same(status, host.HitTest(insideStatus));      // chữ không nhận chuột: panel nhận
        Assert.Null(host.HitTest(new PointD(400, 300)));      // ảnh bên dưới
    }

    [Fact]
    public void HitTest_SkipsHiddenAndZeroOpacityElements_SoClicksFallThrough()
    {
        var fake = new FakeTextRenderer();
        using var host = new OverlayHost();
        PanelElement panel = Corner("ZoomIndicatorPanel", OverlayHorizontalAlignment.Right, OverlayVerticalAlignment.Top, "100 %");
        host.Add(panel);
        host.Arrange(Context(fake));
        PointD inside = new(panel.Bounds.X + 2, panel.Bounds.Y + 2);
        Assert.Same(panel, host.HitTest(inside));

        panel.Opacity = 0f;
        Assert.Null(host.HitTest(inside));

        panel.Opacity = 1f;
        panel.IsVisible = false;
        Assert.Null(host.HitTest(inside));

        panel.IsVisible = true;
        panel.IsHitTestVisible = false;
        Assert.Null(host.HitTest(inside));
    }

    [Fact]
    public void Render_DrawsRootsInInsertionOrder()
    {
        var fake = new FakeTextRenderer();
        using var host = new OverlayHost();
        host.Add(Corner("First", OverlayHorizontalAlignment.Left, OverlayVerticalAlignment.Top, "one"));
        host.Add(Corner("Second", OverlayHorizontalAlignment.Right, OverlayVerticalAlignment.Top, "two"));
        host.Arrange(Context(fake));
        var dc = new RecordingDrawContext();

        host.Render(dc);

        Assert.Equal(["one", "two"], dc.Texts.Select(t => t.Text));
    }

    [Fact]
    public void Find_And_Resolve_WalkTheWholeTree()
    {
        using var host = new OverlayHost();
        PanelElement panel = Corner("FolderInfoPanel", OverlayHorizontalAlignment.Right, OverlayVerticalAlignment.Bottom, "x");
        host.Add(panel);

        Assert.Same(panel, host.Find("FolderInfoPanel"));
        Assert.NotNull(host.Find("FolderInfoPanel.Text"));
        Assert.Null(host.Find("Missing"));
        Assert.Same(panel, host.Resolve("FolderInfoPanel"));
    }

    [Fact]
    public void AnimatorOnHost_FadesPanelAndStopsHitTestingAtZero()
    {
        var fake = new FakeTextRenderer();
        var clock = new FakeFrameClock();
        using var host = new OverlayHost();
        PanelElement toolbar = Corner("ToolbarPanel", OverlayHorizontalAlignment.Left, OverlayVerticalAlignment.Top, "tools");
        host.Add(toolbar);
        host.Arrange(Context(fake));
        PointD inside = new(toolbar.Bounds.X + 2, toolbar.Bounds.Y + 2);
        using var animator = new Animator(clock, host.Resolve, 1000);

        animator.FadeTo("ToolbarPanel", 0f, TimeSpan.FromMilliseconds(200), Easing.Linear);
        clock.Tick(0);
        clock.Tick(100);
        Assert.Equal(0.5f, toolbar.Opacity, 3);
        Assert.Same(toolbar, host.HitTest(inside));
        clock.Tick(200);

        Assert.Equal(0f, toolbar.Opacity);
        Assert.Null(host.HitTest(inside));
    }

    [Fact]
    public void Dispose_ReleasesEveryTextLayout()
    {
        var fake = new FakeTextRenderer();
        var host = new OverlayHost();
        host.Add(Corner("A", OverlayHorizontalAlignment.Left, OverlayVerticalAlignment.Top, "one"));
        host.Add(Corner("B", OverlayHorizontalAlignment.Right, OverlayVerticalAlignment.Top, "two"));
        host.Arrange(Context(fake));
        Assert.Equal(2, fake.Live);

        host.Dispose();
        host.Dispose();

        Assert.Equal(0, fake.Live);
        Assert.Throws<ObjectDisposedException>(() => host.Add(new PanelElement("C")));
    }
}


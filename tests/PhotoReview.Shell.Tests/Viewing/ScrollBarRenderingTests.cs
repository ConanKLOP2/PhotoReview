using PhotoReview.App.Input;
using PhotoReview.Shell.Rendering;
using PhotoReview.Shell.Tests.Rendering;
using PhotoReview.Shell.Win32.Viewing;

namespace PhotoReview.Shell.Tests.Viewing;

/// <summary>
/// WP-26 trên renderer D2D THẬT (WARP offscreen, đọc lại pixel): nền thanh cuộn, con trượt (màu thường/hover/kéo), ô góc, vùng
/// ngoài là canvas, và ComparePanel che thanh cuộn chỉ chừa dải lề. Client 200x120 DIP, ảnh 600x400 ở 100 % (cả hai thanh hiện).
/// </summary>
[Trait("Category", "Native")]
public sealed class ScrollBarRenderingTests : IDisposable
{
    private static readonly ColorF Canvas = new(0x10 / 255f, 0x10 / 255f, 0x10 / 255f, 1f);

    private readonly List<IDisposable> _owned = [];

    public void Dispose()
    {
        foreach (var item in Enumerable.Reverse(_owned))
        {
            item.Dispose();
        }
    }

    private ScrollBarRig NewRig()
    {
        var rig = new ScrollBarRig(200, 120, 600, 400);
        _owned.Add(rig);
        return rig;
    }

    private PixelBufferHolder Render(ScrollBarRig rig)
    {
        var surface = D2DRenderSurface.CreateOffscreenSurface(200, 120, 1);
        _owned.Add(surface);
        var pixels = RenderTestImages.Render(surface, dc =>
        {
            dc.Clear(Canvas);
            rig.Bars.Draw(dc);
        });
        _owned.Add(pixels);
        return new PixelBufferHolder(pixels);
    }

    private sealed class PixelBufferHolder(PhotoReview.Imaging.Pixels.PixelBuffer pixels)
    {
        public (int R, int G, int B) At(double x, double y)
        {
            var (b, g, r, _) = RenderTestImages.Pixel(pixels, (int)x, (int)y);
            return (r, g, b);
        }
    }

    private static void AssertGray(int expected, (int R, int G, int B) actual, string what)
    {
        Assert.True(Math.Abs(actual.R - expected) <= 1 && Math.Abs(actual.G - expected) <= 1 && Math.Abs(actual.B - expected) <= 1,
            $"{what}: mong đợi xám {expected}, thấy {actual}");
    }

    [Fact]
    public void RendersTrackThumbCornerAndLeavesTheRestUntouched()
    {
        var rig = NewRig();
        var layout = rig.Layout;
        Assert.True(layout.Vertical.Visible && layout.Horizontal.Visible);
        var verticalThumb = layout.Vertical.Thumb;
        var horizontalThumb = layout.Horizontal.Thumb;

        var pixels = Render(rig);

        AssertGray(0x10, pixels.At(100, 50), "canvas ngoài thanh cuộn");
        AssertGray(0x16, pixels.At(layout.Vertical.Bar.X + 0.5, 3), "nền thanh dọc (lề trái của Track)");
        AssertGray(0x16, pixels.At(layout.Vertical.Bar.X + 5, layout.Vertical.Track.Bottom + 0.5), "nền thanh dọc (lề dưới)");
        AssertGray(0x4A, pixels.At(layout.Vertical.Bar.X + 5, verticalThumb.Y + (verticalThumb.Height / 2)), "con trượt dọc");
        AssertGray(0x16, pixels.At(layout.Vertical.Bar.X + 5, verticalThumb.Bottom + 5), "rãnh dưới con trượt dọc");
        AssertGray(0x4A, pixels.At(horizontalThumb.X + (horizontalThumb.Width / 2), layout.Horizontal.Bar.Y + 5), "con trượt ngang");
        AssertGray(0x16, pixels.At(horizontalThumb.Right + 8, layout.Horizontal.Bar.Y + 5), "rãnh sau con trượt ngang");
        AssertGray(0x16, pixels.At(layout.Corner.X + 5, layout.Corner.Y + 5), "ô góc");
    }

    [Fact]
    public void ThumbTurnsHoverGrayWhenTheCursorIsOverIt_AndPressedGrayWhileDragging()
    {
        var rig = NewRig();
        var thumb = rig.Layout.Vertical.Thumb;
        var x = thumb.X + 4;
        var y = thumb.Y + (thumb.Height / 2);

        rig.Move(x, y, leftDown: false);
        AssertGray(0x6A, Render(rig).At(x, y), "hover");

        rig.Down(x, y);
        AssertGray(0x7A, Render(rig).At(x, y), "đang kéo");

        rig.Up(x, y);
        rig.Move(20, 20, leftDown: false);
        AssertGray(0x4A, Render(rig).At(x, y), "sau khi rời");
    }

    [Fact]
    public void TheThumbHasRoundedEnds_AndTheSquareCornersOfItsBoxStayTrackColored()
    {
        var rig = NewRig();
        var thumb = rig.Layout.Vertical.Thumb;
        var pixels = Render(rig);

        // Bán kính 4 trên con trượt rộng 8: góc trên-trái của hộp (cách mép 0,5 DIP) chưa phủ màu con trượt.
        AssertGray(0x16, pixels.At(thumb.X + 0.5, thumb.Y + 0.5), "góc trên của hộp con trượt");
        AssertGray(0x4A, pixels.At(thumb.X + 4, thumb.Y + 4), "tâm đầu bo tròn");
    }

    [Fact]
    public void WithTheComparePanelVisible_OnlyTheStripsOutsideItsMarginShowTheBars()
    {
        var rig = NewRig();
        rig.CompareVisible = true;
        var layout = rig.Layout;
        var thumb = layout.Vertical.Thumb;
        var surface = D2DRenderSurface.CreateOffscreenSurface(200, 120, 1);
        _owned.Add(surface);
        var pixels = RenderTestImages.Render(surface, dc =>
        {
            dc.Clear(Canvas);
            dc.FillRectangle(new RectD(8, 8, 184, 104), new ColorF(1f, 0f, 0f, 1f)); // ComparePanel (lề 8) đã phủ sẵn
            rig.Bars.Draw(dc);
        });
        _owned.Add(pixels);
        var holder = new PixelBufferHolder(pixels);

        // Trong panel (x < 192): vẫn là màu panel dù thanh cuộn nằm ở x 190..200.
        Assert.Equal((255, 0, 0), holder.At(layout.Vertical.Bar.X + 1, thumb.Y + (thumb.Height / 2)));
        // Ngoài panel (x >= 192): thanh cuộn hiện.
        AssertGray(0x4A, holder.At(195, thumb.Y + (thumb.Height / 2)), "dải phải của con trượt");
        AssertGray(0x16, holder.At(100, 115), "dải dưới của thanh ngang");
    }
}

using PhotoReview.App.Input;
using PhotoReview.Shell.Win32.Viewing;

namespace PhotoReview.Shell.Tests.Viewing;

/// <summary>
/// WP-21: bố cục compare = <c>ComparePanel</c> của MainWindow.xaml (Margin 8; hai cột bằng nhau; ô Margin 4 + viền 2; ảnh
/// Uniform). Số được tính tay từ XAML, không từ chính hàm.
/// </summary>
[Trait("Category", "HotPath")]
public sealed class CompareLayoutTests
{
    private static readonly SizeD Client = new(1200, 800);

    [Fact]
    public void Panel_IsTheClientInsetBy8OnEverySide()
    {
        Assert.Equal(new RectD(8, 8, 1184, 784), CompareLayout.Panel(Client));
    }

    [Fact]
    public void Borders_AreEqualColumnsInsetBy4()
    {
        // Cột = 1184 / 2 = 592; ô trái bắt đầu ở 8, ô phải ở 600; mỗi ô co 4 mỗi cạnh.
        Assert.Equal(new RectD(12, 12, 584, 776), CompareLayout.Border(Client, CompareSide.Left));
        Assert.Equal(new RectD(604, 12, 584, 776), CompareLayout.Border(Client, CompareSide.Right));
    }

    [Fact]
    public void Content_IsInsideTheTwoDipBorder()
    {
        Assert.Equal(new RectD(14, 14, 580, 772), CompareLayout.Content(Client, CompareSide.Left));
        Assert.Equal(new RectD(606, 14, 580, 772), CompareLayout.Content(Client, CompareSide.Right));
    }

    [Fact]
    public void Image_Landscape_IsUniformFitToWidthAndCenteredVertically()
    {
        // 4000x3000 vào 580x772: scale = min(580/4000, 772/3000) = 0.145 -> 580 x 435; y = 14 + (772 - 435) / 2.
        var image = CompareLayout.Image(Client, CompareSide.Left, 4000, 3000);

        Assert.Equal(14, image.X, 6);
        Assert.Equal(580, image.Width, 6);
        Assert.Equal(435, image.Height, 6);
        Assert.Equal(14 + ((772 - 435) / 2.0), image.Y, 6);
    }

    [Fact]
    public void Image_Portrait_IsUniformFitToHeightAndCenteredHorizontally()
    {
        // 3000x4000 vào 580x772: scale = min(0.1933, 0.193) = 0.193 -> 579 x 772; căn giữa ngang trong ô PHẢI.
        var image = CompareLayout.Image(Client, CompareSide.Right, 3000, 4000);

        Assert.Equal(772, image.Height, 6);
        Assert.Equal(579, image.Width, 6);
        Assert.Equal(14, image.Y, 6);
        Assert.Equal(606 + ((580 - 579) / 2.0), image.X, 6);
    }

    [Fact]
    public void Image_SmallerThanTheCell_IsEnlargedToFit()
    {
        // Stretch=Uniform phóng to cả ảnh nhỏ: 58x77 -> x10.
        var image = CompareLayout.Image(Client, CompareSide.Left, 58, 77);

        Assert.Equal(580, image.Width, 6);
        Assert.Equal(770, image.Height, 6);
    }

    [Fact]
    public void Image_EmptyBitmapOrEmptyCell_IsEmpty()
    {
        Assert.Equal(0, CompareLayout.Image(Client, CompareSide.Left, 0, 100).Width);
        Assert.Equal(0, CompareLayout.Image(new SizeD(10, 10), CompareSide.Left, 100, 100).Width);
        Assert.Equal(0, CompareLayout.Panel(new SizeD(5, 5)).Width);
    }

    [Fact]
    public void HitTest_BordersCountOnTheGapAndMarginDoNot()
    {
        Assert.Equal(CompareSide.Left, CompareLayout.HitTest(Client, new PointD(12, 12)));      // góc ngoài của viền
        Assert.Equal(CompareSide.Left, CompareLayout.HitTest(Client, new PointD(300, 400)));
        Assert.Equal(CompareSide.Right, CompareLayout.HitTest(Client, new PointD(700, 400)));
        Assert.Equal(CompareSide.Right, CompareLayout.HitTest(Client, new PointD(1187, 787)));  // viền ngoài phải-dưới
        Assert.Null(CompareLayout.HitTest(Client, new PointD(600, 400)));                        // khe giữa hai ô (4 + 4)
        Assert.Null(CompareLayout.HitTest(Client, new PointD(5, 5)));                            // lề panel
        Assert.Null(CompareLayout.HitTest(Client, new PointD(1195, 400)));
    }

    [Fact]
    public void HitTest_TinyClient_NeverHits()
    {
        Assert.Null(CompareLayout.HitTest(new SizeD(10, 10), new PointD(5, 5)));
    }
}

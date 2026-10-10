using PhotoReview.App.Services;
using PhotoReview.Imaging;
using Xunit;
using PixelRect = PhotoReview.App.Services.InitialViewportPredictor.PixelRect;

namespace PhotoReview.App.Tests.Services;

/// <summary>
/// P-1 startup: the launch decode is keyed by a viewport predicted before the main window exists. The pure geometry
/// (<see cref="InitialViewportPredictor.PredictClientSize"/>) is pinned with the frame insets Windows reports for a
/// standard overlapped window (AdjustWindowRectEx of an empty rect: negative left/top, positive right/bottom).
/// </summary>
[Trait("Category", "HotPath")]
public sealed class InitialViewportPredictorTests
{
    // 96 dpi (100 %): 8 px sizing border, 31 px top inset = 8 border + 23 caption.
    private static readonly PixelRect Insets96 = new(-8, -31, 8, 8);
    // 144 dpi (150 %): 11 px border, 45 px top inset = 11 border + 34 caption.
    private static readonly PixelRect Insets144 = new(-11, -45, 11, 11);

    [Fact(DisplayName = "A maximized window keeps the work-area width and loses only the caption (96 dpi)")]
    public void PredictClientSize_Maximized96Dpi_KeepsWorkAreaWidthAndLosesOnlyTheCaption()
    {
        var size = InitialViewportPredictor.PredictClientSize(
            maximized: true, normalBounds: new PixelRect(100, 100, 1300, 900), workArea: new PixelRect(0, 0, 1920, 1032), Insets96);

        Assert.Equal((1920, 1032 - 23), size);
    }

    [Fact(DisplayName = "A maximized window keeps the work-area width and loses only the caption (144 dpi)")]
    public void PredictClientSize_Maximized144Dpi_KeepsWorkAreaWidthAndLosesOnlyTheCaption()
    {
        var size = InitialViewportPredictor.PredictClientSize(
            maximized: true, normalBounds: default, workArea: new PixelRect(0, 0, 2880, 1548), Insets144);

        Assert.Equal((2880, 1548 - 34), size);
    }

    [Fact(DisplayName = "A maximized window ignores its normal bounds")]
    public void PredictClientSize_Maximized_IgnoresNormalBounds()
    {
        var work = new PixelRect(0, 0, 1920, 1032);

        var small = InitialViewportPredictor.PredictClientSize(true, new PixelRect(10, 10, 200, 200), work, Insets96);
        var large = InitialViewportPredictor.PredictClientSize(true, new PixelRect(0, 0, 5000, 5000), work, Insets96);

        Assert.Equal(small, large);
    }

    [Fact(DisplayName = "A normal window is its saved bounds minus the frame (96 dpi)")]
    public void PredictClientSize_Normal96Dpi_IsBoundsMinusFrame()
    {
        var size = InitialViewportPredictor.PredictClientSize(
            maximized: false, normalBounds: new PixelRect(100, 100, 1300, 900), workArea: new PixelRect(0, 0, 1920, 1032), Insets96);

        Assert.Equal((1200 - 16, 800 - 39), size);
    }

    [Fact(DisplayName = "A normal window is its saved bounds minus the frame (144 dpi)")]
    public void PredictClientSize_Normal144Dpi_IsBoundsMinusFrame()
    {
        var size = InitialViewportPredictor.PredictClientSize(
            maximized: false, normalBounds: new PixelRect(-300, 50, 1500, 1250), workArea: new PixelRect(0, 0, 2880, 1548), Insets144);

        Assert.Equal((1800 - 22, 1200 - 56), size);
    }

    [Theory(DisplayName = "A window that would have no client area has no prediction")]
    [InlineData(false, 0, 0, 16, 39)]   // normal: bounds exactly the frame
    [InlineData(false, 0, 0, 10, 10)]   // normal: smaller than the frame
    [InlineData(true, 0, 0, 1920, 23)]  // maximized: work area exactly the caption
    [InlineData(true, 0, 0, 0, 0)]      // maximized: empty work area
    public void PredictClientSize_EmptyClient_ReturnsNull(bool maximized, int left, int top, int right, int bottom)
    {
        var rect = new PixelRect(left, top, right, bottom);

        var size = InitialViewportPredictor.PredictClientSize(maximized, normalBounds: rect, workArea: rect, Insets96);

        Assert.Null(size);
    }

    [Fact(DisplayName = "The predicted decode box is a non-null multiple of the box quantum (real Win32 monitor metrics)")]
    [Trait("Category", "Native")]
    public void PredictDecodeBox_DefaultSize_IsQuantisedBox()
    {
        var box = InitialViewportPredictor.PredictDecodeBox(null, 1200, 800, 1.15);

        Assert.NotNull(box);
        Assert.Equal(0, box!.Value.Width % AdaptivePreviewPolicy.BoxQuantum);
        Assert.Equal(0, box.Value.Height % AdaptivePreviewPolicy.BoxQuantum);
        Assert.True(box.Value.Width > 0 && box.Value.Height > 0);
    }
}

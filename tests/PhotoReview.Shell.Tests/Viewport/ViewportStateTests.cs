using PhotoReview.App.Input;
using PhotoReview.App.ViewModels;
using PhotoReview.App.Viewport;

namespace PhotoReview.Shell.Tests.Viewport;

/// <summary>
/// WP-16: <see cref="ViewportState"/> - layout trễ tới lượt <see cref="ViewportState.UpdateLayout"/> (như hàng đợi lệnh cuộn
/// và lượt layout của WPF) và offset được yêu cầu được nhớ qua co/giãn (ScrollContentPresenter; đối chứng với WPF thật ở
/// App.Tests <c>ViewportEngineWpfParityTests</c>).
/// </summary>
public sealed class ViewportStateTests
{
    private static ViewportInput Zoomed(double cw, double ch, double w, double h) =>
        new(cw, ch, 10, ScrollBarPolicy.Auto, ViewerStretchMode.None, w, h, double.PositiveInfinity, double.PositiveInfinity, w, h);

    private static ViewportState LaidOut(ViewportInput input)
    {
        var state = new ViewportState();
        state.SetInput(input);
        Assert.True(state.UpdateLayout());
        return state;
    }

    [Fact]
    public void NewState_IsPendingUntilTheFirstLayout()
    {
        var state = new ViewportState();
        Assert.True(state.IsLayoutPending);
        state.SetInput(Zoomed(1000, 600, 3000, 2000));
        Assert.Equal(0, state.Layout.ExtentWidth); // chưa layout
        Assert.True(state.UpdateLayout());
        Assert.False(state.IsLayoutPending);
        Assert.Equal(3000, state.Layout.ExtentWidth);
        Assert.False(state.UpdateLayout()); // không còn gì để làm
    }

    [Fact]
    public void FirstLayoutOfAnEmptyInput_ChangesNothing()
    {
        var state = new ViewportState();
        Assert.False(state.UpdateLayout()); // layout mặc định = layout của input mặc định
        Assert.False(state.IsLayoutPending);
    }

    [Fact]
    public void SameInputAgain_DoesNotScheduleALayout()
    {
        var input = Zoomed(1000, 600, 3000, 2000) with { ImageWidth = double.NaN };
        var state = LaidOut(input);
        state.SetInput(input with { }); // NaN == NaN với record struct Equals
        Assert.False(state.IsLayoutPending);
        Assert.Equal(input, state.Input);
    }

    [Fact]
    public void ScrollTo_IsAppliedAtTheNextLayout_NotImmediately()
    {
        var state = LaidOut(Zoomed(1000, 600, 3000, 2000));
        state.ScrollTo(300, 200);
        Assert.True(state.IsLayoutPending);
        Assert.Equal(0, state.HorizontalOffset); // ScrollViewer.HorizontalOffset chỉ đổi sau lượt layout
        Assert.True(state.UpdateLayout());
        Assert.Equal(300, state.HorizontalOffset);
        Assert.Equal(200, state.VerticalOffset);
    }

    [Fact]
    public void ScrollTo_TheLastRequestPerAxisWins_AndIsClampedAgainstTheNewLayout()
    {
        var state = LaidOut(Zoomed(1000, 600, 1500, 1000));
        state.ScrollTo(100, 100);
        state.ScrollTo(4000, 50);
        state.SetInput(Zoomed(1000, 600, 5000, 1000)); // nội dung lớn ra trong cùng lượt
        state.UpdateLayout();
        Assert.Equal(4000, state.HorizontalOffset); // kẹp theo layout MỚI (max 4010), không theo layout cũ (max 510)
        Assert.Equal(50, state.VerticalOffset);
    }

    [Fact]
    public void ScrollTo_NegativeIsZero_NaNKeepsThePreviousRequestForThatAxis()
    {
        var state = LaidOut(Zoomed(1000, 600, 3000, 2000));
        state.ScrollTo(300, 200);
        state.UpdateLayout();
        state.ScrollTo(double.NaN, -40);
        state.UpdateLayout();
        Assert.Equal(300, state.HorizontalOffset);
        Assert.Equal(0, state.VerticalOffset);
        state.ScrollTo(-1, double.NaN);
        state.UpdateLayout();
        Assert.Equal(0, state.HorizontalOffset);
        Assert.Equal(0, state.VerticalOffset);
    }

    [Fact]
    public void ARequestedOffset_ClampedByAWiderViewport_ComesBackWhenTheViewportShrinksAgain()
    {
        var state = LaidOut(Zoomed(1000, 600, 3000, 2000));
        state.ScrollTo(1900, 0);
        state.UpdateLayout();
        Assert.Equal(1900, state.HorizontalOffset);

        state.SetInput(Zoomed(1500, 600, 3000, 2000));
        state.UpdateLayout();
        Assert.Equal(1510, state.HorizontalOffset); // 3000 - (1500 - 10)

        state.SetInput(Zoomed(1000, 600, 3000, 2000));
        state.UpdateLayout();
        Assert.Equal(1900, state.HorizontalOffset);
    }

    [Fact]
    public void ScrollHome_GoesToZero()
    {
        var state = LaidOut(Zoomed(1000, 600, 3000, 2000));
        state.ScrollTo(500, 500);
        state.UpdateLayout();
        state.ScrollHome();
        Assert.True(state.UpdateLayout());
        Assert.Equal((0.0, 0.0), (state.HorizontalOffset, state.VerticalOffset));
    }

    [Fact]
    public void UpdateLayout_ReportsAChange_OnlyWhenTheLayoutOrAnOffsetMoved()
    {
        var state = LaidOut(Zoomed(1000, 600, 3000, 2000));
        state.ScrollTo(0, 0);
        Assert.False(state.UpdateLayout()); // cùng offset
        state.SetInput(Zoomed(1000, 600, 3000, 2000) with { BitmapWidth = 1 }); // input khác, layout khác (Fill vẫn giãn)
        Assert.False(state.UpdateLayout());
        state.SetInput(Zoomed(1000, 600, 3001, 2000));
        Assert.True(state.UpdateLayout());
        state.ScrollTo(1, 0);
        Assert.True(state.UpdateLayout());
    }

    [Fact]
    public void ImageOrigin_AndToImageElement_FollowTheOffsetAndTheCentring()
    {
        var centred = LaidOut(Zoomed(1000, 600, 400, 300));
        Assert.Equal(new PointD(300, 150), centred.ImageOrigin);
        Assert.Equal(new PointD(100, 50), centred.ToImageElement(new PointD(400, 200)));
        Assert.Equal(400, centred.ImageActualWidth);
        Assert.Equal(300, centred.ImageActualHeight);

        var scrolled = LaidOut(Zoomed(1000, 600, 3000, 2000));
        scrolled.ScrollTo(250, 120);
        scrolled.UpdateLayout();
        Assert.Equal(new PointD(-250, -120), scrolled.ImageOrigin);
        Assert.Equal(new PointD(260, 130), scrolled.ToImageElement(new PointD(10, 10)));
    }
}

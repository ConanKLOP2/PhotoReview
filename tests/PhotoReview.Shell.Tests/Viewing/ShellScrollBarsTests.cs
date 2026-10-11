using PhotoReview.App.Input;
using PhotoReview.Shell.Interop;
using PhotoReview.Shell.Rendering;
using PhotoReview.Shell.Win32.Viewing;

namespace PhotoReview.Shell.Tests.Viewing;

/// <summary>
/// WP-26: hành vi của <see cref="ShellScrollBars"/> trên cửa sổ giả + viewport thật + <c>ShellInput</c> thật (đăng ký sau thanh
/// cuộn như ở <c>ShellMainWindow</c>): kéo con trượt, nhấn rãnh (trang + lặp theo nhịp bàn phím, đồng hồ giả), nuốt nhấn trước
/// ảnh, ẩn khi vừa khung, hover/pressed, đổi kích thước/DPI/zoom, ComparePanel che. Không phụ thuộc màn hình hay thời gian thật.
/// Hình học mẫu (client 1000x600, ảnh 3000x2000 ở 100 %): thanh dọc x 990..1000, con trượt dọc 172,87 DIP (y 2..174,87),
/// offset dọc tối đa 1410 (quãng 413,13); thanh ngang y 590..600, con trượt ngang 325,38 (x 2..327,38), offset ngang tối đa 2010.
/// </summary>
public sealed class ShellScrollBarsTests
{
    private const double VerticalTravel = 586 - 172.87;
    private const double HorizontalThumb = 986 * 990 / 3000.0;
    private const double HorizontalTravel = 986 - HorizontalThumb;

    [Fact]
    public void RigIsAsDocumented_BothBarsOverAnActualSizeImage()
    {
        using var rig = new ScrollBarRig();
        var layout = rig.Layout;
        Assert.Equal(new RectD(990, 0, 10, 590), layout.Vertical.Bar);
        Assert.Equal(172.87, layout.Vertical.Thumb.Height, 6);
        Assert.Equal(HorizontalThumb, layout.Horizontal.Thumb.Width, 6);
        Assert.Equal(1410, layout.Vertical.MaxOffset, 9);
        Assert.Equal(2010, layout.Horizontal.MaxOffset, 9);
    }

    // ---- kéo con trượt ----

    [Fact]
    public void DraggingTheVerticalThumb_ScrollsProportionally_AndTheThumbStaysUnderTheGrabPoint()
    {
        using var rig = new ScrollBarRig();

        var press = rig.Down(995, 50); // 48 DIP dưới đỉnh con trượt
        Assert.True(press.Handled);
        Assert.True(rig.Bars.IsDragging);
        Assert.True(rig.Window.HasPointerCapture);
        Assert.Equal(0, rig.Viewport.VerticalOffset, 9);

        rig.Move(995, 150);
        Assert.Equal(100 * 1410 / VerticalTravel, rig.Viewport.VerticalOffset, 6);
        Assert.Equal(102, rig.Layout.Vertical.Thumb.Y, 6); // 150 - 48
        Assert.Equal(0, rig.Viewport.HorizontalOffset, 9);

        rig.Move(995, 50 + 413); // gần cuối rãnh
        Assert.Equal((2 + 413 - 2 + 0.0) * 1410 / VerticalTravel, rig.Viewport.VerticalOffset, 6);
    }

    [Fact]
    public void DraggingBeyondTheTrack_Clamps_AndComingBackFollowsTheCursorAgain()
    {
        using var rig = new ScrollBarRig();
        rig.Down(995, 50);

        rig.Move(995, 5000);
        Assert.Equal(1410, rig.Viewport.VerticalOffset, 9);
        rig.Move(-300, -400); // ra ngoài cửa sổ (toạ độ âm, đang capture)
        Assert.Equal(0, rig.Viewport.VerticalOffset, 9);
        rig.Move(300, 100); // x bất kỳ: chỉ toạ độ dọc quyết định
        Assert.Equal((100 - 48 - 2) * 1410 / VerticalTravel, rig.Viewport.VerticalOffset, 6);
    }

    [Fact]
    public void ReleasingTheButton_EndsTheDrag_ReleasesCapture_AndKeepsTheOffset()
    {
        using var rig = new ScrollBarRig();
        rig.Down(995, 50);
        rig.Move(995, 150);
        var offset = rig.Viewport.VerticalOffset;

        var release = rig.Up(995, 150);

        Assert.True(release.Handled);
        Assert.False(rig.Bars.IsDragging);
        Assert.False(rig.Window.HasPointerCapture);
        Assert.Equal(1, rig.Window.Releases);
        rig.Move(995, 400);
        Assert.Equal(offset, rig.Viewport.VerticalOffset, 9);
    }

    [Fact]
    public void DraggingTheHorizontalThumb_ScrollsOnlyTheHorizontalOffset()
    {
        using var rig = new ScrollBarRig();
        rig.Viewport.ScrollTo(0, 500);
        rig.Viewport.UpdateLayout();
        var thumbX = rig.Layout.Horizontal.Thumb.X;
        Assert.Equal(2, thumbX, 9);

        rig.Down(100, 595); // 98 DIP sau mép trái con trượt
        rig.Move(300, 595);

        Assert.Equal((300 - 98 - 2) * 2010 / HorizontalTravel, rig.Viewport.HorizontalOffset, 6);
        Assert.Equal(500, rig.Viewport.VerticalOffset, 9);
    }

    [Fact]
    public void DraggingTheWholeTravel_ReachesExactlyTheMaximumOffset()
    {
        using var rig = new ScrollBarRig();
        rig.Down(995, 50);
        rig.Move(995, 50 + 413.13);
        rig.Move(995, 600);
        Assert.Equal(1410, rig.Viewport.VerticalOffset, 9);
        Assert.Equal(588, rig.Layout.Vertical.Thumb.Bottom, 6);
    }

    [Fact]
    public void TheDragFollowsWhateverLayoutChangesDuringIt_NoStaleThumbGeometry()
    {
        using var rig = new ScrollBarRig();
        rig.Down(995, 50);
        rig.Move(995, 100);
        // Cửa sổ đổi cỡ giữa lúc kéo: thanh dọc cao hơn, con trượt dài hơn; lần di chuyển kế dùng hình học mới.
        rig.Window.Resize(1000, 900);
        rig.Drain();
        var box = rig.Layout.Vertical;
        rig.Move(995, 300);
        var expected = ScrollBarGeometry.OffsetForThumbStart(box, 300 - 48);
        Assert.Equal(expected, rig.Viewport.VerticalOffset, 6);
    }

    // ---- nhấn rãnh ----

    [Fact]
    public void ClickingTheTrackBelowTheThumb_PagesDownByOneViewport()
    {
        using var rig = new ScrollBarRig();

        Assert.True(rig.Down(995, 300).Handled);

        Assert.Equal(590, rig.Viewport.VerticalOffset, 9); // một vùng nhìn dọc
        Assert.Equal(0, rig.Viewport.HorizontalOffset, 9);
        Assert.True(rig.Bars.IsPaging);
        Assert.False(rig.Bars.IsDragging);
    }

    [Fact]
    public void ClickingTheTrackAboveTheThumb_PagesUp_AndTheHorizontalTrackPagesByTheViewportWidth()
    {
        using var rig = new ScrollBarRig();
        rig.Viewport.ScrollTo(1000, 1000);
        rig.Viewport.UpdateLayout();
        var thumb = rig.Layout.Vertical.Thumb;
        Assert.True(thumb.Y > 20);

        rig.Down(995, thumb.Y - 10);
        rig.Up(995, thumb.Y - 10);
        Assert.Equal(1000 - 590, rig.Viewport.VerticalOffset, 9);

        var hThumb = rig.Layout.Horizontal.Thumb;
        rig.Down(hThumb.Right + 20, 595);
        Assert.Equal(1000 + 990, rig.Viewport.HorizontalOffset, 9);
        rig.Up(hThumb.Right + 20, 595);
        rig.Down(2 + 1, 595); // đầu rãnh ngang, trước con trượt (ở 1990/2010 con trượt gần cuối)
        Assert.Equal(1000, rig.Viewport.HorizontalOffset, 9);
    }

    [Fact]
    public void HoldingOnTheTrack_RepeatsAfterTheKeyboardDelay_ThenEveryInterval_UntilTheThumbReachesTheCursor()
    {
        using var rig = new ScrollBarRig(repeat: (500, 50));
        rig.Now = 0;
        rig.Down(995, 580);
        Assert.Equal(590, rig.Viewport.VerticalOffset, 9);

        rig.TickAt(499);
        Assert.Equal(590, rig.Viewport.VerticalOffset, 9); // chưa tới độ trễ
        rig.TickAt(500);
        Assert.Equal(1180, rig.Viewport.VerticalOffset, 9);
        rig.TickAt(549);
        Assert.Equal(1180, rig.Viewport.VerticalOffset, 9); // chưa tới khoảng lặp
        rig.TickAt(550);
        Assert.Equal(1410, rig.Viewport.VerticalOffset, 9); // kẹp ở cuối
        var thumb = rig.Layout.Vertical.Thumb;
        Assert.True(580 >= thumb.Y && 580 < thumb.Bottom, "con trượt phải đã tới con trỏ");

        rig.TickAt(600);
        rig.TickAt(5000);
        Assert.Equal(1410, rig.Viewport.VerticalOffset, 9);
    }

    [Fact]
    public void TheRepeatStopsWhenTheThumbArrivesUnderTheCursor_BeforeTheEndOfTheTrack()
    {
        using var rig = new ScrollBarRig();
        rig.Down(995, 500); // sau trang đầu con trượt ở y 174,9..347,7: con trỏ vẫn dưới nó
        rig.TickAt(500);    // trang hai: con trượt 347,7..520,6 chứa y = 500
        Assert.Equal(1180, rig.Viewport.VerticalOffset, 9);
        rig.TickAt(550);
        rig.TickAt(2000);
        Assert.Equal(1180, rig.Viewport.VerticalOffset, 9);
    }

    [Fact]
    public void ReleasingStopsTheRepeat_AndUnhooksTheFrameClock()
    {
        using var rig = new ScrollBarRig();
        rig.Down(995, 580);
        Assert.True(rig.Clock.SubscriberCount > 0);

        rig.Up(995, 580);
        Assert.False(rig.Bars.IsPaging);
        Assert.False(rig.Window.HasPointerCapture);
        Assert.Equal(0, rig.Clock.SubscriberCount);
        rig.TickAt(5000);
        Assert.Equal(590, rig.Viewport.VerticalOffset, 9);
    }

    [Fact]
    public void LeavingTheRegionWhileHolding_SuspendsTheRepeat_AndComingBackRestartsTheDelay()
    {
        using var rig = new ScrollBarRig();
        rig.Down(995, 580);
        rig.Move(500, 300);   // ra khỏi rãnh (vẫn giữ nút, vẫn capture)
        rig.TickAt(1000);
        Assert.Equal(590, rig.Viewport.VerticalOffset, 9);

        rig.Now = 2000;
        rig.Move(995, 580);   // vào lại: bộ đếm trễ tính lại từ đây
        rig.TickAt(2499);
        Assert.Equal(590, rig.Viewport.VerticalOffset, 9);
        rig.TickAt(2500);
        Assert.Equal(1180, rig.Viewport.VerticalOffset, 9);
    }

    // ---- chuột không lọt xuống ảnh ----

    [Fact]
    public void PressesOnTheBarBackgroundAndCorner_AreSwallowed_NeverPanningTheImageUnderneath()
    {
        using var withoutBars = new ScrollBarRig(withScrollBars: false);
        withoutBars.Down(990, 300); // ảnh 3000 rộng phủ cả vùng thanh cuộn: không có thanh cuộn thì nhấn này kéo ảnh
        Assert.True(withoutBars.Window.HasPointerCapture);

        using var rig = new ScrollBarRig();
        var background = rig.Down(990, 300); // lề trái của rãnh
        Assert.True(background.Handled);
        Assert.False(rig.Window.HasPointerCapture);
        Assert.False(rig.Bars.IsDragging || rig.Bars.IsPaging);
        rig.Up(990, 300);

        var corner = rig.Down(995, 595);
        Assert.True(corner.Handled);
        Assert.False(rig.Window.HasPointerCapture);
        Assert.Equal(0, rig.Viewport.VerticalOffset, 9);
        Assert.Equal(0, rig.Viewport.HorizontalOffset, 9);
    }

    [Fact]
    public void PressesOutsideTheBars_PassToTheImage()
    {
        using var rig = new ScrollBarRig();
        var press = rig.Down(500, 300);
        Assert.True(press.Handled); // ShellInput: nhấn trái trên ảnh (kéo ảnh)
        Assert.True(rig.Window.HasPointerCapture);
        Assert.False(rig.Bars.IsDragging);
        Assert.Equal(0, rig.Presses);
    }

    [Fact]
    public void RightAndMiddleButtons_AreNotTakenByTheBars()
    {
        using var rig = new ScrollBarRig();
        Assert.False(rig.Mouse(WindowMessages.WmRButtonDown, 995, 300).Handled);
        Assert.Equal(0, rig.Presses);
        Assert.False(rig.Bars.IsDragging || rig.Bars.IsPaging);
    }

    [Fact]
    public void TheWheelOverABar_StillGoesToTheInputLayer()
    {
        using var rig = new ScrollBarRig();
        var wParam = (nint)(((uint)(ushort)(short)120 << 16) | 0x0008u);
        var outcome = rig.Window.Dispatch(WindowMessages.WmMouseWheel, wParam, ScrollBarRig.Pack(995, 300));
        Assert.True(outcome.Handled);
        Assert.False(rig.Bars.IsDragging || rig.Bars.IsPaging);
    }

    [Fact]
    public void EveryPressOnABar_StopsKineticGlideLikeWindowPreviewMouseDown()
    {
        using var rig = new ScrollBarRig();
        rig.Down(995, 50);
        rig.Up(995, 50);
        rig.Down(995, 300);
        rig.Up(995, 300);
        rig.Down(990, 300);
        rig.Up(990, 300);
        Assert.Equal(3, rig.Presses);
        rig.Down(500, 300); // ngoài thanh cuộn: không phải việc của thanh cuộn
        Assert.Equal(3, rig.Presses);
    }

    // ---- ẩn khi vừa khung / compare ----

    [Fact]
    public void WhenTheImageFits_NothingIsDrawnOrHandled()
    {
        using var rig = new ScrollBarRig(actualSize: false); // Fit
        Assert.False(rig.Layout.AnyVisible);
        var draw = rig.Draw();
        Assert.Empty(draw.Rects);
        Assert.Empty(draw.Rounded);

        rig.Down(995, 300);
        Assert.Equal(0, rig.Presses);
        Assert.False(rig.Bars.IsPaging);
    }

    [Fact]
    public void TheBarsAppearAndDisappearWithTheImageSize_WhenZooming()
    {
        using var rig = new ScrollBarRig();
        Assert.True(rig.Layout.AnyVisible);
        rig.Viewer.SetZoom(0.2); // 600x400: vừa khung
        rig.Drain();
        Assert.False(rig.Layout.AnyVisible);
        Assert.Equal(1000, rig.Viewport.ViewportWidth, 9); // không còn thanh chiếm chỗ

        rig.Viewer.SetZoom(1.0);
        rig.Drain();
        Assert.True(rig.Layout.AnyVisible);
        Assert.Equal(990, rig.Viewport.ViewportWidth, 9);
    }

    [Fact]
    public void ZoomingIn_ShrinksTheThumb_InProportionToTheViewportOverTheExtent()
    {
        using var rig = new ScrollBarRig();
        var before = rig.Layout.Vertical.Thumb.Height;
        rig.Viewer.SetZoom(2.0); // 6000x4000
        rig.Drain();
        var after = rig.Layout.Vertical;
        Assert.Equal(586 * 590 / 4000.0, after.Thumb.Height, 6);
        Assert.True(after.Thumb.Height < before);
    }

    [Fact]
    public void ComparePanel_CoversTheBars_ExceptTheStripsOutsideItsMargin()
    {
        using var rig = new ScrollBarRig();
        rig.CompareVisible = true;

        Assert.Equal(ScrollBarPart.None, rig.Bars.HitTest(new PointD(991, 300))); // dưới panel (panel tới x = 992)
        Assert.Equal(ScrollBarPart.TrackAfter, rig.Bars.HitTest(new PointD(994, 300)));
        Assert.Equal(ScrollBarPart.None, rig.Bars.HitTest(new PointD(500, 591))); // panel tới y = 592
        Assert.Equal(ScrollBarPart.TrackAfter, rig.Bars.HitTest(new PointD(500, 595)));

        var draw = rig.Draw();
        Assert.Equal(new[] { new RectD(992, 0, 8, 600), new RectD(0, 592, 1000, 8) }, draw.Clips);
        Assert.Equal(0, draw.ClipDepth);
    }

    // ---- hover / pressed / vẽ ----

    [Fact]
    public void DrawsTrackBackgrounds_RoundedThumbs_AndTheCorner()
    {
        using var rig = new ScrollBarRig();
        var draw = rig.Draw();

        Assert.Equal(
            new[]
            {
                (new RectD(990, 0, 10, 590), ScrollBarColors.Track), (new RectD(0, 590, 990, 10), ScrollBarColors.Track),
                (new RectD(990, 590, 10, 10), ScrollBarColors.Track),
            },
            draw.Rects);
        Assert.Equal(2, draw.Rounded.Count);
        Assert.All(draw.Rounded, thumb =>
        {
            Assert.Equal(4, thumb.Radius);
            Assert.Equal(ScrollBarColors.Thumb, thumb.Color);
        });
        Assert.Equal(rig.Layout.Vertical.Thumb, draw.Rounded[0].Rect);
        Assert.Equal(rig.Layout.Horizontal.Thumb, draw.Rounded[1].Rect);
    }

    [Fact]
    public void OnlyTheBarThatIsShown_IsDrawn_AndNoCornerForASingleBar()
    {
        using var rig = new ScrollBarRig(imageWidth: 900, imageHeight: 2000);
        var draw = rig.Draw();
        Assert.Single(draw.Rects);
        Assert.Equal(new RectD(990, 0, 10, 600), draw.Rects[0].Rect);
        Assert.Single(draw.Rounded);
    }

    [Fact]
    public void ThumbColor_FollowsHoverAndPressedStates_PerAxis()
    {
        using var rig = new ScrollBarRig();

        rig.Move(995, 50, leftDown: false);
        var hover = rig.Draw();
        Assert.Equal(ScrollBarColors.ThumbHover, hover.Rounded[0].Color);
        Assert.Equal(ScrollBarColors.Thumb, hover.Rounded[1].Color); // thanh ngang không hover

        rig.Move(995, 300, leftDown: false); // trên rãnh: không hover con trượt
        Assert.Equal(ScrollBarColors.Thumb, rig.Draw().Rounded[0].Color);

        rig.Move(100, 595, leftDown: false);
        var horizontalHover = rig.Draw();
        Assert.Equal(ScrollBarColors.Thumb, horizontalHover.Rounded[0].Color);
        Assert.Equal(ScrollBarColors.ThumbHover, horizontalHover.Rounded[1].Color);

        rig.Down(100, 595);
        var pressed = rig.Draw();
        Assert.Equal(ScrollBarColors.ThumbPressed, pressed.Rounded[1].Color);
        Assert.Equal(ScrollBarColors.Thumb, pressed.Rounded[0].Color);

        rig.Up(100, 595);
        Assert.Equal(ScrollBarColors.ThumbHover, rig.Draw().Rounded[1].Color); // con trỏ còn trên con trượt
    }

    [Fact]
    public void Colors_AreTheThumbColorsOfDarkScrollBarsXaml()
    {
        Assert.Equal(new ColorF(0x4A / 255f, 0x4A / 255f, 0x4A / 255f, 1f), ScrollBarColors.Thumb);
        Assert.Equal(new ColorF(0x6A / 255f, 0x6A / 255f, 0x6A / 255f, 1f), ScrollBarColors.ThumbHover);
        Assert.Equal(new ColorF(0x7A / 255f, 0x7A / 255f, 0x7A / 255f, 1f), ScrollBarColors.ThumbPressed);
        Assert.Equal(new ColorF(0x16 / 255f, 0x16 / 255f, 0x16 / 255f, 1f), ScrollBarColors.Track); // Dark.ScrollTrack
    }

    [Fact]
    public void HoverIsCleared_WhenTheMouseLeavesTheWindow_AndTrackingIsRequestedOncePerEntry()
    {
        using var rig = new ScrollBarRig();
        rig.Move(995, 50, leftDown: false);
        rig.Move(995, 60, leftDown: false);
        Assert.Equal(new[] { rig.Window.Hwnd }, rig.TrackedLeave);
        Assert.Equal(ScrollBarColors.ThumbHover, rig.Draw().Rounded[0].Color);

        rig.Window.Dispatch(ScrollBarNative.WmMouseLeave, 0, 0);
        Assert.Equal(ScrollBarColors.Thumb, rig.Draw().Rounded[0].Color);

        rig.Move(995, 50, leftDown: false);
        Assert.Equal(2, rig.TrackedLeave.Count);
    }

    [Fact]
    public void HoverFollowsTheThumbWhenTheLayoutMovesItUnderAStationaryCursor()
    {
        using var rig = new ScrollBarRig();
        rig.Move(995, 300, leftDown: false);
        Assert.Equal(ScrollBarColors.Thumb, rig.Draw().Rounded[0].Color);

        rig.Viewport.ScrollTo(0, 705); // con trượt trượt xuống quanh y = 300
        rig.Viewport.UpdateLayout();
        Assert.Equal(ScrollBarColors.ThumbHover, rig.Draw().Rounded[0].Color);
    }

    // ---- kết thúc bất thường / hệ thống ----

    [Fact]
    public void LosingCapture_EndsTheDrag()
    {
        using var rig = new ScrollBarRig();
        rig.Down(995, 50);

        rig.Window.Dispatch(WindowMessages.WmCaptureChanged, 0, 0);

        Assert.False(rig.Bars.IsDragging);
        rig.Move(995, 300);
        Assert.Equal(0, rig.Viewport.VerticalOffset, 9);
    }

    [Fact]
    public void CancelMode_EndsTheInteraction_AndReleasesTheCapture()
    {
        using var rig = new ScrollBarRig();
        rig.Down(995, 50);
        rig.Window.Dispatch(ScrollBarNative.WmCancelMode, 0, 0);
        Assert.False(rig.Bars.IsDragging);
        Assert.False(rig.Window.HasPointerCapture);
    }

    [Fact]
    public void ButtonUpWithoutAnInteraction_IsNotTaken()
    {
        using var rig = new ScrollBarRig();
        Assert.False(rig.Up(995, 50).Handled);
        Assert.False(rig.Bars.IsDragging || rig.Bars.IsPaging);
    }

    [Fact]
    public void Resize_ReflowsTheBars_AndRepositionsTheThumb()
    {
        using var rig = new ScrollBarRig();
        rig.Window.Resize(1200, 700);
        rig.Drain();

        var vertical = rig.Layout.Vertical;
        Assert.Equal(new RectD(1190, 0, 10, 690), vertical.Bar);
        Assert.Equal(686 * 690 / 2000.0, vertical.Thumb.Height, 6);
        Assert.Equal(new RectD(0, 690, 1190, 10), rig.Layout.Horizontal.Bar);
        Assert.Equal(1190, rig.Viewport.ViewportWidth, 9);
    }

    [Fact]
    public void Dpi150_PixelCoordinatesAreConvertedToDip_ForPressAndDrag()
    {
        using var rig = new ScrollBarRig(dpiScale: 1.5);
        var box = rig.Layout.Vertical;
        Assert.True(box.Visible);
        // Ảnh 3000x2000 px ở 150 % = 2000x1333,33 DIP trong client 1000x600 DIP; thanh dọc ở x 990..1000 DIP.
        Assert.Equal(990, box.Bar.X, 9);
        var startOffset = rig.Viewport.VerticalOffset;
        // lParam mang pixel NGUYÊN: ví dụ này tính lại đúng các pixel đã làm tròn rồi đổi sang DIP như shell.
        var grabPixel = Math.Round((box.Thumb.Y + 20) * 1.5);
        var movePixel = Math.Round((box.Thumb.Y + 110) * 1.5);

        rig.Down(996 * 1.5, grabPixel);
        Assert.True(rig.Bars.IsDragging);
        rig.Move(996 * 1.5, movePixel);

        var grab = (grabPixel / 1.5) - box.Thumb.Y;
        var expected = ScrollBarGeometry.OffsetForThumbStart(box, (movePixel / 1.5) - grab);
        Assert.Equal(expected, rig.Viewport.VerticalOffset, 6);
        Assert.True(rig.Viewport.VerticalOffset > startOffset);
    }

    // ---- bảng giá trị của nhịp lặp ----

    [Theory]
    [InlineData(0, 31, 250, 33)]
    [InlineData(1, 31, 500, 33)]
    [InlineData(3, 0, 1000, 400)]
    [InlineData(2, 15, 750, 222)]
    public void RepeatTiming_FollowsWpfRepeatButtonFormulas(int keyboardDelay, int keyboardSpeed, int delayMs, int intervalMs)
    {
        Assert.Equal((delayMs, intervalMs), ScrollBarNative.RepeatTimingFor(keyboardDelay, keyboardSpeed));
    }
}

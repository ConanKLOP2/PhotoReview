using PhotoReview.App.Input;
using PhotoReview.Core.Model;
using PhotoReview.Shell.Interop;
using PhotoReview.Shell.Win32.Hosting;
using PhotoReview.Shell.Win32.Input;

namespace PhotoReview.Shell.Tests.Input;

/// <summary>
/// WP-22: chuột (nhấn, kéo pan, capture, double-click, nút giữa, wheel, touchpad) qua ba handler thật + controller con trỏ thật trên
/// ViewportController thật (cửa sổ giả). Ảnh 3000x2000 trong client 1000x600 (DPI 1): Fit = 900x600 ở x = 50; 100 % có hai thanh
/// cuộn (viewport 990x590).
/// </summary>
public sealed class PointerInputTests
{
    private const uint LDown = WindowMessages.WmLButtonDown;
    private const uint LUp = WindowMessages.WmLButtonUp;
    private const uint MDown = WindowMessages.WmMButtonDown;
    private const uint Move = WindowMessages.WmMouseMove;
    private const int MkL = InputNative.MkLButton;

    private static void ActualSize(InputRig rig)
    {
        rig.KeyDown(KeyIdMapping.ToVirtualKey(KeyId.D1));
        rig.Drain();
    }

    // ---- nhấn / click-to-zoom ----

    [Fact]
    public void LeftPress_OutsideTheImageRectangle_IsNotHandled_AndDoesNotCapture()
    {
        using var rig = new InputRig(configure: s => s.ClickToZoomEnabled = true);
        rig.Fit();

        var (handled, _) = rig.Mouse(LDown, 20, 300); // lề trái (ảnh Fit bắt đầu ở x = 50)

        Assert.False(handled);
        Assert.False(rig.Window.HasPointerCapture);
    }

    [Fact]
    public void ClickToZoom_PressReleaseOnTheImage_ZoomsToTheClickLevel_AndAClickAtThatLevelReturnsToFit()
    {
        using var rig = new InputRig(configure: s =>
        {
            s.ClickToZoomEnabled = true;
            s.ClickZoomPercent = 200;
        });
        rig.Fit();

        Assert.True(rig.Mouse(LDown, 500, 300).Handled);
        Assert.True(rig.Window.HasPointerCapture);
        rig.Mouse(LUp, 500, 300);
        Assert.False(rig.Window.HasPointerCapture);
        rig.Pump(Task.CompletedTask);

        Assert.False(rig.Viewer.IsFit);
        Assert.Equal(2.0, rig.Viewer.Zoom, 9);

        rig.MessageTime = 1_000_000; // lần nhấn thứ hai cách xa: không phải double-click
        rig.Mouse(LDown, 500, 300);
        rig.Mouse(LUp, 500, 300);
        rig.Drain();
        Assert.True(rig.Viewer.IsFit);
    }

    [Fact]
    public void ClickToZoomDisabled_AtFit_DoesNothing()
    {
        using var rig = new InputRig();
        rig.Fit();

        Assert.False(rig.Mouse(LDown, 500, 300).Handled);
        rig.Mouse(LUp, 500, 300);

        Assert.True(rig.Viewer.IsFit);
    }

    [Fact]
    public void DoubleClick_ReturnsToFit_EvenWhenZoomed_AndTheSecondReleaseIsNotAClick()
    {
        using var rig = new InputRig(configure: s => s.ClickToZoomEnabled = true);
        ActualSize(rig);
        Assert.False(rig.Viewer.IsFit);

        rig.MessageTime = 5000;
        rig.Mouse(LDown, 400, 300);
        rig.Mouse(LUp, 400, 300);
        rig.MessageTime = 5100;
        rig.Mouse(LDown, 401, 300);
        rig.Mouse(LUp, 401, 300);
        rig.Drain();

        Assert.True(rig.Viewer.IsFit);
    }

    [Fact]
    public void TwoClicksTooFarApart_InTime_AreNotADoubleClick()
    {
        using var rig = new InputRig(configure: s => s.ClickToZoomEnabled = true);
        ActualSize(rig);

        rig.MessageTime = 5000;
        rig.Mouse(LDown, 400, 300);
        rig.Mouse(LUp, 400, 300);
        rig.MessageTime = 5000 + 501;
        rig.Mouse(LDown, 400, 300);
        rig.Mouse(LUp, 400, 300);
        rig.Drain();

        Assert.False(rig.Viewer.IsFit); // zoomed, kéo không có: click-to-zoom về Fit chỉ khi đúng mức; 100 % != click level mặc định
    }

    // ---- kéo pan / capture ----

    [Fact]
    public void Drag_PansTheImage_ByThePointerDelta_AndKeepsTrackingOutsideTheWindow()
    {
        using var rig = new InputRig(configure: s => s.KineticPanEnabled = false);
        ActualSize(rig);

        var (x0, y0) = (rig.Viewport.HorizontalOffset, rig.Viewport.VerticalOffset);

        Assert.True(rig.Mouse(LDown, 500, 300).Handled);
        Assert.Equal(ShellCursor.SizeAll, rig.Window.Cursor);
        rig.Mouse(Move, 440, 270, MkL);
        rig.Drain();
        Assert.Equal(x0 + 60, rig.Viewport.HorizontalOffset, 6);
        Assert.Equal(y0 + 30, rig.Viewport.VerticalOffset, 6);

        // ra ngoài cửa sổ: toạ độ âm có dấu vẫn được theo dõi nhờ capture
        rig.Mouse(Move, -20, 270, MkL);
        rig.Drain();
        Assert.Equal(x0 + 60 + 460, rig.Viewport.HorizontalOffset, 6);

        rig.Mouse(LUp, -20, 270);
        Assert.False(rig.Window.HasPointerCapture);
        Assert.Equal(ShellCursor.Arrow, rig.Window.Cursor);
    }

    [Fact]
    public void Move_WithoutCapture_OverTheMargin_IsIgnored_ButOverTheImageIsTracked()
    {
        using var rig = new InputRig(configure: s => s.KineticPanEnabled = false);
        ActualSize(rig);
        var x0 = rig.Viewport.HorizontalOffset;
        rig.Mouse(LDown, 500, 300);
        rig.Window.ReleasePointer(); // ví dụ mất capture do hệ thống
        rig.Input.HandleCaptureLost();

        rig.Mouse(Move, 400, 300, MkL);
        rig.Drain();

        Assert.Equal(x0, rig.Viewport.HorizontalOffset); // pan đã bị huỷ bởi mất capture
    }

    [Fact]
    public void CaptureChanged_CancelsThePan()
    {
        using var rig = new InputRig(configure: s => s.KineticPanEnabled = false);
        ActualSize(rig);
        var x0 = rig.Viewport.HorizontalOffset;
        rig.Mouse(LDown, 500, 300);
        rig.Mouse(Move, 450, 300, MkL);
        rig.Drain();
        var panned = rig.Viewport.HorizontalOffset;
        Assert.True(panned > x0);

        rig.Window.ReleasePointer();
        rig.Send(WindowMessages.WmCaptureChanged, 0, 0);
        rig.Mouse(Move, 300, 300, MkL);
        rig.Drain();

        Assert.Equal(panned, rig.Viewport.HorizontalOffset);
    }

    [Fact]
    public void Move_WithoutTheLeftButton_DoesNotPan()
    {
        using var rig = new InputRig(configure: s => s.KineticPanEnabled = false);
        ActualSize(rig);
        var x0 = rig.Viewport.HorizontalOffset;
        rig.Mouse(LDown, 500, 300);
        rig.Mouse(Move, 400, 300, 0);
        rig.Drain();
        Assert.Equal(x0, rig.Viewport.HorizontalOffset);
    }

    [Theory]
    [InlineData(1.0, 100, 60, 100, 60)]
    [InlineData(1.5, 150, 90, 100, 60)]
    [InlineData(2.0, 200, 120, 100, 60)]
    public void PixelCoordinates_AreConvertedToDipsByTheWindowDpi(double dpi, int pixelX, int pixelY, double dipX, double dipY)
    {
        using var rig = new InputRig(clientWidth: 1000, clientHeight: 600, dpiScale: dpi);
        rig.Mouse(Move, pixelX, pixelY);
        Assert.Equal(new PointD(dipX, dipY), rig.Input.LastPointerPosition);
    }

    [Fact]
    public void ThePointerActivityEvent_FiresOnMove()
    {
        using var rig = new InputRig();
        var count = 0;
        rig.Input.PointerActivity += (_, _) => count++;
        rig.Mouse(Move, 10, 10);
        rig.Mouse(Move, 11, 10);
        Assert.Equal(2, count);
    }

    // ---- overlay / so sánh ----

    [Fact]
    public void Overlay_SwallowsPressAndWheel_ButStillStopsAGlide()
    {
        using var rig = new InputRig(configure: s =>
        {
            s.ClickToZoomEnabled = true;
            s.KineticPanEnabled = true;
        });
        ActualSize(rig);
        rig.Input.OverlayHitTest = p => p.X < 100 && p.Y < 60;
        rig.KeyDown(KeyIdMapping.ToVirtualKey(KeyId.Right)); // bắt đầu đà
        Assert.Equal(1, rig.Clock.SubscriberCount);

        Assert.False(rig.Mouse(LDown, 50, 30).Handled);
        Assert.Equal(0, rig.Clock.SubscriberCount);
        Assert.False(rig.Window.HasPointerCapture);
        Assert.False(rig.Wheel(WindowMessages.WmMouseWheel, 120, 50, 30).Handled);
        Assert.True(rig.Wheel(WindowMessages.WmMouseWheel, 120, 500, 300).Handled);
    }

    [Fact]
    public void CompareVisible_TakesPressAndWheelAway_FromTheImage()
    {
        using var rig = new InputRig(configure: s => s.ClickToZoomEnabled = true);
        rig.Fit();
        rig.Host.IsCompareVisible = true;

        Assert.False(rig.Mouse(LDown, 500, 300).Handled);
        Assert.False(rig.Wheel(WindowMessages.WmMouseWheel, 120, 500, 300).Handled);
        Assert.False(rig.Wheel(WindowMessages.WmMouseHWheel, 120, 500, 300).Handled);
        Assert.False(rig.Mouse(MDown, 500, 300).Handled);
        Assert.True(rig.Viewer.IsFit);
    }

    // ---- nút giữa / nút phải ----

    [Theory]
    [InlineData(MiddleClickAction.NextImage, ReviewCommandType.Next)]
    [InlineData(MiddleClickAction.PreviousImage, ReviewCommandType.Previous)]
    [InlineData(MiddleClickAction.NextFolder, ReviewCommandType.NextFolder)]
    [InlineData(MiddleClickAction.PreviousFolder, ReviewCommandType.PreviousFolder)]
    [InlineData(MiddleClickAction.OpenFolder, ReviewCommandType.OpenFolder)]
    public void MiddleClick_RunsTheConfiguredAction(MiddleClickAction action, ReviewCommandType expected)
    {
        using var rig = new InputRig(configure: s => s.MiddleClickAction = action);
        rig.Fit();

        Assert.True(rig.Mouse(MDown, 500, 300).Handled);

        Assert.Equal([new ReviewCommand(expected)], rig.Host.Commands);
    }

    [Fact]
    public void MiddleClick_ViewportActions_RunOnTheController()
    {
        using var rig = new InputRig(configure: s => s.MiddleClickAction = MiddleClickAction.ActualSize);
        rig.Fit();
        rig.Mouse(MDown, 500, 300);
        rig.Drain();
        Assert.Equal(1.0, rig.Viewer.Zoom, 9);
        Assert.Empty(rig.Host.Commands);
    }

    [Fact]
    public void MiddleClick_WorksOverTheMargin_NotJustTheImage()
    {
        using var rig = new InputRig(configure: s => s.MiddleClickAction = MiddleClickAction.NextImage);
        rig.Fit();
        Assert.True(rig.Mouse(MDown, 10, 300).Handled);
        Assert.Equal([ReviewCommand.Next], rig.Host.Commands);
    }

    [Fact]
    public void MiddleClick_OnAScrollBar_Overlay_NoImages_OrNone_DoesNotRunAnything()
    {
        using var rig = new InputRig(configure: s => s.MiddleClickAction = MiddleClickAction.NextImage);
        ActualSize(rig); // hai thanh cuộn: viewport 990x590
        Assert.False(rig.Mouse(MDown, 995, 300).Handled); // thanh dọc
        Assert.False(rig.Mouse(MDown, 300, 595).Handled); // thanh ngang
        rig.Input.OverlayHitTest = p => p.X < 100 && p.Y < 60;
        Assert.False(rig.Mouse(MDown, 50, 30).Handled);
        Assert.Empty(rig.Host.Commands);

        rig.Host.HasImages = false;
        Assert.True(rig.Mouse(MDown, 500, 300).Handled); // nuốt nhưng không có ảnh: không lệnh
        Assert.Empty(rig.Host.Commands);

        rig.Host.HasImages = true;
        rig.Settings.MiddleClickAction = MiddleClickAction.None;
        Assert.True(rig.Mouse(MDown, 500, 300).Handled);
        Assert.Empty(rig.Host.Commands);
    }

    [Fact]
    public void MiddleClick_RepeatedWithinDoubleClickTime_RunsOnce()
    {
        using var rig = new InputRig(configure: s => s.MiddleClickAction = MiddleClickAction.NextImage);
        rig.MessageTime = 1000;
        rig.Mouse(MDown, 500, 300);
        rig.MessageTime = 1100;
        Assert.True(rig.Mouse(MDown, 500, 300).Handled);
        Assert.Equal([ReviewCommand.Next], rig.Host.Commands);
        rig.MessageTime = 5000;
        rig.Mouse(MDown, 500, 300);
        Assert.Equal(2, rig.Host.Commands.Count);
    }

    [Fact]
    public void RightAndExtraButtons_AreNotHandled_ButStopAGlide()
    {
        using var rig = new InputRig(configure: s => s.KineticPanEnabled = true);
        ActualSize(rig);
        rig.KeyDown(KeyIdMapping.ToVirtualKey(KeyId.Right));
        Assert.Equal(1, rig.Clock.SubscriberCount);

        Assert.False(rig.Mouse(WindowMessages.WmRButtonDown, 500, 300).Handled);
        Assert.Equal(0, rig.Clock.SubscriberCount);

        rig.KeyDown(KeyIdMapping.ToVirtualKey(KeyId.Right));
        Assert.False(rig.Send(WindowMessages.WmXButtonDown, (nint)(1 << 16), PackPoint(500, 300)).Handled);
        Assert.Equal(0, rig.Clock.SubscriberCount);
        Assert.Empty(rig.Host.Commands);
    }

    private static nint PackPoint(int x, int y) => InputRig.Pack(x, y);

    // ---- wheel ----

    [Fact]
    public void Wheel_ZoomsAtThePointer_KeepingThePointUnderTheCursor()
    {
        using var rig = new InputRig();
        rig.Fit();
        var before = rig.Viewport.ToImageElement(new PointD(300, 200));
        var fraction = (before.X / rig.Viewport.ImageActualWidth, before.Y / rig.Viewport.ImageActualHeight);

        Assert.True(rig.Wheel(WindowMessages.WmMouseWheel, 120, 300, 200).Handled);
        rig.Drain();

        Assert.False(rig.Viewer.IsFit);
        Assert.Equal(0.4, rig.Viewer.Zoom, 9); // Fit 0,3 + bước 10 %
        var after = rig.Viewport.ToImageElement(new PointD(300, 200));
        Assert.Equal(fraction.Item1, after.X / rig.Viewport.ImageActualWidth, 3);
        Assert.Equal(fraction.Item2, after.Y / rig.Viewport.ImageActualHeight, 3);
    }

    [Fact]
    public void Wheel_Delta_IsSigned_NegativeZoomsOut()
    {
        using var rig = new InputRig();
        rig.Fit();
        rig.Wheel(WindowMessages.WmMouseWheel, 120, 500, 300);
        rig.Wheel(WindowMessages.WmMouseWheel, 120, 500, 300);
        rig.Drain();
        var zoomedIn = rig.Viewer.Zoom;
        rig.Wheel(WindowMessages.WmMouseWheel, -120, 500, 300);
        rig.Drain();
        Assert.True(rig.Viewer.Zoom < zoomedIn);
        Assert.Equal(zoomedIn - 0.1, rig.Viewer.Zoom, 9);
    }

    [Fact]
    public void Wheel_ScreenCoordinates_AreConvertedToTheClientAtTheWindowDpi()
    {
        using var rig = new InputRig(dpiScale: 1.25);
        rig.Window.ClientOriginPixel = new PointD(100, 50);
        rig.Fit();
        var client = rig.Viewport.ViewportSize;
        var target = new PointD(client.Width / 2 - 100, client.Height / 2 - 20); // DIP trong client
        var before = rig.Viewport.ToImageElement(target);
        var (beforeWidth, beforeHeight) = (rig.Viewport.ImageActualWidth, rig.Viewport.ImageActualHeight);

        // pixel màn hình = gốc + DIP x 1,25
        var screenX = (int)Math.Round(100 + (target.X * 1.25));
        var screenY = (int)Math.Round(50 + (target.Y * 1.25));
        rig.Wheel(WindowMessages.WmMouseWheel, 120, screenX, screenY);
        rig.Drain();
        var after = rig.Viewport.ToImageElement(target);

        Assert.True(rig.Viewport.ImageActualWidth > beforeWidth);
        Assert.Equal(before.X / beforeWidth, after.X / rig.Viewport.ImageActualWidth, 2);
        Assert.Equal(before.Y / beforeHeight, after.Y / rig.Viewport.ImageActualHeight, 2);
    }

    [Fact]
    public void Wheel_CtrlFlag_FromWParam_TogglesBetweenZoomAndNavigate()
    {
        using var rig = new InputRig(configure: s => s.MouseWheelAction = MouseWheelAction.Navigate);
        rig.Fit();

        rig.Wheel(WindowMessages.WmMouseWheel, -120, 500, 300); // không Ctrl: Next
        Assert.Equal(1, rig.Host.Nexts);
        Assert.True(rig.Viewer.IsFit);

        rig.MessageTime = 10_000;
        rig.Wheel(WindowMessages.WmMouseWheel, 120, 500, 300, ctrl: true); // Ctrl: zoom
        rig.Drain();
        Assert.False(rig.Viewer.IsFit);
        Assert.Equal(1, rig.Host.Nexts);
        Assert.Equal(0, rig.Host.Previouses);
    }

    [Fact]
    public void Wheel_Navigate_UsesTheMessageTimeForTheGestureGrouping()
    {
        using var rig = new InputRig(configure: s => s.MouseWheelAction = MouseWheelAction.Navigate);
        rig.Fit();
        rig.MessageTime = 100;
        rig.Wheel(WindowMessages.WmMouseWheel, 120, 500, 300);
        rig.MessageTime = 100 + 5000;
        rig.Wheel(WindowMessages.WmMouseWheel, 120, 500, 300);
        Assert.Equal(2, rig.Host.Previouses);
    }

    // ---- touchpad ----

    private static InputRig ZoomedRig(Action<PhotoReview.Core.Settings.AppSettings>? configure = null)
    {
        var rig = new InputRig(configure: s =>
        {
            s.KineticPanEnabled = false;
            configure?.Invoke(s);
        });
        ActualSize(rig);
        rig.Viewport.ScrollTo(500, 500);
        rig.Drain();
        return rig;
    }

    [Theory]
    [InlineData(30, 500.0, 470.0)]   // lên (+) cuộn về đầu: offset giảm 1 DIP / đơn vị delta
    [InlineData(-45, 500.0, 545.0)]
    public void TouchpadVerticalSwipe_PansAZoomedImage_OneDipPerDeltaUnit(int delta, double x, double y)
    {
        using var rig = ZoomedRig();
        rig.MessageTime = 100;

        rig.Wheel(WindowMessages.WmMouseWheel, delta, 500, 300);
        rig.Drain();

        Assert.Equal(x, rig.Viewport.HorizontalOffset, 6);
        Assert.Equal(y, rig.Viewport.VerticalOffset, 6);
    }

    [Fact]
    public void TouchpadHorizontalSwipe_PansRight_WithPositiveDelta()
    {
        using var rig = ZoomedRig();
        rig.MessageTime = 100;
        rig.TickCount = 100;

        Assert.True(rig.Wheel(WindowMessages.WmMouseHWheel, 40, 500, 300).Handled);
        rig.Drain();

        Assert.Equal(540, rig.Viewport.HorizontalOffset, 6);
        Assert.Equal(500, rig.Viewport.VerticalOffset, 6);
    }

    [Fact]
    public void DeviceHint_MakesAnExactNotchATouchpadMessage()
    {
        using var rig = ZoomedRig();
        rig.MessageTime = 100;

        rig.Wheel(WindowMessages.WmMouseWheel, 120, 500, 300); // thiết bị Unknown + đúng 1 notch: bánh xe chuột -> zoom
        rig.Drain();
        Assert.True(rig.Viewer.Zoom > 1.0);

        using var rig2 = ZoomedRig();
        rig2.MessageTime = 100;
        rig2.WheelDevice = WheelDeviceHint.Touchpad;
        rig2.Wheel(WindowMessages.WmMouseWheel, 120, 500, 300);
        rig2.Drain();
        Assert.Equal(1.0, rig2.Viewer.Zoom, 9);
        Assert.Equal(380, rig2.Viewport.VerticalOffset, 6);
    }

    [Theory]
    [InlineData(250, true)]  // đúng GestureGapMs vẫn cùng gesture
    [InlineData(251, false)]
    public void TouchpadGesture_GapBoundary_DecidesWhetherANotchIsStillTheSwipe(int gap, bool stillTouchpad)
    {
        using var rig = ZoomedRig();
        rig.MessageTime = 1000;
        rig.Wheel(WindowMessages.WmMouseWheel, 30, 500, 300); // chắc chắn là touchpad
        rig.Drain();
        var offsetAfterFirst = rig.Viewport.VerticalOffset;

        rig.MessageTime = 1000 + gap;
        rig.Wheel(WindowMessages.WmMouseWheel, 120, 500, 300);
        rig.Drain();

        if (stillTouchpad)
        {
            Assert.Equal(offsetAfterFirst - 120, rig.Viewport.VerticalOffset, 6);
            Assert.Equal(1.0, rig.Viewer.Zoom, 9);
        }
        else
        {
            Assert.True(rig.Viewer.Zoom > 1.0);
        }
    }

    [Fact]
    public void HorizontalWheel_UsesTheTickCount_NotTheMessageTime()
    {
        using var rig = ZoomedRig();
        rig.MessageTime = 5;
        rig.TickCount = 1000;
        rig.Wheel(WindowMessages.WmMouseHWheel, 30, 500, 300); // touchpad (30 không phải notch)
        rig.Drain();
        var x = rig.Viewport.HorizontalOffset;
        Assert.Equal(530, x, 6);

        // Cách xa 5000 ms theo TickCount nhưng cùng MessageTime: đúng nguồn -> ngoài gesture -> notch ngang bị bỏ qua.
        rig.TickCount = 6000;
        rig.Wheel(WindowMessages.WmMouseHWheel, 120, 500, 300);
        rig.Drain();
        Assert.Equal(x, rig.Viewport.HorizontalOffset, 6);
    }

    [Fact]
    public void TouchpadSwipeAtFit_ChangesImage_AfterHalfTheConfiguredDistance()
    {
        using var rig = new InputRig(configure: s =>
        {
            s.TouchpadSwipeEnabled = true;
            s.TouchpadSwipeDistancePerImage = 600;
        });
        rig.Fit();
        rig.MessageTime = 100;
        rig.WheelDevice = WheelDeviceHint.Touchpad;

        rig.Wheel(WindowMessages.WmMouseWheel, -290, 500, 300);
        Assert.Equal(0, rig.Host.Nexts); // 290 < 300
        rig.MessageTime = 116;
        rig.Wheel(WindowMessages.WmMouseWheel, -20, 500, 300); // tổng 310 >= 300
        Assert.Equal(1, rig.Host.Nexts);
    }

    [Fact]
    public void TouchpadSwipe_Disabled_FallsBackToTheMouseWheelBehaviour()
    {
        using var rig = new InputRig(configure: s => s.TouchpadSwipeEnabled = false);
        rig.Fit();
        rig.MessageTime = 100;
        rig.WheelDevice = WheelDeviceHint.Touchpad;
        rig.Wheel(WindowMessages.WmMouseWheel, 30, 500, 300);
        rig.Drain();
        Assert.False(rig.Viewer.IsFit); // zoom (MouseWheelAction mặc định), không điều hướng
    }

    [Fact]
    public void HorizontalWheel_OutsideTheViewport_IsNotHandled_InsideIsHandled()
    {
        using var rig = ZoomedRig();
        Assert.False(rig.Wheel(WindowMessages.WmMouseHWheel, 40, 995, 300).Handled); // trên thanh cuộn dọc
        Assert.False(rig.Wheel(WindowMessages.WmMouseHWheel, 40, 300, 595).Handled);
        Assert.False(rig.Wheel(WindowMessages.WmMouseHWheel, 40, -1, 300).Handled);
        Assert.True(rig.Wheel(WindowMessages.WmMouseHWheel, 40, 989, 589).Handled);
        rig.Window.IsLoaded = false;
        Assert.False(rig.Wheel(WindowMessages.WmMouseHWheel, 40, 300, 300).Handled);
    }

    // ---- đà kinetic từ chuột ----

    [Fact]
    public void Flick_StartsAGlide_ThatMovesTheImageEachFrame_AndAPressStopsIt()
    {
        using var rig = new InputRig(configure: s => s.KineticPanEnabled = true);
        ActualSize(rig);
        rig.MessageTime = 1000;
        rig.Mouse(LDown, 800, 300);
        for (var i = 1; i <= 6; i++)
        {
            rig.MessageTime = 1000 + (i * 8);
            rig.Mouse(Move, 800 - (i * 40), 300, MkL);
        }

        rig.MessageTime = 1000 + 56;
        rig.Mouse(LUp, 800 - 240, 300);
        Assert.Equal(1, rig.Clock.SubscriberCount);

        var offsets = new List<double>();
        rig.Timestamp = 0;
        for (var i = 0; i < 8; i++)
        {
            rig.Clock.Tick(i * 16.0);
            rig.Timestamp += 160_000;
            rig.Drain();
            offsets.Add(rig.Viewport.HorizontalOffset);
        }

        Assert.True(offsets[^1] > offsets[0]);
        Assert.True(offsets.Zip(offsets.Skip(1), (a, b) => b >= a).All(x => x));

        rig.Mouse(LDown, 500, 300); // nhấn dừng đà (và lần nhấn đó không phải click)
        Assert.Equal(0, rig.Clock.SubscriberCount);
    }
}

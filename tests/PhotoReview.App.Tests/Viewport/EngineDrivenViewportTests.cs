using System.Windows;
using System.Windows.Input;
using PhotoReview.App.Coordinators;
using PhotoReview.App.Input;
using PhotoReview.App.ViewModels;
using PhotoReview.Core.Model;

namespace PhotoReview.App.Tests.Viewport;

/// <summary>
/// WP-16: <see cref="FitViewController"/> và <see cref="PointerInputController"/> THẬT (không sửa) chạy trên engine thuần
/// (<see cref="EngineImageSurface"/> = <c>ViewportState</c> + <c>ViewportLayoutEngine</c>). Chứng minh: vòng hội tụ Fit (T89)
/// giữ đúng các pass và điều kiện dừng của nó trên engine đồng bộ; zoom neo con trỏ/tâm giữ điểm ảnh; FitWidth neo 1/3 trên,
/// giữa, 1/3 dưới với hiệu chỉnh thanh cuộn bên; pan/kinetic/mũi tên kẹp trong [0, Max]; DPI chỉ đổi zoom, không đổi DIP.
/// </summary>
public sealed class EngineDrivenViewportTests
{
    private readonly ViewerState _viewer = new();
    private readonly ViewportOperationVersion _version = new();
    private readonly AppSettings _settings = new()
    {
        MouseWheelAction = MouseWheelAction.Zoom,
        ClickToZoomEnabled = true,
        ClickZoomPercent = 200,
        KineticPanEnabled = false,
        KineticGlideSmoothing = KineticGlideSmoothing.Off,
        KeyboardZoomAnchor = KeyboardZoomAnchor.Pointer,
    };

    private EngineImageSurface _surface = null!;
    private PointerInputController _pointer = null!;
    private FitViewController _fit = null!;

    private void Open(int pixelWidth, int pixelHeight, double clientWidth = 1280, double clientHeight = 720, double dpi = 1.0)
    {
        _viewer.DpiScale = dpi;
        _viewer.SetSourceSize(pixelWidth, pixelHeight, newImage: true);
        _surface = new EngineImageSurface(_viewer, clientWidth, clientHeight, (pixelWidth / 4.0, pixelHeight / 4.0));
        _pointer = new PointerInputController(_surface, _viewer, () => _settings, _version,
            new PointerCommands(() => true, () => Task.CompletedTask, () => Task.CompletedTask, _viewer.ZoomToActualSize, () => _fit.ApplyFitAsync()));
        _fit = new FitViewController(_surface, _viewer, _version, _pointer.CancelPan);
        _viewer.ResetFit(clientWidth, clientHeight);
        _surface.Pump();
    }

    private void AssertOffsetsInRange()
    {
        var layout = _surface.State.Layout;
        Assert.InRange(_surface.HorizontalOffset, 0, layout.MaxHorizontalOffset);
        Assert.InRange(_surface.VerticalOffset, 0, layout.MaxVerticalOffset);
    }

    // ---- Fit / T89 ----

    [Fact]
    public async Task Fit_FromAScrolledZoom_IsStableOnTheSecondPass_ThenScrollsHome_AndFillsTheClient()
    {
        Open(6000, 4000);
        _viewer.SetZoom(1.0);
        _surface.ScrollTo(900, 700);
        _surface.Pump();
        Assert.Equal(900, _surface.HorizontalOffset);
        _surface.Log.Clear();

        await _surface.Run(_fit.ApplyFitAsync());

        // Hai pass (pass 2 xác nhận ổn định), không phải một: vòng T89 không bị rút gọn khi chạy trên engine đồng bộ.
        Assert.Equal("UpdateLayout UpdateFitSize Yield Capture UpdateLayout UpdateFitSize Yield Capture UpdateLayout ScrollHome",
            string.Join(' ', _surface.Log));
        Assert.True(_viewer.IsFit);
        Assert.Equal((0.0, 0.0), (_surface.HorizontalOffset, _surface.VerticalOffset));
        var layout = _surface.State.Layout;
        Assert.False(layout.HorizontalBarVisible || layout.VerticalBarVisible);
        Assert.Equal(new PhotoReview.App.Input.RectD(100, 0, 1080, 720), layout.ImageRect);
    }

    [Fact]
    public async Task Fit_WhenTheWindowIsResizedDuringTheSecondPass_RunsTheThirdPass_AndFitsTheNewClient()
    {
        Open(6000, 4000);
        _viewer.SetZoom(2.0);
        _surface.Pump();
        _surface.Log.Clear();
        _surface.AtYield[2] = () => _surface.Resize(1000, 700); // giữa pass 1 và pass 2 (Open/Pump chưa yield lần nào)

        await _surface.Run(_fit.ApplyFitAsync());

        Assert.Equal(3, _surface.Log.Count(e => e == "Capture"));
        Assert.Equal(1, _surface.Log.Count(e => e == "ScrollHome"));
        Assert.Equal(1000, _viewer.MaxImageWidth);
        Assert.Equal(700, _viewer.MaxImageHeight);
        Assert.Equal(1000, _surface.ImageActualWidth, 9);
        Assert.Equal(4000 * (1000 / 6000.0), _surface.ImageActualHeight, 9);
    }

    [Fact]
    public async Task Fit_WithAStaleBoundThatOverflows_IsPulledBackToTheClient_ByTheSizeChangedHookInTheSameLayout()
    {
        // T89: MaxImage cũ (lớn hơn client) làm ảnh Fit tràn; MainImage_SizeChanged -> UpdateFitSize kéo về client ngay trong
        // lượt layout đó (LayoutManager chạy lại tới khi sạch), nên không khung nào có thanh cuộn.
        Open(6000, 4000);
        var hooksBefore = _surface.SizeChangedFitUpdates;
        _viewer.UpdateViewport(3000, 2000, force: true);
        _surface.Pump();
        Assert.True(_surface.SizeChangedFitUpdates > hooksBefore); // tràn -> kéo về (và SizeChanged lần nữa khi về 1080x720)
        Assert.False(_surface.State.Layout.VerticalBarVisible);
        Assert.Equal(1280, _viewer.MaxImageWidth);

        await _surface.Run(_fit.ApplyFitAsync());

        Assert.False(_surface.State.Layout.VerticalBarVisible);
        Assert.Equal(1080, _surface.ImageActualWidth, 9);
        Assert.Equal(1280, _surface.ViewportWidth);
    }

    // ---- zoom neo con trỏ / tâm ----

    [Fact]
    public async Task WheelZoom_FromFit_KeepsTheImagePointUnderTheCursor()
    {
        Open(6000, 4000);
        var cursor = new PointD(400, 300);
        var before = _surface.ImageFractionAt(cursor);

        await _surface.Run(_pointer.OnWheelAsync(120, ctrl: false, cursor));

        Assert.False(_viewer.IsFit);
        Assert.Equal(0.18 + 0.25, _viewer.Zoom, 9); // bước từ FitZoom = min(1280/6000, 720/4000)
        var after = _surface.ImageFractionAt(cursor);
        Assert.Equal(before.X, after.X, 9);
        Assert.Equal(before.Y, after.Y, 9);
        AssertOffsetsInRange();
    }

    [Fact]
    public async Task WheelZoom_TwiceAtDifferentPoints_KeepsEachPoint_AndStaysInRange()
    {
        Open(4000, 6000, dpi: 1.25);
        foreach (var cursor in new[] { new PointD(900, 200), new PointD(100, 650), new PointD(1270, 715) })
        {
            var before = _surface.ImageFractionAt(cursor);
            await _surface.Run(_pointer.OnWheelAsync(120, ctrl: false, cursor));
            var after = _surface.ImageFractionAt(cursor);
            AssertOffsetsInRange();
            // Giữ điểm trừ khi bị kẹp ở mép: khi đó offset nằm ở 0 hoặc Max.
            var layout = _surface.State.Layout;
            if (_surface.HorizontalOffset > 0 && _surface.HorizontalOffset < layout.MaxHorizontalOffset) Assert.Equal(before.X, after.X, 9);
            if (_surface.VerticalOffset > 0 && _surface.VerticalOffset < layout.MaxVerticalOffset) Assert.Equal(before.Y, after.Y, 9);
        }
    }

    [Fact]
    public async Task WheelZoom_AtTheTopLeftCorner_BesideTheCentredImage_ClampsTheOffsetsAtZero()
    {
        Open(6000, 4000);
        await _surface.Run(_pointer.OnWheelAsync(120, ctrl: false, new PointD(0, 0))); // x = 0 nằm trong lề trái 100 DIP của ảnh Fit
        _surface.Pump();
        Assert.Equal((0.0, 0.0), (_surface.HorizontalOffset, _surface.VerticalOffset));
    }

    [Fact]
    public async Task KeyboardZoom_WithTheCentreAnchor_KeepsTheViewportCentre()
    {
        Open(6000, 4000);
        _settings.KeyboardZoomAnchor = KeyboardZoomAnchor.ViewportCentre;
        _surface.PointerPosition = new PointD(10, 10); // bị bỏ qua với Centre
        _viewer.SetZoom(1.0);
        _surface.ScrollTo(1500, 900);
        _surface.Pump();
        var centre = new PointD(_surface.ViewportWidth / 2, _surface.ViewportHeight / 2);
        var before = _surface.ImageFractionAt(centre);

        await _surface.Run(_pointer.ZoomInAsync());

        Assert.Equal(1.25, _viewer.Zoom, 9);
        var after = _surface.ImageFractionAt(new PointD(_surface.ViewportWidth / 2, _surface.ViewportHeight / 2));
        Assert.Equal(before.X, after.X, 9);
        Assert.Equal(before.Y, after.Y, 9);
    }

    [Fact]
    public async Task KeyboardZoom_WithThePointerAnchor_KeepsThePointUnderTheMouse()
    {
        Open(6000, 4000);
        _viewer.SetZoom(1.0);
        _surface.ScrollTo(1500, 900);
        _surface.Pump();
        var mouse = new PointD(200, 500);
        _surface.PointerPosition = mouse;
        var before = _surface.ImageFractionAt(mouse);

        await _surface.Run(_pointer.ZoomOutAsync());

        Assert.Equal(0.75, _viewer.Zoom, 9);
        var after = _surface.ImageFractionAt(mouse);
        Assert.Equal(before.X, after.X, 9);
        Assert.Equal(before.Y, after.Y, 9);
    }

    [Fact]
    public async Task ClickZoom_FromFit_ZoomsToTheClickLevelAtTheCursor()
    {
        Open(6000, 4000);
        var cursor = new PointD(700, 250);
        var before = _surface.ImageFractionAt(cursor);

        Assert.True(_pointer.OnImagePress(PointerButton.Left, 1, cursor, 0));
        _pointer.OnImageRelease(cursor, 50);
        _surface.Pump();

        Assert.Equal(2.0, _viewer.Zoom, 9);
        var after = _surface.ImageFractionAt(cursor);
        Assert.Equal(before.X, after.X, 9);
        Assert.Equal(before.Y, after.Y, 9);
    }

    // ---- FitWidth (neo 1/3 trên / giữa / 1/3 dưới) + FitHeight, hiệu chỉnh thanh cuộn bên ----

    [Theory]
    [InlineData(FitWidthAnchor.TopThird, 1.0 / 3.0, 1.0)]
    [InlineData(FitWidthAnchor.Centre, 0.5, 1.0)]
    [InlineData(FitWidthAnchor.BottomThird, 2.0 / 3.0, 1.0)]
    [InlineData(FitWidthAnchor.TopThird, 1.0 / 3.0, 1.5)]
    [InlineData(FitWidthAnchor.BottomThird, 2.0 / 3.0, 2.0)]
    public async Task FitWidth_FillsTheWidthBesideTheVerticalBar_AndPutsTheAnchorAtTheViewportCentre(FitWidthAnchor anchor, double fraction, double dpi)
    {
        Open(4000, 6000, dpi: dpi);

        await _surface.Run(_pointer.FitWidthAsync(anchor));

        var layout = _surface.State.Layout;
        // Lượt 1: 1280 DIP -> ảnh 1280x1920 -> thanh dọc -> 1280 > 1270 -> cả thanh ngang. Hiệu chỉnh: lấp 1270 -> chỉ còn thanh dọc.
        Assert.Equal(1270 * dpi / 4000, _viewer.Zoom, 9);
        Assert.False(layout.HorizontalBarVisible);
        Assert.True(layout.VerticalBarVisible);
        Assert.Equal(1270, layout.ViewportWidth, 9);
        Assert.Equal(720, layout.ViewportHeight, 9);
        Assert.Equal(1270, _surface.ImageActualWidth, 9);
        Assert.Equal(0, layout.MaxHorizontalOffset, 9);
        // Hành vi hiện có của PointerInputController (không phải của engine): pass hiệu chỉnh đọc ViewportCentre khi CẢ HAI
        // thanh đang hiện (1270x710), nên điểm neo nằm ở y = 355, thấp hơn tâm cuối (360) nửa độ dày thanh cuộn.
        var anchoredAt = _surface.ImageFractionAt(new PointD(1270 / 2.0, (720 - EngineImageSurface.ScrollBarThickness) / 2));
        Assert.Equal(0.5, anchoredAt.X, 9);
        Assert.Equal(fraction, anchoredAt.Y, 9);
    }

    [Fact]
    public async Task FitWidth_OnALandscape_TopThirdClampsAtTheTop()
    {
        Open(6000, 4000);
        await _surface.Run(_pointer.FitWidthAsync(FitWidthAnchor.TopThird));
        Assert.Equal(0, _surface.VerticalOffset); // 1/3 của 846,7 = 282 < nửa viewport 360: kẹp ở 0
        Assert.Equal(1270, _surface.ImageActualWidth, 9);
    }

    [Fact]
    public async Task FitHeight_OnAPanorama_FillsTheHeightAboveTheHorizontalBar_Centred()
    {
        Open(12000, 1000);

        await _surface.Run(_pointer.FitHeightAsync());

        var layout = _surface.State.Layout;
        Assert.Equal(710 / 1000.0, _viewer.Zoom, 9);
        Assert.True(layout.HorizontalBarVisible);
        Assert.False(layout.VerticalBarVisible);
        Assert.Equal(710, _surface.ImageActualHeight, 9);
        // Lượt 1: 8640x720 -> thanh ngang -> 720 > 710 -> cả thanh dọc; pass hiệu chỉnh neo ở tâm của viewport 1270x710 (x = 635).
        Assert.Equal((12000 * 0.71 / 2) - 635, _surface.HorizontalOffset, 9);
    }

    // ---- pan / kinetic / mũi tên ----

    [Fact]
    public void DragPan_IsClampedAtBothEnds()
    {
        Open(6000, 4000);
        _viewer.SetZoom(1.0);
        _surface.Pump();
        Assert.True(_pointer.OnImagePress(PointerButton.Left, 1, new PointD(600, 400), 0));
        _pointer.OnImageMove(true, new PointD(5600, 4400), 10);
        _surface.Pump();
        Assert.Equal((0.0, 0.0), (_surface.HorizontalOffset, _surface.VerticalOffset));
        _pointer.OnImageMove(true, new PointD(-9400, -8600), 20);
        _surface.Pump();
        var layout = _surface.State.Layout;
        Assert.Equal((layout.MaxHorizontalOffset, layout.MaxVerticalOffset), (_surface.HorizontalOffset, _surface.VerticalOffset));
        Assert.Equal((4730.0, 3290.0), (layout.MaxHorizontalOffset, layout.MaxVerticalOffset)); // 6000 - 1270, 4000 - 710
        _pointer.OnImageRelease(new PointD(-9400, -8600), 30);
    }

    [Fact]
    public void KineticGlide_StaysInRangeEveryFrame_AndStopsOnItsOwn()
    {
        _settings.KineticPanEnabled = true;
        Open(6000, 4000);
        _viewer.SetZoom(1.0);
        _surface.ScrollTo(4000, 1000);
        _surface.Pump();
        _pointer.OnImagePress(PointerButton.Left, 1, new PointD(900, 400), 0);
        for (var i = 1; i <= 5; i++)
        {
            _pointer.OnImageMove(true, new PointD(900 - (i * 60), 400), i * 8);
            _surface.Pump();
        }
        _pointer.OnImageRelease(new PointD(560, 400), 48);
        Assert.True(_surface.HasFrameHandler);

        var previous = _surface.HorizontalOffset;
        var frames = 0;
        for (var t = 0; _surface.HasFrameHandler && frames < 5000; t += 16, frames++)
        {
            _surface.Frame(TimeSpan.FromMilliseconds(t));
            AssertOffsetsInRange();
            Assert.True(_surface.HorizontalOffset >= previous); // kéo sang trái -> nội dung trôi tiếp sang phải
            previous = _surface.HorizontalOffset;
        }
        Assert.False(_surface.HasFrameHandler);
        Assert.Equal(_surface.State.Layout.MaxHorizontalOffset, _surface.HorizontalOffset, 9); // 4000 + quán tính chạm mép 4730
    }

    // Chủ ý chọn số để đúng với CẢ quy tắc pan phím cũ (mỗi trục x viewport trục đó) lẫn quy tắc mới (x cạnh ngắn, ArrowPanRule):
    // bước sang phải bị kẹp ở mép, bước xuống = 10 % của 710 = cạnh ngắn. PR đổi KeyboardPan.Step không làm đỏ test này.
    [Fact]
    public void ArrowPan_IsClampedAtTheEdge_AndIsANoOpThere()
    {
        Open(6000, 4000);
        _viewer.SetZoom(1.0);
        _surface.ScrollTo(4700, 0);
        _surface.Pump();

        Assert.True(_pointer.TryPanByArrow(KeyId.Right, isRepeat: false));
        _surface.Pump();
        Assert.Equal(4730, _surface.HorizontalOffset, 9); // 4700 + bước (cũ 127 / mới 71) kẹp ở Max 4730

        Assert.True(_pointer.TryPanByArrow(KeyId.Right, isRepeat: false)); // ở mép: nuốt phím, không đổi
        _surface.Pump();
        Assert.Equal(4730, _surface.HorizontalOffset, 9);

        Assert.True(_pointer.TryPanByArrow(KeyId.Down, isRepeat: false));
        _surface.Pump();
        Assert.Equal(71, _surface.VerticalOffset, 9); // 10 % của viewport 710
    }

    // ---- đổi bitmap cùng ảnh (RAW: LibRaw lệch vài pixel) ----

    [Fact]
    public async Task SourceSizeSwap_WhileZoomed_KeepsTheViewportCentre()
    {
        Open(6000, 4000);
        _viewer.SetZoom(2.0);
        _surface.ScrollTo(5000, 3000);
        _surface.Pump();
        var centre = new PointD(_surface.ViewportWidth / 2, _surface.ViewportHeight / 2);
        var before = _surface.ImageFractionAt(centre);

        _viewer.SwapSourceSize(6024, 4016);
        _surface.SetBitmap(6024, 4016);
        _surface.Pump(); // lượt Render mà AnchorAfterSwapAsync chờ: layout đã có kích thước mới

        Assert.Equal(6024 * 2.0, _surface.ImageActualWidth, 9);
        var after = _surface.ImageFractionAt(centre);
        Assert.Equal(before.X, after.X, 9);
        Assert.Equal(before.Y, after.Y, 9);
    }
}

using PhotoReview.App.Coordinators;
using PhotoReview.App.Input;
using PhotoReview.App.ViewModels;
using PhotoReview.App.Viewport;
using PhotoReview.Core.Abstractions;
using PhotoReview.Shell.Win32.Viewing;

namespace PhotoReview.Shell.Win32.Input;

/// <summary>
/// WP-22: <see cref="IImageSurface"/> + <see cref="IFitSurface"/> (C-07) của <see cref="ViewportController"/> (WP-16). WP-16 để
/// controller chưa khai báo hai interface vì chúng còn ở App (hợp đồng v1.1); từ WP-09 chúng đã ở App.Shared nên adapter mỏng này
/// nối chúng mà không phải sửa <c>Viewing/*</c> (vùng của WP-21). Chỉ chuyển tiếp, cộng <see cref="Capture"/> (snapshot cho
/// <see cref="FitViewController"/>), nên không có logic để sai lệch khỏi controller.
/// </summary>
internal sealed class ViewportImageSurface(ViewportController viewport, ViewerState viewer) : IImageSurface, IFitSurface
{
    // ---- IFitSurface ----

    public (double Width, double Height) ViewportSize => viewport.ViewportSize;

    public bool IsLoaded => viewport.IsLoaded;

    public void UpdateLayout() => viewport.UpdateLayout();

    public void UpdateFitSize() => viewport.UpdateFitSize();

    public Task YieldToRenderAsync() => viewport.YieldToRenderAsync();

    /// <summary>Cùng các trường mà <c>MainWindow.CaptureViewportSnapshot</c> so giữa hai pass của Fit (T89).</summary>
    public ViewportSnapshot Capture()
    {
        var layout = viewport.Layout;
        return new ViewportSnapshot(viewer.Zoom, viewer.Stretch, viewer.MaxImageWidth, viewer.MaxImageHeight,
            viewport.ImageActualWidth, viewport.ImageActualHeight, layout.ExtentWidth, layout.ExtentHeight,
            layout.ViewportWidth, layout.ViewportHeight, viewport.HorizontalOffset, viewport.VerticalOffset,
            layout.HorizontalBarVisible, layout.VerticalBarVisible);
    }

    public void ScrollHome() => viewport.ScrollHome();

    // ---- IImageSurface ----

    public double HorizontalOffset => viewport.HorizontalOffset;

    public double VerticalOffset => viewport.VerticalOffset;

    public double ViewportWidth => viewport.ViewportWidth;

    public double ViewportHeight => viewport.ViewportHeight;

    public double ExtentWidth => viewport.ExtentWidth;

    public double ExtentHeight => viewport.ExtentHeight;

    public (double Horizontal, double Vertical) DragThreshold => viewport.DragThreshold;

    public void ScrollTo(double horizontal, double vertical) => viewport.ScrollTo(horizontal, vertical);

    public PointD ImageOrigin => viewport.ImageOrigin;

    public PointD ToImageElement(PointD surfacePoint) => viewport.ToImageElement(surfacePoint);

    public double ImageActualWidth => viewport.ImageActualWidth;

    public double ImageActualHeight => viewport.ImageActualHeight;

    public (double Width, double Height)? SourceSize => viewport.SourceSize;

    public void CaptureMouse() => viewport.CaptureMouse();

    public void ReleaseMouseCapture() => viewport.ReleaseMouseCapture();

    public void SetPanCursor(bool panning) => viewport.SetPanCursor(panning);

    public void HookRenderFrame(EventHandler handler) => viewport.HookRenderFrame(handler);

    public void UnhookRenderFrame(EventHandler handler) => viewport.UnhookRenderFrame(handler);

    public TimeSpan? RenderingTime(EventArgs e) => viewport.RenderingTime(e);

    public long Timestamp => viewport.Timestamp;

    public DisplayTiming? DisplayTiming => viewport.DisplayTiming;

    public PointD? PointerPosition => viewport.PointerPosition;
}

using PhotoReview.App.Input;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Imaging.Pixels;
using PhotoReview.Shell.Rendering;

namespace PhotoReview.Shell.Win32;

// WP-20 giai đoạn A: khung vẽ tối thiểu (canvas + ảnh hiện tại theo layout của ViewportController). WP-21 (trình bày ảnh,
// compare, crossfade) thay thân DrawContent; WP-23 vẽ overlay sau nó. Không đổi vòng đời khung (RenderFrame/RenderNow).
internal sealed partial class ShellMainWindow
{
    private bool _firstImageDrawn;
    private bool _imageDrawnInFrame;

    /// <summary>Khung do <c>Invalidate</c> yêu cầu tới nhịp (<see cref="ShellWindow.Render"/>).</summary>
    private void RenderFrame() => Present();

    /// <summary>
    /// Layout + vẽ + Present đồng bộ, không chờ nhịp khung: dùng cho khung đầu (cửa sổ phải hiện cái gì đó ngay) và ảnh đầu.
    /// Lỗi vẽ không làm sập tiến trình: log, khung này bỏ.
    /// </summary>
    internal void RenderNow() => Present();

    private void Present()
    {
        if (_disposed || _surface is null || _window.IsDestroyed)
        {
            return;
        }

        var (width, height) = _window.ClientSizePixels;
        if (width <= 0 || height <= 0)
        {
            return; // thu nhỏ: không có gì để vẽ; khung sau khi khôi phục sẽ vẽ
        }

        PresentResult result;
        try
        {
            if (_surface.PixelWidth != width || _surface.PixelHeight != height)
            {
                _surface.Resize(width, height, _window.DpiScale);
            }

            // Layout đã xếp mà chưa chạy: chạy ngay để khung này dùng kích thước/offset mới (như UpdateLayout của WPF).
            if (_viewport.IsLayoutPosted)
            {
                _viewport.UpdateLayout();
            }

            // Khung ảnh đầu không chờ vblank (xem D2DRenderSurface.ImmediatePresent); sau đó Present(1) bình thường.
            _surface.ImmediatePresent = !_firstImageDrawn;
            var context = _surface.BeginDrawContext();
            try
            {
                context.Clear(CanvasColor);
                DrawContent(context);
            }
            finally
            {
                result = _surface.EndDrawAndPresent();
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or ObjectDisposedException)
        {
            _log.Error("Frame failed", ex);
            return;
        }

        if (result == PresentResult.DeviceRecreated || result == PresentResult.Failed)
        {
            // Thiết bị mất: cache đã tự xoá (DeviceRecreated); vẽ lại khung ở nhịp kế (ảnh được upload lại từ PixelBuffer).
            _window.Invalidate();
            if (result == PresentResult.Failed)
            {
                return;
            }
        }

        // Occluded (bị che/thu nhỏ/desktop không tương tác): khung đã vẽ và nộp, chỉ là không có gì hiện ra. Vẫn tính là đã
        // present để cloak được bỏ và mốc khởi động tiến lên (khi hết che, WM_PAINT -> Invalidate vẽ lại).
        if (result is PresentResult.Presented or PresentResult.DeviceRecreated or PresentResult.Occluded)
        {
            OnFramePresented();
        }
    }

    private void DrawContent(D2DDrawContext context)
    {
        if (_image is not { IsDisposed: false } pixels || _cache is null)
        {
            return;
        }

        IGpuImage gpu;
        try
        {
            gpu = _cache.GetOrUpload(pixels);
        }
        catch (ObjectDisposedException)
        {
            return; // bộ đệm CPU vừa giải phóng buffer này: ảnh thay thế sắp tới
        }

        var origin = _viewport.ImageOrigin;
        var destination = new RectD(origin.X, origin.Y, _viewport.ImageActualWidth, _viewport.ImageActualHeight);
        var (viewportWidth, viewportHeight) = (_window.ClientSizeDip.Width, _window.ClientSizeDip.Height);
        context.PushClip(new RectD(0, 0, viewportWidth, viewportHeight));
        try
        {
            context.DrawImage(gpu, destination, null,
                ImageInterpolations.FromScalingQuality(_viewModel.Viewer.EffectiveScalingQuality));
        }
        finally
        {
            context.PopClip();
        }

        _imageDrawnInFrame = true;
    }

    private void OnFramePresented()
    {
        var firstImage = false;
        var imageDrawn = _imageDrawnInFrame;
        if (_imageDrawnInFrame)
        {
            _imageDrawnInFrame = false;
            firstImage = !_firstImageDrawn;
            _firstImageDrawn = true;
        }

        _hub.NoteFramePresented(imageDrawn);
        if (!_firstPresentDone)
        {
            _firstPresentDone = true;
            _mark(ShellPerfMarks.FirstPresent);
            _cloak.NoteFirstPresent();
            FirstPresented?.Invoke(this, EventArgs.Empty);
        }

        if (firstImage)
        {
            FirstImagePresentedEvent?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Khung đầu tiên (canvas hoặc ảnh) đã Present: cửa sổ đã được bỏ cloak.</summary>
    internal event EventHandler? FirstPresented;

    /// <summary>Đã có một khung chứa ảnh được Present (mốc "ảnh đầu đã vẽ"; cho test/smoke).</summary>
    internal bool FirstImagePresented => _firstImageDrawn;

    /// <summary>Khung đầu tiên có ảnh đã Present (không phát ở canvas trống).</summary>
    internal event EventHandler? FirstImagePresentedEvent;
}

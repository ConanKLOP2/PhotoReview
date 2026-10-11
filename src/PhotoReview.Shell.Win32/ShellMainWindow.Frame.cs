using System.Diagnostics;
using PhotoReview.App.Input;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Imaging.Pixels;
using PhotoReview.Shell.Rendering;
using PhotoReview.Shell.Win32.Viewing;

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

        // WP-21: nhịp khung - Present không được chặn UI thread (FramePacer). Ảnh đầu: Immediate tới khi đã vẽ.
        var awaitingFirstImage = !_firstImageDrawn;
        var mode = _pacer.ModeFor(_viewModel.Viewer.IsFullscreen, awaitingFirstImage);
        if (!awaitingFirstImage && _surface.HasFrameLatencyWaitHandle && _pacer.ShouldDefer(_surface.WaitForNextFrame(0)))
        {
            _window.Invalidate(); // swap chain chưa nhận khung mới: để nhịp sau, không chặn
            return;
        }

        PresentResult result;
        var frameStart = Stopwatch.GetTimestamp();
        var presentTicks = 0L;
        var presentStartTicks = 0L;
        try
        {
            // WP-25: DPI đổi mà số pixel giữ nguyên (đổi màn hình DPI khác, rect gợi ý cùng cỡ) vẫn phải đặt lại tỉ lệ của bề mặt.
            if (_surface.PixelWidth != width || _surface.PixelHeight != height || _surface.DpiScale != _window.DpiScale)
            {
                _surface.Resize(width, height, _window.DpiScale);
            }

            // WP-21: bản nâng cấp (full-res) đã upload xong thì thành ảnh chính TRƯỚC layout, để khung này đo theo bitmap mới.
            _composer?.TryPromotePending();

            // Layout đã xếp mà chưa chạy: chạy ngay để khung này dùng kích thước/offset mới (như UpdateLayout của WPF).
            if (_viewport.IsLayoutPosted)
            {
                _viewport.UpdateLayout();
            }

            _surface.ImmediatePresent = mode == PresentMode.Immediate;
            var context = _surface.BeginDrawContext();
            try
            {
                context.Clear(CanvasColor);
                DrawContent(context);
            }
            finally
            {
                var presentStart = Stopwatch.GetTimestamp();
                presentStartTicks = presentStart;
                result = _surface.EndDrawAndPresent();
                presentTicks = Stopwatch.GetTimestamp() - presentStart;
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or ObjectDisposedException)
        {
            _log.Error("Frame failed", ex);
            return;
        }

        _pacer.ObservePresent(mode, FrameStats.ToMs(presentTicks));
        var endDrawMs = FrameStats.ToMs(_surface.LastEndDrawTicks);
        var surfacePresentMs = FrameStats.ToMs(_surface.LastPresentTicks);
        if (FrameStats.IsSlow(endDrawMs, surfacePresentMs))
        {
            // Chỉ khung chậm mới dựng chuỗi chẩn đoán (đường nóng không cấp phát).
            Stats.RecordSlow(FrameStats.ToMs(Stopwatch.GetTimestamp() - frameStart), FrameStats.ToMs(presentStartTicks - frameStart),
                endDrawMs, surfacePresentMs,
                $"mode={mode} result={result} img={_composer?.Current?.Width}x{_composer?.Current?.Height} fade={_composer?.Crossfade.IsActive}");
        }

        Stats.Record(Stopwatch.GetTimestamp() - frameStart, presentTicks);
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

    /// <summary>WP-21: ảnh chính, lớp ảnh cũ crossfade, compare (<c>ShellMainWindow.Viewing.cs</c>). WP-23 vẽ overlay sau nó.</summary>
    private void DrawContent(D2DDrawContext context) => DrawPresentation(context);

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

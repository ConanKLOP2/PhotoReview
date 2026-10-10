using PhotoReview.App.Input;
using PhotoReview.Imaging.Pixels;

namespace PhotoReview.Shell.Rendering;

// C-09 (NO-WPF-EXEC-PLAN mục 5): bề mặt vẽ Direct2D. Thực thi ở WP-15. Toạ độ vẽ là DIP (SetDpi), kích thước bề mặt là px.

public readonly record struct ColorF(float R, float G, float B, float A);

/// <summary>ScalingQuality.Linear -&gt; Linear, HighQuality -&gt; HighQualityCubic.</summary>
public enum ImageInterpolation
{
    NearestNeighbor = 0,
    Linear = 1,
    HighQualityCubic = 2,
}

public enum PresentResult
{
    Presented = 0,
    Occluded = 1,
    DeviceRecreated = 2,
    Failed = 3,
}

public sealed record RenderSurfaceOptions(bool PreferWarp = false, bool AllowTearing = false, int MaxFrameLatency = 1);

public interface IRenderSurface : IDisposable
{
    nint Hwnd { get; }

    int PixelWidth { get; }

    int PixelHeight { get; }

    /// <summary>GetDpiForWindow / 96.</summary>
    double DpiScale { get; }

    /// <summary>Cho message loop (C-05).</summary>
    nint FrameLatencyWaitHandle { get; }

    /// <summary>True khi rơi về WARP (RDP/không GPU) hoặc test.</summary>
    bool IsWarp { get; }

    /// <summary>ResizeBuffers; gọi trên UI thread.</summary>
    void Resize(int pixelWidth, int pixelHeight, double dpiScale);

    /// <summary>DIP -&gt; px qua SetDpi.</summary>
    IDrawContext BeginDraw();

    /// <summary>Present(1, 0); D2DERR_RECREATE_TARGET / DXGI_ERROR_DEVICE_REMOVED -&gt; tạo lại, trả DeviceRecreated.</summary>
    PresentResult EndDrawAndPresent();

    /// <summary>GpuImageCache phải xoá hết.</summary>
    event EventHandler? DeviceRecreated;

    static abstract IRenderSurface CreateForWindow(nint hwnd, RenderSurfaceOptions options);

    /// <summary>WARP, cho test.</summary>
    static abstract IRenderSurface CreateOffscreen(int pixelWidth, int pixelHeight, double dpiScale);
}

public interface IDrawContext
{
    void Clear(ColorF color);

    void DrawImage(IGpuImage image, RectD destination, RectD? sourcePixels, ImageInterpolation interpolation, float opacity = 1f);

    void FillRectangle(RectD rect, ColorF color);

    void FillRoundedRectangle(RectD rect, double radius, ColorF color);

    void DrawRectangle(RectD rect, ColorF color, double strokeWidth);

    void DrawText(ITextLayout layout, PointD origin, ColorF color);

    void PushClip(RectD rect);

    void PopClip();

    void PushOpacity(float opacity);

    void PopOpacity();

    /// <summary>Chỉ offscreen/WARP; cho golden G-PIX.</summary>
    bool TryReadPixels(out PixelBuffer pixels);
}

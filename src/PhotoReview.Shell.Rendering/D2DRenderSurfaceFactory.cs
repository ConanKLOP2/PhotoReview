namespace PhotoReview.Shell.Rendering;

/// <summary>
/// WP-15 / hợp đồng v1.1 (C-09): <see cref="IRenderSurfaceFactory"/> mỏng trên hai hàm tĩnh của <see cref="D2DRenderSurface"/>.
/// Không giữ trạng thái; dùng chung một thể hiện hay tạo mới đều được.
/// </summary>
public sealed class D2DRenderSurfaceFactory : IRenderSurfaceFactory
{
    public IRenderSurface CreateForWindow(nint hwnd, RenderSurfaceOptions options) => D2DRenderSurface.CreateForWindow(hwnd, options);

    public IRenderSurface CreateOffscreen(int pixelWidth, int pixelHeight, double dpiScale) =>
        D2DRenderSurface.CreateOffscreen(pixelWidth, pixelHeight, dpiScale);
}

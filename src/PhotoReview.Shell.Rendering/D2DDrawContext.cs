using System.Numerics;
using PhotoReview.App.Input;
using PhotoReview.Imaging.Pixels;
using PhotoReview.Shell.Interop.Graphics;

namespace PhotoReview.Shell.Rendering;

/// <summary>
/// WP-15 (C-09): ngữ cảnh vẽ của một khung (giữa <see cref="IRenderSurface.BeginDraw"/> và
/// <see cref="IRenderSurface.EndDrawAndPresent"/>). Toạ độ là DIP (DPI của đích = 96 x DpiScale); <c>sourcePixels</c> là
/// pixel của ảnh (bitmap 96 DPI). Màu <see cref="ColorF"/> là alpha thẳng (D2D tự premultiply); bitmap nguồn là
/// premultiplied (Pbgra32) hoặc bỏ alpha (Bgr32).
/// <para>
/// Ngoài C-09: <see cref="PushTransform"/>/<see cref="PopTransform"/> (ma trận pan/zoom/xoay) và
/// <see cref="DrawImageOriented"/> (vẽ ảnh thô theo orientation EXIF, cùng kết quả với <c>PixelOps.ApplyOrientation</c>).
/// </para>
/// <para>
/// Opacity: <see cref="PushOpacity"/> nhân dồn vào opacity của từng lệnh vẽ (brush/bitmap) - không dùng layer. Khác layer
/// chỉ khi các hình trong cùng nhóm chồng nhau (overlay của app không chồng nhau).
/// </para>
/// </summary>
public sealed unsafe class D2DDrawContext : IDrawContext
{
    private readonly D2DRenderSurface _surface;
    private readonly Stack<float> _opacity = new();
    private readonly Stack<Matrix3x2> _transforms = new();
    private float _currentOpacity = 1f;
    private Matrix3x2 _transform = Matrix3x2.Identity;
    private int _clipDepth;
    private bool _active;

    internal D2DDrawContext(D2DRenderSurface surface)
    {
        _surface = surface;
    }

    /// <summary>Số lần <see cref="DrawImage"/> bỏ qua ảnh không vẽ được (đã evict, prefetch dở, hoặc của thiết bị cũ).</summary>
    public long SkippedImageDraws { get; private set; }

    /// <summary>Ma trận hiện tại (local DIP -&gt; DIP của đích).</summary>
    public Matrix3x2 Transform => _transform;

    private ID2D1DeviceContext Context
    {
        get
        {
            if (!_active)
            {
                throw new InvalidOperationException("The draw context is only valid between BeginDraw and EndDrawAndPresent.");
            }

            return _surface.Resources.Context;
        }
    }

    public void Clear(ColorF color)
    {
        D2dColorF c = ToD2D(color, 1f);
        Context.Clear(&c);
    }

    public void DrawImage(IGpuImage image, RectD destination, RectD? sourcePixels, ImageInterpolation interpolation, float opacity = 1f)
    {
        ArgumentNullException.ThrowIfNull(image);
        ID2D1DeviceContext context = Context;
        if (image is not TiledGpuImage tiled || !tiled.IsDrawable(_surface.Resources.Generation))
        {
            SkippedImageDraws++;
            return;
        }

        RectD bounds = new(0, 0, tiled.PixelWidth, tiled.PixelHeight);
        RectD source = DrawGeometry.Intersect(sourcePixels ?? bounds, bounds);
        if (DrawGeometry.IsEmpty(source) || DrawGeometry.IsEmpty(destination))
        {
            return;
        }

        if (sourcePixels is { } requested && source != requested)
        {
            // Phần nguồn yêu cầu nằm ngoài ảnh: chỉ vẽ phần giao, ở đúng chỗ của nó trong đích.
            destination = DrawGeometry.MapToDestination(requested, destination, source);
        }

        D2dInterpolationMode mode = InterpolationModes.ToD2D(interpolation);
        float dpi = (float)_surface.Resources.DpiScale;
        Matrix3x2 device = _transform * Matrix3x2.CreateScale(dpi);
        if (DrawGeometry.TrySnapPixelExact(device, destination, source, out RectD snapped))
        {
            // 1 pixel nguồn = 1 pixel thiết bị: sao chép đúng từng byte (G-PIX "giống hệt ở 100 %").
            destination = snapped;
            mode = D2dInterpolationMode.NearestNeighbor;
        }

        float alpha = Math.Clamp(opacity, 0f, 1f) * _currentOpacity;
        GpuTile[] tiles = tiled.Tiles;
        if (tiles.Length == 1)
        {
            DrawTile(context, tiles[0], source, destination, mode, alpha, clipToCore: false);
            return;
        }

        foreach (GpuTile tile in tiles)
        {
            DrawTile(context, tile, source, destination, mode, alpha, clipToCore: true);
        }
    }

    /// <summary>
    /// Vẽ ảnh THÔ (chưa xoay) sao cho trong <paramref name="destination"/> nó hiện như ảnh đã áp orientation EXIF
    /// <paramref name="exifOrientation"/> (1..8). <paramref name="sourcePixels"/> tính trên ảnh thô.
    /// </summary>
    public void DrawImageOriented(IGpuImage image, RectD destination, int exifOrientation, RectD? sourcePixels,
        ImageInterpolation interpolation, float opacity = 1f)
    {
        PushTransform(ImageTransforms.ForExifOrientation(exifOrientation, destination));
        try
        {
            DrawImage(image, ImageTransforms.UnorientedRect(exifOrientation, destination), sourcePixels, interpolation, opacity);
        }
        finally
        {
            PopTransform();
        }
    }

    public void FillRectangle(RectD rect, ColorF color)
    {
        D2dRectF r = ToD2D(rect);
        Context.FillRectangle(&r, PrepareBrush(color));
    }

    public void FillRoundedRectangle(RectD rect, double radius, ColorF color)
    {
        D2dRoundedRect rounded = new() { Rect = ToD2D(rect), RadiusX = (float)radius, RadiusY = (float)radius };
        D2D1RawCalls.FillRoundedRectangle(Context, &rounded, PrepareBrush(color));
    }

    public void DrawRectangle(RectD rect, ColorF color, double strokeWidth)
    {
        D2dRectF r = ToD2D(rect);
        Context.DrawRectangle(&r, PrepareBrush(color), (float)strokeWidth, 0);
    }

    /// <summary>Layout phải do renderer chữ của shell tạo (<see cref="INativeTextLayout"/>, WP-17).</summary>
    public void DrawText(ITextLayout layout, PointD origin, ColorF color)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (layout is not INativeTextLayout native)
        {
            throw new ArgumentException("The text layout was not created by the Direct2D text renderer.", nameof(layout));
        }

        Context.DrawTextLayout(new D2dPointF((float)origin.X, (float)origin.Y), native.NativeLayout, PrepareBrush(color),
            D2dDrawTextOptions.None);
    }

    public void PushClip(RectD rect)
    {
        D2dRectF r = ToD2D(rect);
        Context.PushAxisAlignedClip(&r, D2dAntialiasMode.Aliased);
        _clipDepth++;
    }

    public void PopClip()
    {
        if (_clipDepth == 0)
        {
            throw new InvalidOperationException("PopClip without PushClip.");
        }

        Context.PopAxisAlignedClip();
        _clipDepth--;
    }

    public void PushOpacity(float opacity)
    {
        _ = Context;
        _opacity.Push(_currentOpacity);
        _currentOpacity *= Math.Clamp(opacity, 0f, 1f);
    }

    public void PopOpacity()
    {
        if (_opacity.Count == 0)
        {
            throw new InvalidOperationException("PopOpacity without PushOpacity.");
        }

        _currentOpacity = _opacity.Pop();
    }

    /// <summary>Nhân <paramref name="transform"/> (hàng-vector, áp TRƯỚC ma trận hiện tại) vào ma trận vẽ.</summary>
    public void PushTransform(Matrix3x2 transform)
    {
        _transforms.Push(_transform);
        SetTransform(transform * _transform);
    }

    public void PopTransform()
    {
        if (_transforms.Count == 0)
        {
            throw new InvalidOperationException("PopTransform without PushTransform.");
        }

        SetTransform(_transforms.Pop());
    }

    /// <summary>Đọc lại toàn bộ đích (Pbgra32, kích thước pixel của bề mặt). Chỉ offscreen; cửa sổ trả false.</summary>
    public bool TryReadPixels(out PixelBuffer pixels)
    {
        ID2D1DeviceContext context = Context;
        DeviceResources resources = _surface.Resources;
        if (resources.HasSwapChain)
        {
            pixels = null!;
            return false;
        }

        ComInterop.Check(context.Flush(null, null));
        int width = resources.PixelWidth;
        int height = resources.PixelHeight;

        // Bitmap CPU_READ | CANNOT_DRAW = texture staging của D3D11: CopyFromBitmap (GPU) rồi Map (CPU).
        ID2D1Bitmap1 staging = resources.CreateBitmap(width, height, null, 0, D2dAlphaMode.Premultiplied,
            D2dBitmapOptions.CpuRead | D2dBitmapOptions.CannotDraw);
        PixelBuffer result = PixelBuffer.Allocate(width, height, PixelLayout.Pbgra32);
        try
        {
            ComInterop.Check(staging.CopyFromBitmap(null, resources.Target, null));
            D2dMappedRect mapped;
            ComInterop.Check(staging.Map(D2dMapOptions.Read, &mapped));
            try
            {
                for (int y = 0; y < height; y++)
                {
                    new ReadOnlySpan<byte>(mapped.Bits + ((long)y * mapped.Pitch), width * 4).CopyTo(result.GetRow(y));
                }
            }
            finally
            {
                ComInterop.Check(staging.Unmap());
            }
        }
        catch
        {
            result.Dispose();
            throw;
        }
        finally
        {
            resources.ReleaseBitmap(staging);
        }

        pixels = result;
        return true;
    }

    internal void Begin()
    {
        _active = true;
        _opacity.Clear();
        _transforms.Clear();
        _currentOpacity = 1f;
        _clipDepth = 0;
        SetTransform(Matrix3x2.Identity);
    }

    /// <summary>Kết thúc khung: gỡ clip còn treo (EndDraw của D2D lỗi nếu không cân). Trả false nếu stack không cân.</summary>
    internal bool End()
    {
        bool balanced = _clipDepth == 0 && _opacity.Count == 0 && _transforms.Count == 0;
        ID2D1DeviceContext context = _surface.Resources.Context;
        while (_clipDepth > 0)
        {
            context.PopAxisAlignedClip();
            _clipDepth--;
        }

        _active = false;
        return balanced;
    }

    private static void DrawTile(ID2D1DeviceContext context, GpuTile tile, RectD source, RectD destination,
        D2dInterpolationMode mode, float alpha, bool clipToCore)
    {
        if (tile.Bitmap is null)
        {
            return;
        }

        RectD texture = DrawGeometry.ToRectD(tile.Spec.Texture);
        RectD core = DrawGeometry.Intersect(DrawGeometry.ToRectD(tile.Spec.Core), source);
        if (DrawGeometry.IsEmpty(core))
        {
            return;
        }

        // Vẽ cả viền chồng (gutter) của tile để nội suy ở mép dùng pixel thật của tile bên cạnh, rồi cắt về đúng phần core:
        // mỗi pixel thiết bị thuộc đúng một tile (clip aliased theo tâm pixel) -> không đường nối, không vẽ chồng.
        RectD region = DrawGeometry.Intersect(texture, source);
        RectD dest = DrawGeometry.MapToDestination(source, destination, region);
        D2dRectF destRect = ToD2D(dest);
        D2dRectF sourceRect = new(
            (float)(region.X - texture.X),
            (float)(region.Y - texture.Y),
            (float)(region.Right - texture.X),
            (float)(region.Bottom - texture.Y));
        if (clipToCore)
        {
            D2dRectF clip = ToD2D(DrawGeometry.MapToDestination(source, destination, core));
            context.PushAxisAlignedClip(&clip, D2dAntialiasMode.Aliased);
            context.DrawBitmapEx(tile.Bitmap, &destRect, alpha, mode, &sourceRect, 0);
            context.PopAxisAlignedClip();
        }
        else
        {
            context.DrawBitmapEx(tile.Bitmap, &destRect, alpha, mode, &sourceRect, 0);
        }
    }

    private static D2dRectF ToD2D(RectD rect) =>
        new((float)rect.X, (float)rect.Y, (float)rect.Right, (float)rect.Bottom);

    private static D2dColorF ToD2D(ColorF color, float opacity) => new(color.R, color.G, color.B, color.A * opacity);

    private ID2D1SolidColorBrush PrepareBrush(ColorF color)
    {
        ID2D1SolidColorBrush brush = _surface.Resources.Brush;
        D2dColorF c = ToD2D(color, 1f);
        brush.SetColor(&c);
        brush.SetOpacity(_currentOpacity);
        return brush;
    }

    private void SetTransform(Matrix3x2 transform)
    {
        _transform = transform;
        D2dMatrix3x2F m = new()
        {
            M11 = transform.M11,
            M12 = transform.M12,
            M21 = transform.M21,
            M22 = transform.M22,
            Dx = transform.M31,
            Dy = transform.M32,
        };
        _surface.Resources.Context.SetTransform(&m);
    }
}

/// <summary>Layout DirectWrite mà <see cref="D2DDrawContext.DrawText"/> vẽ được (renderer chữ WP-17 implement).</summary>
internal interface INativeTextLayout
{
    IDWriteTextLayout NativeLayout { get; }
}

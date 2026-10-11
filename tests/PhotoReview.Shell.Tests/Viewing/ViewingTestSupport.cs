using PhotoReview.App.Input;
using PhotoReview.Imaging.Pixels;
using PhotoReview.Shell.Rendering;
using PhotoReview.Shell.Tests.Rendering;

namespace PhotoReview.Shell.Tests.Viewing;

/// <summary>Backend GPU giả cho <see cref="GpuImageCache"/>: không cần thiết bị, đếm upload/giải phóng và dải prefetch.</summary>
internal sealed class FakeGpuBackend : IGpuImageBackend
{
    private EventHandler? _deviceRecreated;

    public event EventHandler? DeviceRecreated
    {
        add => _deviceRecreated += value;
        remove => _deviceRecreated -= value;
    }

    public List<(long Id, bool WithData)> Uploads { get; } = [];

    public List<(int Start, int End)> RowUploads { get; } = [];

    public List<long> Released { get; } = [];

    public long Generation { get; private set; }

    public TiledGpuImage Upload(PixelBuffer pixels, bool withData)
    {
        Uploads.Add((pixels.Id, withData));
        var tiles = TileLayout.Compute(pixels.Width, pixels.Height, 16384, TileLayout.DefaultGutter)
            .Select(spec => new GpuTile(spec, null)).ToArray();
        return new TiledGpuImage(pixels, tiles, Generation);
    }

    public void UploadRows(TiledGpuImage image, PixelBuffer pixels, int startRow, int endRow) => RowUploads.Add((startRow, endRow));

    public void Release(TiledGpuImage image) => Released.Add(image.PixelBufferId);

    public void RaiseDeviceRecreated()
    {
        Generation++;
        _deviceRecreated?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>Ghi lại lệnh vẽ ảnh/hình (thứ tự, hộp đích, nội suy, opacity) để test thứ tự lớp và hình học.</summary>
internal sealed class RecordingImageContext : IDrawContext
{
    public List<string> Calls { get; } = [];

    public List<(long BufferId, RectD Destination, ImageInterpolation Interpolation, float Opacity)> Images { get; } = [];

    public List<(RectD Rect, ColorF Color)> Fills { get; } = [];

    public int ClipDepth { get; private set; }

    public List<RectD> Clips { get; } = [];

    public void Clear(ColorF color) => Calls.Add("Clear");

    public void DrawImage(IGpuImage image, RectD destination, RectD? sourcePixels, ImageInterpolation interpolation, float opacity = 1f)
    {
        Calls.Add("DrawImage");
        Images.Add((image.PixelBufferId, destination, interpolation, opacity));
    }

    public void FillRectangle(RectD rect, ColorF color)
    {
        Calls.Add("FillRectangle");
        Fills.Add((rect, color));
    }

    public void FillRoundedRectangle(RectD rect, double radius, ColorF color) => Calls.Add("FillRoundedRectangle");

    public void DrawRectangle(RectD rect, ColorF color, double strokeWidth) => Calls.Add("DrawRectangle");

    public void DrawText(ITextLayout layout, PointD origin, ColorF color) => Calls.Add("DrawText");

    public void PushClip(RectD rect)
    {
        Calls.Add("PushClip");
        Clips.Add(rect);
        ClipDepth++;
    }

    public void PopClip()
    {
        Calls.Add("PopClip");
        ClipDepth--;
    }

    public void PushOpacity(float opacity) => Calls.Add("PushOpacity");

    public void PopOpacity() => Calls.Add("PopOpacity");

    public bool TryReadPixels(out PixelBuffer pixels)
    {
        pixels = null!;
        return false;
    }
}

internal static class ViewingImages
{
    public static PixelBuffer Solid(int width, int height, byte b, byte g, byte r) =>
        RenderTestImages.CreateSolid(width, height, b, g, r);
}

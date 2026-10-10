using PhotoReview.Imaging.Pixels;
using PhotoReview.Shell.Interop.Graphics;

namespace PhotoReview.Shell.Rendering;

/// <summary>
/// WP-15 (C-10): ảnh trên GPU = một hoặc nhiều tile <c>ID2D1Bitmap1</c> (ảnh vượt giới hạn texture được chia theo
/// <see cref="TileLayout"/>). Khoá theo <see cref="PixelBuffer.Id"/> (INV-1). Chỉ cache tạo/giải phóng; người vẽ
/// kiểm <see cref="IsDrawable"/> (ảnh đã bị evict hoặc thuộc thiết bị cũ thì bỏ qua thay vì vẽ con trỏ chết).
/// </summary>
internal sealed class TiledGpuImage : IGpuImage
{
    internal TiledGpuImage(PixelBuffer pixels, GpuTile[] tiles, long generation)
    {
        PixelBufferId = pixels.Id;
        PixelWidth = pixels.Width;
        PixelHeight = pixels.Height;
        Layout = pixels.Layout;
        Tiles = tiles;
        Generation = generation;
        long bytes = 0;
        foreach (GpuTile tile in tiles)
        {
            bytes += (long)tile.Spec.Texture.Width * tile.Spec.Texture.Height * 4;
        }

        GpuBytes = bytes;
    }

    public long PixelBufferId { get; }

    public int PixelWidth { get; }

    public int PixelHeight { get; }

    /// <summary>Tổng byte của các texture (gồm viền chồng của tile).</summary>
    public long GpuBytes { get; }

    public PixelLayout Layout { get; }

    internal GpuTile[] Tiles { get; }

    /// <summary>Thế hệ thiết bị đã tạo các tile (<see cref="DeviceResources.Generation"/>).</summary>
    internal long Generation { get; }

    /// <summary>Số dòng đầu tiên đã có trên GPU (upload từng dải khi prefetch).</summary>
    internal int UploadedRows { get; set; }

    internal bool IsComplete => UploadedRows >= PixelHeight;

    internal bool IsReleased { get; set; }

    internal bool IsDrawable(long currentGeneration) => !IsReleased && IsComplete && Generation == currentGeneration;
}

/// <summary>Một tile: vị trí trong ảnh + bitmap D2D (null với backend giả của unit test).</summary>
internal sealed class GpuTile(TileSpec spec, ID2D1Bitmap1? bitmap)
{
    public TileSpec Spec { get; } = spec;

    public ID2D1Bitmap1? Bitmap { get; set; } = bitmap;
}

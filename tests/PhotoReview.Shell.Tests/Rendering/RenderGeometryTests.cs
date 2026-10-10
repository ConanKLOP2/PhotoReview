using System.Numerics;
using PhotoReview.App.Input;
using PhotoReview.Core.Model;
using PhotoReview.Imaging.Pixels;
using PhotoReview.Shell.Rendering;

namespace PhotoReview.Shell.Tests.Rendering;

/// <summary>WP-15: phần thuần của renderer (tile, ánh xạ nguồn-đích, bắt 1:1, ma trận orientation) - không cần GPU.</summary>
public sealed class RenderGeometryTests
{
    [Theory]
    [InlineData(100, 50, 16384, 1, 1)]
    [InlineData(16384, 16384, 16384, 1, 1)]
    [InlineData(30000, 2000, 16384, 2, 1)]
    [InlineData(2000, 40000, 16384, 1, 3)]
    [InlineData(1000, 1000, 256, 6, 6)]
    [InlineData(1000, 300, 256, 6, 2)]
    public void TileLayout_CoresPartitionImage_TexturesWithinLimitAndGutter(int width, int height, int max, int columns, int rows)
    {
        const int Gutter = 32;
        TileSpec[] tiles = TileLayout.Compute(width, height, max, Gutter);

        Assert.Equal(columns * rows, tiles.Length);
        var covered = new int[width + 1];
        long area = 0;
        foreach (TileSpec tile in tiles)
        {
            Assert.InRange(tile.Texture.Width, 1, max);
            Assert.InRange(tile.Texture.Height, 1, max);
            Assert.True(tile.Texture.X <= tile.Core.X && tile.Texture.Right >= tile.Core.Right, $"{tile}");
            Assert.True(tile.Texture.Y <= tile.Core.Y && tile.Texture.Bottom >= tile.Core.Bottom, $"{tile}");
            Assert.True(tile.Texture.X >= 0 && tile.Texture.Right <= width && tile.Texture.Y >= 0 && tile.Texture.Bottom <= height);

            // Viền chồng: mép core nằm trong ảnh thì texture vượt nó đúng Gutter (hoặc tới mép ảnh).
            Assert.Equal(Math.Max(0, tile.Core.X - Gutter), tile.Texture.X);
            Assert.Equal(Math.Min(width, tile.Core.Right + Gutter), tile.Texture.Right);
            Assert.Equal(Math.Max(0, tile.Core.Y - Gutter), tile.Texture.Y);
            Assert.Equal(Math.Min(height, tile.Core.Bottom + Gutter), tile.Texture.Bottom);
            area += (long)tile.Core.Width * tile.Core.Height;
        }

        // Core phủ kín và không chồng: tổng diện tích = ảnh, và mỗi cột core liên tiếp nhau.
        Assert.Equal((long)width * height, area);
        int[] starts = tiles.Select(t => t.Core.X).Distinct().Order().ToArray();
        int[] ends = tiles.Select(t => t.Core.Right).Distinct().Order().ToArray();
        Assert.Equal(0, starts[0]);
        Assert.Equal(width, ends[^1]);
        Assert.Equal(starts.Skip(1), ends.SkipLast(1));
    }

    [Fact]
    public void TileLayout_SingleTile_WhenImageFits_HasNoGutter()
    {
        TileSpec tile = Assert.Single(TileLayout.Compute(6000, 4000, 16384, 64));

        Assert.Equal(new PixelRect(0, 0, 6000, 4000), tile.Texture);
        Assert.Equal(tile.Texture, tile.Core);
    }

    [Fact]
    public void TileLayout_RejectsInvalidSizes()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TileLayout.Compute(0, 1, 16384, 64));
        Assert.Throws<ArgumentOutOfRangeException>(() => TileLayout.Compute(1, 0, 16384, 64));
        Assert.Throws<ArgumentOutOfRangeException>(() => TileLayout.Compute(1, 1, 2, 64));
    }

    [Fact]
    public void MapToDestination_ScalesAndOffsetsPart()
    {
        RectD mapped = DrawGeometry.MapToDestination(new RectD(10, 20, 100, 50), new RectD(0, 0, 50, 100), new RectD(30, 30, 20, 10));

        Assert.Equal(new RectD(10, 20, 10, 20), mapped);
    }

    [Fact]
    public void Intersect_ReturnsOverlapOrEmpty()
    {
        Assert.Equal(new RectD(5, 5, 5, 5), DrawGeometry.Intersect(new RectD(0, 0, 10, 10), new RectD(5, 5, 10, 10)));
        Assert.True(DrawGeometry.IsEmpty(DrawGeometry.Intersect(new RectD(0, 0, 10, 10), new RectD(10, 0, 5, 5))));
        Assert.False(DrawGeometry.IsEmpty(new RectD(0, 0, 1, 1)));
    }

    [Fact]
    public void TrySnapPixelExact_OneToOne_SnapsCornerToDevicePixel()
    {
        Assert.True(DrawGeometry.TrySnapPixelExact(Matrix3x2.Identity, new RectD(10.3, 4.6, 100, 50), new RectD(0, 0, 100, 50), out RectD snapped));

        Assert.Equal(10, snapped.X, 9);
        Assert.Equal(5, snapped.Y, 9);
        Assert.Equal((100.0, 50.0), (snapped.Width, snapped.Height));
    }

    [Fact]
    public void TrySnapPixelExact_HighDpi_UsesDevicePixels()
    {
        // DPI 1.5: 100 px nguồn = 66,67 DIP; góc 10,1 DIP = 15,15 px -> dời về 15 px = 10 DIP.
        Matrix3x2 device = Matrix3x2.CreateScale(1.5f);
        Assert.True(DrawGeometry.TrySnapPixelExact(device, new RectD(10.1, 0, 100 / 1.5, 50 / 1.5), new RectD(0, 0, 100, 50), out RectD snapped));

        Assert.Equal(10, snapped.X, 4);
        Assert.Equal(0, snapped.Y, 4);
    }

    [Fact]
    public void TrySnapPixelExact_Rotated90_IsExactAndSnapsThroughInverse()
    {
        Matrix3x2 rotate = new(0, 1, -1, 0, 200.4f, 0);   // xoay 90 + tịnh tiến lẻ
        Assert.True(DrawGeometry.TrySnapPixelExact(rotate, new RectD(0, 0, 40, 30), new RectD(0, 0, 40, 30), out RectD snapped));

        Vector2 corner = Vector2.Transform(new Vector2((float)snapped.X, (float)snapped.Y), rotate);
        Assert.Equal(Math.Round(corner.X), corner.X, 3);
        Assert.Equal(Math.Round(corner.Y), corner.Y, 3);
    }

    [Theory]
    [InlineData(101, 50)]
    [InlineData(100, 49)]
    public void TrySnapPixelExact_ScaledDraw_IsNotExact(double width, double height)
    {
        Assert.False(DrawGeometry.TrySnapPixelExact(Matrix3x2.Identity, new RectD(0, 0, width, height), new RectD(0, 0, 100, 50), out RectD snapped));
        Assert.Equal(new RectD(0, 0, width, height), snapped);
    }

    [Fact]
    public void TrySnapPixelExact_Rotated45_IsNotExact()
    {
        Matrix3x2 rotate = Matrix3x2.CreateRotation(MathF.PI / 4);

        Assert.False(DrawGeometry.TrySnapPixelExact(rotate, new RectD(0, 0, 10, 10), new RectD(0, 0, 10, 10), out _));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void ExifOrientationMatrix_MapsPixelCentersLikePixelOps(int orientation)
    {
        const int W = 5;
        const int H = 3;
        using PixelBuffer raw = PixelBuffer.Allocate(W, H, PixelLayout.Pbgra32);
        for (int y = 0; y < H; y++)
        {
            Span<byte> row = raw.GetRow(y);
            for (int x = 0; x < W; x++)
            {
                row[x * 4] = (byte)((y * W) + x);
            }
        }

        using PixelBuffer copy = PixelBuffer.Allocate(W, H, PixelLayout.Pbgra32);
        raw.TryGetSpan(out Span<byte> rawSpan);
        copy.TryGetSpan(out Span<byte> copySpan);
        rawSpan.CopyTo(copySpan);
        using PixelBuffer oriented = PixelOps.ApplyOrientation(copy, orientation);

        // Đích (đã xoay) đặt lệch gốc để kiểm cả phần tịnh tiến.
        RectD destination = new(100, 200, oriented.Width, oriented.Height);
        Matrix3x2 m = ImageTransforms.ForExifOrientation(orientation, destination);
        RectD rawRect = ImageTransforms.UnorientedRect(orientation, destination);
        Assert.Equal((W, H), ((int)rawRect.Width, (int)rawRect.Height));
        for (int v = 0; v < H; v++)
        {
            for (int u = 0; u < W; u++)
            {
                Vector2 p = Vector2.Transform(new Vector2(u + 0.5f, v + 0.5f), m);
                int x = (int)Math.Floor(p.X - 100);
                int y = (int)Math.Floor(p.Y - 200);
                Assert.Equal((byte)((v * W) + u), oriented.GetRow(y)[x * 4]);
            }
        }
    }

    [Fact]
    public void ImageInterpolations_FromScalingQuality_MatchesWpfConverter()
    {
        Assert.Equal(ImageInterpolation.Linear, ImageInterpolations.FromScalingQuality(ScalingQuality.Linear));
        Assert.Equal(ImageInterpolation.HighQualityCubic, ImageInterpolations.FromScalingQuality(ScalingQuality.HighQuality));
    }

    [Fact]
    public void RenderColors_DarkCanvas_Is101010()
    {
        Assert.Equal(new ColorF(16 / 255f, 16 / 255f, 16 / 255f, 1f), RenderColors.DarkCanvas);
    }
}

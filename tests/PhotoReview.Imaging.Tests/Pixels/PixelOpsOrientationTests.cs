using PhotoReview.Imaging.Pixels;
using PhotoReview.TestSupport.Windows;

namespace PhotoReview.Imaging.Tests.Pixels;

/// <summary>
/// WP-02 / C-03: <see cref="PixelOps.ApplyOrientation"/> giống từng byte đường WPF hiện tại (<see cref="ExifOrientation.Apply"/>,
/// tức TransformedBitmap) trên 8 orientation, cả ảnh lẻ 1x1/1xN/Nx1 và kích thước cắt ngang ô chuyển vị 32x32; quyền sở hữu src;
/// và tính chất "áp orientation rồi nghịch đảo = gốc".
/// </summary>
public sealed class PixelOpsOrientationTests
{
    public static TheoryData<int, int, int, PixelLayout> WpfParityCases()
    {
        var data = new TheoryData<int, int, int, PixelLayout>();
        (int W, int H)[] sizes = [(1, 1), (1, 7), (7, 1), (2, 3), (33, 31), (70, 45)];
        foreach (var layout in new[] { PixelLayout.Bgr32, PixelLayout.Pbgra32 })
            foreach (var (w, h) in sizes)
                for (var orientation = 1; orientation <= 8; orientation++) data.Add(orientation, w, h, layout);
        return data;
    }

    [Theory]
    [MemberData(nameof(WpfParityCases))]
    public void ApplyOrientation_MatchesWpfTransformedBitmap_ByteForByte(int orientation, int width, int height, PixelLayout layout)
    {
        using var original = PixelAssert.CreatePattern(width, height, layout, seed: (orientation * 1000) + width + height);
        var reference = WpfExifOrientation.Apply(PixelAssert.ToBitmapSource(original), orientation);

        using var actual = PixelOps.ApplyOrientation(PixelAssert.Clone(original), orientation);

        var transposed = orientation >= 5;
        Assert.Equal(transposed ? height : width, actual.Width);
        Assert.Equal(transposed ? width : height, actual.Height);
        Assert.Equal(layout, actual.Layout);
        PixelAssert.Equal(reference, actual);
    }

    [Theory]
    [InlineData(2, 0, 0, 2, 0)] // gương ngang: pixel (0,0) tới (W-1, 0)
    [InlineData(3, 0, 0, 2, 1)] // xoay 180
    [InlineData(4, 0, 0, 0, 1)] // gương dọc
    [InlineData(5, 2, 0, 0, 2)] // chuyển vị: (x,y) -> (y,x)
    [InlineData(6, 0, 0, 1, 0)] // xoay 90 CW: góc trên-trái tới góc trên-phải (đích rộng H = 2)
    [InlineData(7, 0, 0, 1, 2)] // transverse
    [InlineData(8, 0, 0, 0, 2)] // xoay 270 CW: góc trên-trái tới góc dưới-trái
    public void ApplyOrientation_MovesKnownPixel(int orientation, int srcX, int srcY, int dstX, int dstY)
    {
        // Nguồn 3 x 2, mỗi pixel mang chỉ số của nó (đọc được bằng mắt khi test đỏ).
        var src = PixelBuffer.Allocate(3, 2, PixelLayout.Bgr32);
        for (var y = 0; y < 2; y++)
            for (var x = 0; x < 3; x++) src.GetRow(y).Slice(x * 4, 4).Fill((byte)(10 + (y * 3) + x));

        using var dst = PixelOps.ApplyOrientation(src, orientation);

        Assert.Equal((byte)(10 + (srcY * 3) + srcX), dst.GetRow(dstY)[dstX * 4]);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(8)]
    public void ApplyOrientation_NonIdentity_ReturnsNewBuffer_AndDisposesSource(int orientation)
    {
        var src = PixelAssert.CreatePattern(4, 3, PixelLayout.Pbgra32, seed: orientation);

        using var dst = PixelOps.ApplyOrientation(src, orientation);

        Assert.NotSame(src, dst);
        Assert.True(src.IsDisposed);
        Assert.False(dst.IsDisposed);
        Assert.NotEqual(src.Id, dst.Id);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(9)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void ApplyOrientation_IdentityOrInvalid_ReturnsSourceUntouched(int orientation)
    {
        using var src = PixelAssert.CreatePattern(4, 3, PixelLayout.Bgr32, seed: 5);
        using var copy = PixelAssert.Clone(src);

        var result = PixelOps.ApplyOrientation(src, orientation);

        Assert.Same(src, result);
        Assert.False(src.IsDisposed);
        PixelAssert.Equal(copy, result);
    }

    [Fact]
    public void ApplyOrientation_NullOrDisposed_Throws()
    {
        var disposed = PixelBuffer.Allocate(2, 2, PixelLayout.Bgr32);
        disposed.Dispose();

        Assert.Throws<ArgumentNullException>(() => PixelOps.ApplyOrientation(null!, 6));
        Assert.Throws<ObjectDisposedException>(() => PixelOps.ApplyOrientation(disposed, 6));
        Assert.Throws<ObjectDisposedException>(() => PixelOps.ApplyOrientation(disposed, 1));
    }

    [Fact]
    public void ApplyOrientation_ThenInverse_IsIdentity_Property()
    {
        // Nghịch đảo: 6 <-> 8, các giá trị khác tự nghịch đảo (gương/180/chuyển vị).
        static int Inverse(int o) => o switch { 6 => 8, 8 => 6, _ => o };

        PropertyRunner.Check("ApplyOrientation(o) then ApplyOrientation(inverse o) == original", iterations: 40, (rng, _) =>
        {
            var width = rng.Next(1, 80);
            var height = rng.Next(1, 80);
            var layout = rng.Next(2) == 0 ? PixelLayout.Bgr32 : PixelLayout.Pbgra32;
            var orientation = rng.Next(1, 9);
            using var original = PixelAssert.CreatePattern(width, height, layout, rng.Next());

            using var roundTrip = PixelOps.ApplyOrientation(PixelOps.ApplyOrientation(PixelAssert.Clone(original), orientation), Inverse(orientation));

            Assert.NotSame(original, roundTrip);
            PixelAssert.Equal(original, roundTrip);
        });
    }

    [Fact]
    public void ApplyOrientation_ComposesLikeTheDihedralGroup_Property()
    {
        // 6 (90 CW) bốn lần = gốc; 6 rồi 6 = 3 (180).
        PropertyRunner.Check("rotate90 composes", iterations: 10, (rng, _) =>
        {
            using var original = PixelAssert.CreatePattern(rng.Next(1, 50), rng.Next(1, 50), PixelLayout.Pbgra32, rng.Next());

            using var twice = PixelOps.ApplyOrientation(PixelOps.ApplyOrientation(PixelAssert.Clone(original), 6), 6);
            using var half = PixelOps.ApplyOrientation(PixelAssert.Clone(original), 3);
            Assert.Equal((half.Width, half.Height), (twice.Width, twice.Height));
            PixelAssert.Equal(half, twice);

            using var full = PixelOps.ApplyOrientation(PixelOps.ApplyOrientation(PixelAssert.Clone(twice), 6), 6);
            PixelAssert.Equal(original, full);
        });
    }
}

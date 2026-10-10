using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoReview.Imaging.Pixels;
using PhotoReview.TestSupport.Windows;

namespace PhotoReview.Imaging.Tests.Pixels;

/// <summary>
/// WP-02 / C-03: <see cref="PixelOps.IsFullyOpaque"/> / <see cref="PixelOps.HasAlpha"/> khớp quyết định cache hiện tại
/// (<c>PreviewCacheFile.HasAlpha/IsFullyOpaque(BitmapSource)</c>) trên 6 định dạng nguồn. Ánh xạ sang PixelBuffer theo cách
/// WP-03/04 sẽ làm: nguồn có thể mang alpha -&gt; Pbgra32, còn lại -&gt; Bgr32. Lưu ý ngữ nghĩa: <c>PreviewCacheFile.HasAlpha</c>
/// hỏi "định dạng có thể mang alpha", <see cref="PixelOps.HasAlpha"/> hỏi "có pixel trong suốt thật" = !IsFullyOpaque của bản cũ.
/// </summary>
public sealed class PixelOpsAlphaTests
{
    public static TheoryData<string, bool> FormatCases() => new()
    {
        { "Bgr24", false }, { "Bgr32", false }, { "Gray8", false },
        { "Bgra32", false }, { "Bgra32", true },
        { "Pbgra32", false }, { "Pbgra32", true },
        { "Indexed8", false }, { "Indexed8", true },
    };

    [Theory]
    [MemberData(nameof(FormatCases))]
    public void AlphaChecks_MatchPreviewCacheFile(string formatName, bool withTransparentPixel)
    {
        var source = CreateSource(formatName, withTransparentPixel);
        var layout = PreviewCacheFile.HasAlpha(source) ? PixelLayout.Pbgra32 : PixelLayout.Bgr32;
        using var pixels = PixelAssert.FromBitmapSource(source, layout);

        var legacyOpaque = PreviewCacheFile.IsFullyOpaque(source);

        Assert.Equal(legacyOpaque, PixelOps.IsFullyOpaque(pixels));
        Assert.Equal(!legacyOpaque, PixelOps.HasAlpha(pixels));
        // Chặn ca rỗng: nguồn "có pixel trong suốt" phải thật sự bị coi là không đục.
        Assert.Equal(withTransparentPixel, !legacyOpaque);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(17, 3)] // 51 pixel: đuôi không vừa vector
    [InlineData(64, 2)] // vừa khít vector
    [InlineData(3, 9)]
    public void IsFullyOpaque_FindsOneTransparentPixelAnywhere(int width, int height)
    {
        using var px = PixelBuffer.Allocate(width, height, PixelLayout.Pbgra32);
        for (var y = 0; y < height; y++) px.GetRow(y).Fill(255);
        Assert.True(PixelOps.IsFullyOpaque(px));
        Assert.False(PixelOps.HasAlpha(px));

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                px.GetRow(y)[(x * 4) + 3] = 254;
                Assert.False(PixelOps.IsFullyOpaque(px), $"pixel ({x}, {y})");
                Assert.True(PixelOps.HasAlpha(px), $"pixel ({x}, {y})");
                px.GetRow(y)[(x * 4) + 3] = 255;
            }
        }
    }

    [Fact]
    public void IsFullyOpaque_IgnoresColourBytes()
    {
        using var px = PixelBuffer.Allocate(20, 2, PixelLayout.Pbgra32);
        for (var y = 0; y < 2; y++)
        {
            var row = px.GetRow(y);
            for (var i = 0; i < row.Length; i++) row[i] = i % 4 == 3 ? (byte)255 : (byte)0;
        }

        Assert.True(PixelOps.IsFullyOpaque(px));
    }

    [Fact]
    public void Bgr32_IsAlwaysOpaque_WhateverTheUnusedByte()
    {
        using var px = PixelBuffer.Allocate(9, 2, PixelLayout.Bgr32);
        px.GetRow(0).Clear();
        px.GetRow(1).Clear();

        Assert.True(PixelOps.IsFullyOpaque(px));
        Assert.False(PixelOps.HasAlpha(px));
    }

    [Fact]
    public void NullOrDisposed_Throws()
    {
        var disposed = PixelBuffer.Allocate(2, 2, PixelLayout.Bgr32);
        disposed.Dispose();

        Assert.Throws<ArgumentNullException>(() => PixelOps.IsFullyOpaque(null!));
        Assert.Throws<ArgumentNullException>(() => PixelOps.HasAlpha(null!));
        Assert.Throws<ObjectDisposedException>(() => PixelOps.IsFullyOpaque(disposed));
        Assert.Throws<ObjectDisposedException>(() => PixelOps.HasAlpha(disposed));
    }

    // Ảnh 21 x 5 (đuôi vector), một pixel bán trong suốt ở giữa dòng cuối khi được yêu cầu.
    private static BitmapSource CreateSource(string formatName, bool withTransparentPixel)
    {
        const int Width = 21;
        const int Height = 5;
        var rng = new Random(formatName.Length + (withTransparentPixel ? 100 : 0));
        BitmapSource bitmap;
        switch (formatName)
        {
            case "Indexed8":
            {
                var colors = Enumerable.Range(0, 16).Select(i => Color.FromArgb(255, (byte)(i * 16), (byte)(255 - (i * 16)), (byte)i)).ToList();
                if (withTransparentPixel) colors[7] = Color.FromArgb(128, 64, 64, 64);
                var indices = new byte[Width * Height];
                for (var i = 0; i < indices.Length; i++) indices[i] = (byte)rng.Next(16);
                if (withTransparentPixel) indices[^3] = 7;
                else for (var i = 0; i < indices.Length; i++) if (indices[i] == 7) indices[i] = 6;
                bitmap = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Indexed8, new BitmapPalette(colors), indices, Width);
                break;
            }
            default:
            {
                var format = formatName switch
                {
                    "Bgr24" => PixelFormats.Bgr24,
                    "Bgr32" => PixelFormats.Bgr32,
                    "Gray8" => PixelFormats.Gray8,
                    "Bgra32" => PixelFormats.Bgra32,
                    "Pbgra32" => PixelFormats.Pbgra32,
                    _ => throw new ArgumentOutOfRangeException(nameof(formatName)),
                };
                var bytesPerPixel = (format.BitsPerPixel + 7) / 8;
                var stride = Width * bytesPerPixel;
                var data = new byte[stride * Height];
                rng.NextBytes(data);
                if (bytesPerPixel == 4 && format != PixelFormats.Bgr32)
                {
                    for (var i = 3; i < data.Length; i += 4) data[i] = 255;
                    if (withTransparentPixel)
                    {
                        var at = ((Height - 1) * stride) + (10 * 4);
                        data[at] = data[at + 1] = data[at + 2] = 40;
                        data[at + 3] = 100;
                    }
                }

                bitmap = BitmapSource.Create(Width, Height, 96, 96, format, null, data, stride);
                break;
            }
        }

        bitmap.Freeze();
        return bitmap;
    }
}

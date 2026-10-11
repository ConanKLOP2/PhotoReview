using System.IO;
using PhotoReview.Imaging.Decoding.Wic;
using PhotoReview.Imaging.Pixels;
using PhotoReview.Imaging.Tests.Fixtures;

namespace PhotoReview.Imaging.Tests.Decoding;

/// <summary>
/// WP-12: every WIC wrapper a code path creates is released before the path returns - also when it fails. A wrapper that is not
/// released keeps its native decoder (and its pixel working memory) alive until a finalizer runs, which is the one regression a
/// pixel-parity test cannot see. <see cref="WicCom.OutstandingOnThisThread"/> counts wrappers created minus released per thread.
/// </summary>
public sealed class WicLifetimeBalanceTests : IDisposable
{
    private readonly TempRoot _root = new("wic-balance");

    public void Dispose() => _root.Dispose();

    private static void AssertBalanced(Action action)
    {
        var before = WicCom.OutstandingOnThisThread;
        try
        {
            action();
        }
        finally
        {
            Assert.Equal(before, WicCom.OutstandingOnThisThread);
        }
    }

    private static void Drop(IDecodedImage image) => (image.PlatformImage as IDisposable)?.Dispose();

    private static PixelBuffer Gradient(int width, int height, PixelLayout layout, byte alpha = 255)
    {
        var pixels = PixelBuffer.Allocate(width, height, layout);
        for (var y = 0; y < height; y++)
        {
            var row = pixels.GetRow(y);
            for (var x = 0; x < width; x++)
            {
                row[x * 4] = (byte)(x * 255 / width);
                row[(x * 4) + 1] = (byte)(y * 255 / height);
                row[(x * 4) + 2] = (byte)((x + y) & 0xFF);
                row[(x * 4) + 3] = alpha;
            }
        }

        return pixels;
    }

    [Fact(DisplayName = "Decode (full size, downscaled + rotated, ICC-transformed) releases every wrapper")]
    public void Decode_AllPipelineShapes_Balanced()
    {
        var plain = FixtureGenerator.GenerateGradientJpeg(_root.Combine("plain.jpg"), 256, 192);
        var rotated = FixtureGenerator.GenerateJpegWithOrientation(_root.Combine("rot.jpg"), 256, 192, 6);
        var icc = FixtureGenerator.GenerateJpegWithIcc(_root.Combine("icc.jpg"), 256, 192);
        var decoder = new WicDirectDecoder(PixelBufferImageCodec.Instance);

        AssertBalanced(() => { Drop(decoder.Decode(new DecodeRequest(plain, 0))); });
        AssertBalanced(() => { Drop(decoder.Decode(new DecodeRequest(plain, 64))); });
        AssertBalanced(() => { Drop(decoder.Decode(new DecodeRequest(rotated, 100))); });
        AssertBalanced(() => { Drop(decoder.Decode(new DecodeRequest(icc, 0))); });
        AssertBalanced(() => { Drop(decoder.Decode(new DecodeRequest(icc, 90))); });
    }

    [Fact(DisplayName = "Decode and ReadInfo of damaged files release every wrapper on the failure path")]
    public void DecodeAndReadInfo_DamagedFiles_Balanced()
    {
        var source = FixtureGenerator.GenerateGradientJpeg(_root.Combine("src.jpg"), 256, 192);
        var truncated = FixtureGenerator.GenerateTruncatedJpeg(source, _root.Combine("cut.jpg"), 0.3);
        var text = FixtureGenerator.GenerateTextFile(_root.Combine("not-an-image.jpg"));
        var decoder = new WicDirectDecoder(PixelBufferImageCodec.Instance);

        foreach (var path in new[] { truncated, text })
        {
            AssertBalanced(() => Record.Exception(() => { Drop(decoder.Decode(new DecodeRequest(path, 0))); }));
            AssertBalanced(() => Record.Exception(() => decoder.ReadInfo(path)));
        }
    }

    [Fact(DisplayName = "ReadInfo releases every wrapper")]
    public void ReadInfo_Balanced()
    {
        var path = FixtureGenerator.GenerateJpegWithOrientation(_root.Combine("o.jpg"), 128, 96, 3);
        var decoder = new WicDirectDecoder(PixelBufferImageCodec.Instance);

        AssertBalanced(() => Assert.Equal(128, decoder.ReadInfo(path).Width));
    }

    [Fact(DisplayName = "EmbeddedThumbnailReader (with and without a thumbnail) releases every wrapper")]
    public void EmbeddedThumbnail_Balanced()
    {
        var path = FixtureGenerator.GenerateGradientJpeg(_root.Combine("t.jpg"), 128, 96);
        var png = FixtureGenerator.GeneratePng(_root.Combine("t.png"), 32, 24);
        var withThumbnail = _root.Combine("thumb.jpg");
        File.WriteAllBytes(withThumbnail, EmbeddedThumbnailJpegFixture.CreateWithThumbnail(mainSize: 48, thumbnailSize: 16));

        AssertBalanced(() => (EmbeddedThumbnailReader.TryRead(path, PixelBufferImageCodec.Instance)?.PlatformImage as IDisposable)?.Dispose());
        AssertBalanced(() => EmbeddedThumbnailReader.TryRead(png, PixelBufferImageCodec.Instance));
        AssertBalanced(() =>
        {
            var image = EmbeddedThumbnailReader.TryRead(withThumbnail, PixelBufferImageCodec.Instance);
            Assert.NotNull(image); // the converter / thumbnail wrappers were really created
            (image.PlatformImage as IDisposable)?.Dispose();
        });
    }

    [Fact(DisplayName = "Cache encode (opaque BGR24 path, alpha WriteSource path), cache decode and the Fant scaler release every wrapper")]
    public void CacheEncodeDecodeAndScale_Balanced()
    {
        using var opaque = Gradient(96, 64, PixelLayout.Bgr32);
        using var translucent = Gradient(96, 64, PixelLayout.Pbgra32, alpha: 128);
        var jpeg = new MemoryStream();
        var png = new MemoryStream();

        AssertBalanced(() => WicImageEncoder.EncodeJpeg(opaque, jpeg, 90));
        AssertBalanced(() => WicImageEncoder.EncodePng(opaque, new MemoryStream()));
        AssertBalanced(() => WicImageEncoder.EncodePng(translucent, png));

        AssertBalanced(() => WicCacheImageReader.Decode(jpeg.ToArray(), null, DecodeBox.Unbounded, 96, 64, out _).Dispose());
        AssertBalanced(() => WicCacheImageReader.Decode(png.ToArray(), null, new DecodeBox(48, 32), 0, 0, out _).Dispose());
        AssertBalanced(() => Record.Exception(() => WicCacheImageReader.Decode(new byte[] { 1, 2, 3, 4 }, null, DecodeBox.Unbounded, 0, 0, out _)));
        AssertBalanced(() => WicPixelScaler.Resize(opaque, 48, 32).Dispose());
    }

    [Fact(DisplayName = "The optional-codec probe releases the factory, the enumerator and every component")]
    public void CodecEnumeration_Balanced()
    {
        AssertBalanced(() => Assert.NotEmpty(WicCodecAvailability.EnumerateDecoders()));
    }
}

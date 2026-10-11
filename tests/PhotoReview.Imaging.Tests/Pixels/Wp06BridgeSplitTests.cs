using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.Model;
using PhotoReview.Imaging.Pixels;
using PhotoReview.TestSupport.Windows;

namespace PhotoReview.Imaging.Tests.Pixels;

/// <summary>
/// WP-06: the WIC Fant scaler that replaced the hand-written fine-scale step, the mandatory codec of every cache/decoder type,
/// the decoder-factory slot of the WPF decoder, and the exception mapping that dropped WPF's <c>FileFormatException</c>.
/// </summary>
[Collection("GlobalState")]
public sealed class WicPixelScalerTests
{
    [Theory(DisplayName = "WicPixelScaler keeps the layout and produces the requested size")]
    [InlineData(PixelLayout.Bgr32, 64, 48, 16, 12)]
    [InlineData(PixelLayout.Pbgra32, 64, 48, 33, 7)]
    [InlineData(PixelLayout.Bgr32, 5, 3, 1, 1)]
    public void Resize_ProducesRequestedSize_AndLayout(PixelLayout layout, int sw, int sh, int tw, int th)
    {
        using var source = PixelAssert.CreatePattern(sw, sh, layout, seed: 4);

        using var scaled = WicPixelScaler.Resize(source, tw, th);

        Assert.Equal((tw, th), (scaled.Width, scaled.Height));
        Assert.Equal(layout, scaled.Layout);
        Assert.False(source.IsDisposed);
    }

    [Fact(DisplayName = "WicPixelScaler: a flat colour stays flat (no edge bleed, no channel swap)")]
    public void Resize_FlatColour_StaysFlat()
    {
        using var source = PixelBuffer.Allocate(40, 30, PixelLayout.Bgr32);
        for (var y = 0; y < 30; y++)
        {
            var row = source.GetRow(y);
            for (var x = 0; x < 40; x++)
            {
                row[x * 4] = 10; row[x * 4 + 1] = 120; row[x * 4 + 2] = 230; row[x * 4 + 3] = 255;
            }
        }

        using var scaled = WicPixelScaler.Resize(source, 13, 9);

        for (var y = 0; y < 9; y++)
        {
            var row = scaled.GetRow(y);
            for (var x = 0; x < 13; x++)
            {
                Assert.Equal(10, row[x * 4]);
                Assert.Equal(120, row[x * 4 + 1]);
                Assert.Equal(230, row[x * 4 + 2]);
            }
        }
    }

    [Fact(DisplayName = "WicPixelScaler keeps premultiplied alpha of a Pbgra32 buffer")]
    public void Resize_Pbgra_KeepsAlpha()
    {
        using var source = PixelBuffer.Allocate(8, 8, PixelLayout.Pbgra32);
        for (var y = 0; y < 8; y++)
        {
            var row = source.GetRow(y);
            for (var x = 0; x < 8; x++)
            {
                row[x * 4] = 50; row[x * 4 + 1] = 25; row[x * 4 + 2] = 10; row[x * 4 + 3] = 100;
            }
        }

        using var scaled = WicPixelScaler.Resize(source, 2, 2);

        var px = scaled.GetRow(0);
        Assert.Equal(100, px[3]);
        Assert.Equal(50, px[0]);
    }

    [Fact(DisplayName = "WicPixelScaler equals WPF's ScaleTransform byte for byte (the pass the legacy TurboJPEG path used)")]
    public void Resize_EqualsWpfScaleTransform()
    {
        using var source = PixelAssert.CreatePattern(120, 90, PixelLayout.Bgr32, seed: 8);
        var bitmap = PixelAssert.ToBitmapSource(source);
        var wpf = new TransformedBitmap(bitmap, new ScaleTransform(40.0 / 120, 30.0 / 90));
        wpf.Freeze();
        using var reference = PixelAssert.FromBitmapSource(wpf, PixelLayout.Bgr32);

        using var scaled = WicPixelScaler.Resize(source, 40, 30);

        Assert.Equal((reference.Width, reference.Height), (scaled.Width, scaled.Height));
        PixelAssert.Equal(reference, scaled); // byte for byte: WPF's ScaleTransform is the same WIC Fant scaler
    }

    [Fact(DisplayName = "WicPixelScaler same size is an independent copy")]
    public void Resize_SameSize_IsACopy()
    {
        using var source = PixelAssert.CreatePattern(17, 9, PixelLayout.Bgr32, seed: 2);

        using var copy = WicPixelScaler.Resize(source, 17, 9);

        PixelAssert.Equal(source, copy);
        Assert.NotEqual(source.Address, copy.Address);
    }

    [Fact(DisplayName = "WicPixelScaler validates its arguments and leaks no buffer")]
    public void Resize_InvalidArguments_Throw_AndLeakNothing()
    {
        var before = NativePixelMemory.LiveCount;
        var source = PixelBuffer.Allocate(4, 4, PixelLayout.Bgr32);
        Assert.Throws<ArgumentNullException>(() => WicPixelScaler.Resize(null!, 2, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => WicPixelScaler.Resize(source, 0, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => WicPixelScaler.Resize(source, 2, 0));
        source.Dispose();
        Assert.Throws<ObjectDisposedException>(() => WicPixelScaler.Resize(source, 2, 2));
        Assert.Equal(before, NativePixelMemory.LiveCount);
    }

    [Fact(DisplayName = "WicPixelScaler holds exactly one extra buffer, the result")]
    public void Resize_HoldsOnlyTheResult()
    {
        using var source = PixelAssert.CreatePattern(32, 32, PixelLayout.Bgr32, seed: 1);
        var before = NativePixelMemory.LiveCount;

        var scaled = WicPixelScaler.Resize(source, 8, 8);

        Assert.Equal(before + 1, NativePixelMemory.LiveCount);
        scaled.Dispose();
        Assert.Equal(before, NativePixelMemory.LiveCount);
    }
}

public sealed class Wp06RequiredCodecTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "PhotoReview-Wp06-" + Guid.NewGuid().ToString("N"));

    public Wp06RequiredCodecTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact(DisplayName = "PreviewImageService refuses a null codec (both constructors)")]
    public void PreviewImageService_NullCodec_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new PreviewImageService(new ReviewMetrics(), () => false, () => 100, null!,
            diskCacheDirectory: _dir));
        Assert.Throws<ArgumentNullException>(() => new PreviewImageService(new ReviewMetrics(), () => false, () => new DecodeBox(100, 0), null!,
            diskCacheDirectory: _dir));
    }

    [Fact(DisplayName = "ThumbnailCache refuses a null codec")]
    public void ThumbnailCache_NullCodec_Throws() =>
        Assert.Throws<ArgumentNullException>(() => new ThumbnailCache(null!, _dir));

    [Fact(DisplayName = "LibRawDecoder refuses a null codec")]
    public void LibRawDecoder_NullCodec_Throws() =>
        Assert.Throws<ArgumentNullException>(() => new PhotoReview.Imaging.LibRaw.LibRawDecoder(null!));

    [Fact(DisplayName = "Cache writers refuse a null codec")]
    public async Task CacheWriters_NullCodec_Throw()
    {
        var image = new WpfDecodedImage(PixelAssert.ToBitmapSource(PixelAssert.CreatePattern(4, 4, PixelLayout.Bgr32, 1)));
        await Assert.ThrowsAsync<ArgumentNullException>(() => PreviewCacheFile.WriteAtomicallyAsync(image, null!, Path.Combine(_dir, "a.pv4")));
        await Assert.ThrowsAsync<ArgumentNullException>(() => DiskCacheStore.WriteAtomicallyAsync(image, null!, Path.Combine(_dir, "a.png")));
        Assert.Throws<ArgumentNullException>(() => PreviewCacheFile.ReadAsDecodedImage(Path.Combine(_dir, "a.pv4"), null!));
        Assert.Throws<ArgumentNullException>(() => EmbeddedThumbnailReader.TryRead(Path.Combine(_dir, "a.jpg"), null!));
    }

    [Fact(DisplayName = "PreviewImageService refuses a null codec even when a decoder is supplied (no late NullReference)")]
    public void PreviewImageService_NullCodec_WithExplicitDecoder_Throws() =>
        Assert.Throws<ArgumentNullException>(() => new PreviewImageService(new ReviewMetrics(), () => false, () => new DecodeBox(100, 0), null!,
            diskCacheDirectory: _dir, decoder: new PhotoReview.Imaging.Decoding.Wic.WicDirectDecoder(PixelBufferImageCodec.Instance)));

    [Fact(DisplayName = "ThumbnailCache hands the embedded thumbnail to the INJECTED codec")]
    public async Task ThumbnailCache_EmbeddedThumbnail_UsesTheInjectedCodec()
    {
        var source = Path.Combine(_dir, "t.jpg");
        File.WriteAllBytes(source, EmbeddedThumbnailJpegFixture.CreateWithThumbnail(mainSize: 48, thumbnailSize: 16));
        using var cache = new ThumbnailCache(new TaggingCodec(), Path.Combine(_dir, "thumbs"), maxRamBytes: 16 * 1024 * 1024);

        var image = await cache.GetAsync(source, null);

        Assert.NotNull(image);
        Assert.IsType<TaggedImage>(image!.PlatformImage);
    }

    private sealed record TaggedImage(PixelBuffer Pixels);

    private sealed class TaggingCodec : IPlatformImageCodec
    {
        public string Name => "tagging";
        public object FromPixels(PixelBuffer pixels) => new TaggedImage(pixels);
        public PixelLease ToPixels(object platformImage) => new(((TaggedImage)platformImage).Pixels, owned: false);
    }

    [Fact(DisplayName = "PreviewImageService without a decoder or factory decodes with WIC Direct through the injected codec")]
    public async Task PreviewImageService_DefaultDecoder_IsWicDirectWithTheCodec()
    {
        var path = Path.Combine(_dir, "p.png");
        File.WriteAllBytes(path, PhotoReview.TestSupport.TestImages.PreviewPng);
        var codec = new CountingCodec();
        var service = new PreviewImageService(new ReviewMetrics(), () => false, () => 512, codec,
            capacityBytes: 32L * 1024 * 1024, diskCacheDirectory: Path.Combine(_dir, "cache"), disableDiskCacheOverride: true);

        var image = await service.GetPreviewAsync(path);

        Assert.Equal(DecoderBackend.WicDirect, image.ActualBackend);
        Assert.IsType<PixelBuffer>(image.PlatformImage);
        Assert.True(codec.FromPixelsCalls >= 1);
    }

    private sealed class CountingCodec : IPlatformImageCodec
    {
        public int FromPixelsCalls;
        public string Name => "counting";
        public object FromPixels(PixelBuffer pixels) { Interlocked.Increment(ref FromPixelsCalls); return pixels; }
        public PixelLease ToPixels(object platformImage) => new((PixelBuffer)platformImage, owned: false);
    }
}

public sealed class Wp06DecoderFactoryWpfSlotTests
{
    private sealed class Marker(string name) : IImageDecoder
    {
        public string Name { get; } = name;
        public IDecodedImage Decode(DecodeRequest request) => throw new NotSupportedException();
        public ImageInfo ReadInfo(string path) => throw new NotSupportedException();
    }

    [Fact(DisplayName = "ImageDecoderFactory_WithoutWpf_MapsWpfToWicDirect_AndKeepsSetting")]
    public void WithoutWpf_MapsWpfToWicDirect()
    {
        var wic = new Marker("wic");
        var factory = new ImageDecoderFactory([(DecoderBackend.WicDirect, () => wic)]);

        // The persisted "Wpf" setting is only asked for, never rewritten: the slot is served by WIC Direct.
        Assert.Same(wic, factory.Create(DecoderBackend.Wpf));
        Assert.True(factory.IsRegistered(DecoderBackend.WicDirect));
        Assert.True(factory.IsRegistered(DecoderBackend.Wpf)); // the slot is always served
    }

    [Fact(DisplayName = "Without a Wpf decoder a WicDirect request is not wrapped in a fallback to itself")]
    public void WithoutWpf_WicDirectIsNotWrapped()
    {
        var wic = new Marker("wic");
        var factory = new ImageDecoderFactory([(DecoderBackend.WicDirect, () => wic)]);

        Assert.Same(wic, factory.Create(DecoderBackend.WicDirect));
    }

    [Fact(DisplayName = "Without a Wpf decoder another backend falls back to WIC Direct")]
    public void WithoutWpf_OtherBackendFallsBackToWicDirect()
    {
        var factory = new ImageDecoderFactory([(DecoderBackend.WicDirect, () => new Marker("wic")),
            (DecoderBackend.TurboJpeg, () => new Marker("turbo"))]);

        Assert.IsType<FallbackImageDecoder>(factory.Create(DecoderBackend.TurboJpeg));
    }

    [Fact(DisplayName = "wpfDecoderFactory fills the Wpf slot; an explicit Wpf provider wins over it")]
    public void WpfDecoderFactory_FillsTheSlot_ExplicitProviderWins()
    {
        var fromFactory = new Marker("factory");
        var explicitWpf = new Marker("explicit");

        var viaFactory = new ImageDecoderFactory([(DecoderBackend.WicDirect, () => new Marker("wic"))], wpfDecoderFactory: () => fromFactory);
        var both = new ImageDecoderFactory([(DecoderBackend.Wpf, () => explicitWpf)], wpfDecoderFactory: () => fromFactory);

        Assert.Same(fromFactory, viaFactory.Create(DecoderBackend.Wpf));
        Assert.Same(explicitWpf, both.Create(DecoderBackend.Wpf));
    }

    [Fact(DisplayName = "A factory with neither a Wpf nor a WicDirect decoder is a composition error")]
    public void NoWpfAndNoWicDirect_Throws() =>
        Assert.Throws<ArgumentException>(() => new ImageDecoderFactory([(DecoderBackend.TurboJpeg, () => new Marker("t"))]));
}

public sealed class Wp06WpfFreeExceptionMappingTests
{
    [Fact(DisplayName = "FormatException (the base of WPF's FileFormatException) is a fallbackable decode failure and a cache-entry failure")]
    public void FormatException_IsFallbackable_AndACacheEntryFailure()
    {
        Assert.True(FallbackImageDecoder.IsFallbackable(new FormatException("bad header")));
        Assert.True(FallbackImageDecoder.IsFallbackable(new FileFormatException("bad header")));
        Assert.True(DiskCacheStore.IsCacheEntryFailure(new FormatException("bad entry")));
        Assert.True(DiskCacheStore.IsCacheEntryFailure(new FileFormatException("bad entry")));
    }

    [Fact(DisplayName = "WpfBitmapSourceCodec.HasAlpha(IDecodedImage) is false for a foreign platform image and throws for null")]
    public void HasAlpha_ForeignImage_IsFalse()
    {
        Assert.Throws<ArgumentNullException>(() => WpfBitmapSourceCodec.HasAlpha((IDecodedImage)null!));
        using var pixels = PixelBuffer.Allocate(2, 2, PixelLayout.Pbgra32);
        Assert.False(WpfBitmapSourceCodec.HasAlpha(new DecodedImage(pixels, 2, 2, 16, false)));
    }
}

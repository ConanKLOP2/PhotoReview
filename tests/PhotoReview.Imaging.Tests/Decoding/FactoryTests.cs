using System.IO;
using System.Runtime.InteropServices;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.Model;
using PhotoReview.Imaging.Caching;
using PhotoReview.Imaging.Decoding;
using PhotoReview.Imaging.Decoding.Wic;
using PhotoReview.Imaging.Tests.Fixtures;

namespace PhotoReview.Imaging.Tests.Decoding;

[Trait("Category", "HotPath")]
public sealed class FactoryTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _validImagePath;

    public FactoryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "PhotoReview-FactoryTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _validImagePath = Path.Combine(_tempDir, "valid.jpg");
        FixtureGenerator.GenerateGradientJpeg(_validImagePath, 64, 48);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    [Fact(DisplayName = "ImageDecoderFactory returns Wpf decoder for Wpf backend")]
    public void FactoryReturnsWpfDecoderDirectly()
    {
        var factory = new ImageDecoderFactory([]);
        var decoder = factory.Create(DecoderBackend.Wpf);

        Assert.IsType<WpfBitmapImageDecoder>(decoder);
    }

    [Theory(DisplayName = "ImageDecoderFactory wraps a registered non-Wpf backend in FallbackImageDecoder that falls back to Wpf")]
    [InlineData(DecoderBackend.WicDirect)]
    [InlineData(DecoderBackend.TurboJpeg)]
    public void FactoryWrapsRegisteredBackendInFallbackDecoder(DecoderBackend backend)
    {
        var mockDecoder = new MockFailingDecoder(new NotSupportedException());
        var factory = new ImageDecoderFactory(new (DecoderBackend, Func<IImageDecoder>)[]
        {
            (backend, () => mockDecoder),
            (DecoderBackend.Wpf, () => new WpfBitmapImageDecoder())
        });

        var decoder = factory.Create(backend);
        var fallbackDecoder = Assert.IsType<FallbackImageDecoder>(decoder);
        Assert.Equal(DecoderBackend.Wpf, FallbackImageDecoder.FallbackBackend);

        // The primary throws NotSupportedException, so the decode is served by the Wpf fallback.
        var decoded = decoder.Decode(new DecodeRequest(_validImagePath, TargetWidth: 0));
        Assert.Equal(64, decoded.PixelWidth);
        Assert.Equal(48, decoded.PixelHeight);
    }

    [Fact(DisplayName = "FallbackImageDecoder returns primary result when primary succeeds")]
    public void PrimarySucceedsReturnsPrimaryBackend()
    {
        var wpf = new WpfBitmapImageDecoder();
        var metrics = new ReviewMetrics();
        var fallbackDecoder = new FallbackImageDecoder(
            wpf, DecoderBackend.WicDirect, wpf, metrics: metrics);

        var decoded = fallbackDecoder.Decode(new DecodeRequest(_validImagePath, TargetWidth: 0));

        Assert.NotNull(decoded);
        Assert.Equal(DecoderBackend.WicDirect, decoded.ActualBackend);
        Assert.False(metrics.Snapshot().DecoderFallbacks.ContainsKey(DecoderBackend.WicDirect));
    }

    [Theory(DisplayName = "FallbackImageDecoder falls back to Wpf when primary throws fallbackable exceptions (INV-12)")]
    [InlineData("NotSupported")]
    [InlineData("FileFormat")]
    [InlineData("InvalidData")]
    [InlineData("COM")]
    [InlineData("DllNotFound")]
    public void FallbackOccursOnSupportedExceptions(string exceptionType)
    {
#pragma warning disable CA2201 // Creating COMException intentionally for mock testing fallback
        Exception ex = exceptionType switch
        {
            "NotSupported" => new NotSupportedException("Unsupported format"),
            "FileFormat" => new FileFormatException("Invalid header"),
            "InvalidData" => new InvalidDataException("Corrupt data"),
            "COM" => new COMException("Native COM failure", unchecked((int)0x88982F50)),
            "DllNotFound" => new DllNotFoundException("libjpeg-turbo.dll not found"),
            _ => throw new ArgumentException("Unknown type", nameof(exceptionType))
        };
#pragma warning restore CA2201

        var failingMock = new MockFailingDecoder(ex);
        var wpf = new WpfBitmapImageDecoder();
        var metrics = new ReviewMetrics();
        var fallbackDecoder = new FallbackImageDecoder(
            failingMock, DecoderBackend.TurboJpeg, wpf, metrics: metrics);

        var decoded = fallbackDecoder.Decode(new DecodeRequest(_validImagePath, TargetWidth: 0));

        Assert.NotNull(decoded);
        Assert.Equal(DecoderBackend.Wpf, decoded.ActualBackend);
        Assert.Equal(1, metrics.Snapshot().DecoderFallbacks[DecoderBackend.TurboJpeg]);
    }

    [Fact(DisplayName = "FallbackImageDecoder rethrows FileNotFoundException immediately without fallback")]
    public void MissingFileDoesNotFallback()
    {
        var missingPath = Path.Combine(_tempDir, "missing_" + Guid.NewGuid().ToString("N") + ".jpg");
        var failingMock = new MockFailingDecoder(new FileNotFoundException("File not found", missingPath));
        var wpf = new WpfBitmapImageDecoder();
        var metrics = new ReviewMetrics();
        var fallbackDecoder = new FallbackImageDecoder(
            failingMock, DecoderBackend.WicDirect, wpf, metrics: metrics);

        Assert.Throws<FileNotFoundException>(() =>
            fallbackDecoder.Decode(new DecodeRequest(missingPath, TargetWidth: 0)));

        Assert.False(metrics.Snapshot().DecoderFallbacks.ContainsKey(DecoderBackend.WicDirect));
    }

    [Fact(DisplayName = "FallbackImageDecoder delegates ReadInfo to fallback when primary fails")]
    public void ReadInfoFallsBack()
    {
        var failingMock = new MockFailingDecoder(new NotSupportedException());
        var wpf = new WpfBitmapImageDecoder();
        var metrics = new ReviewMetrics();
        var fallbackDecoder = new FallbackImageDecoder(
            failingMock, DecoderBackend.WicDirect, wpf, metrics: metrics);

        var info = fallbackDecoder.ReadInfo(_validImagePath);

        Assert.Equal(64, info.PixelWidth);
        Assert.Equal(48, info.PixelHeight);
        Assert.Equal(1, metrics.Snapshot().DecoderFallbacks[DecoderBackend.WicDirect]);
    }

    [Fact(DisplayName = "ImageCacheKey distinguishes keys by DecoderBackend")]
    public void ImageCacheKeyDistinguishesBackends()
    {
        var keyWpf = ImageCacheKey.Create(_validImagePath, isOriginal: true, targetWidth: 0, orientationApplied: true, backend: DecoderBackend.Wpf);
        var keyWic = ImageCacheKey.Create(_validImagePath, isOriginal: true, targetWidth: 0, orientationApplied: true, backend: DecoderBackend.WicDirect);

        Assert.NotEqual(keyWpf, keyWic);
        Assert.NotEqual(keyWpf.GetHashCode(), keyWic.GetHashCode());
    }

    [Fact(DisplayName = "PreviewImageService integrates IImageDecoderFactory with custom backend")]
    public async Task PreviewImageServiceIntegratesDecoderFactory()
    {
        var metrics = new ReviewMetrics();
        var factory = new ImageDecoderFactory(new (DecoderBackend, Func<IImageDecoder>)[]
        {
            (DecoderBackend.Wpf, () => new WpfBitmapImageDecoder())
        }, metrics: metrics);

        var currentBackend = DecoderBackend.Wpf;
        var service = new PreviewImageService(
            metrics,
            () => true,
            () => 0, WpfBitmapSourceCodec.Instance,
            diskCacheDirectory: Path.Combine(_tempDir, "cache"),
            currentBackend: () => currentBackend,
            decoderFactory: factory);

        var key = service.GetCurrentCacheKey(_validImagePath);
        Assert.Equal(DecoderBackend.Wpf, key.Backend);

        var preview = await service.GetPreviewAsync(_validImagePath);
        Assert.NotNull(preview);
        Assert.Equal(64, preview.PixelWidth);
        Assert.Equal(48, preview.PixelHeight);
    }

    [Fact(DisplayName = "ImageDecoderFactory falls back to Wpf decoder for an unregistered backend (AR01)")]
    public void Create_UnregisteredBackend_ReturnsWpfDecoder()
    {
        var factory = new ImageDecoderFactory([]);

        var decoder = factory.Create(DecoderBackend.TurboJpeg);

        Assert.IsType<WpfBitmapImageDecoder>(decoder);
    }

    [Fact(DisplayName = "ImageDecoderFactory.Create caches one decoder per backend and builds each provider once (IMG-08)")]
    public void Create_SameBackendTwice_ReturnsSameInstanceAndBuildsOnce()
    {
        var wicBuilds = 0;
        var factory = new ImageDecoderFactory(new (DecoderBackend, Func<IImageDecoder>)[]
        {
            (DecoderBackend.Wpf, () => new WpfBitmapImageDecoder()),
            (DecoderBackend.WicDirect, () => { wicBuilds++; return new WicDirectDecoder(WpfBitmapSourceCodec.Instance); })
        });

        Assert.Same(factory.Create(DecoderBackend.WicDirect), factory.Create(DecoderBackend.WicDirect));
        Assert.Same(factory.Create(DecoderBackend.Wpf), factory.Create(DecoderBackend.Wpf));
        Assert.NotSame(factory.Create(DecoderBackend.Wpf), factory.Create(DecoderBackend.WicDirect));
        Assert.Equal(1, wicBuilds);
    }

    [Fact(DisplayName = "ImageDecoderFactory.Create called from 16 threads at once builds the provider and decorates exactly once and shares one instance (RV-I07)")]
    public void Create_Concurrent_BuildsAndDecoratesOnce()
    {
        const int threads = 16;
        var providerRuns = 0;
        var decoratorRuns = 0;
        var factory = new ImageDecoderFactory(
            new (DecoderBackend, Func<IImageDecoder>)[]
            {
                (DecoderBackend.Wpf, () => new WpfBitmapImageDecoder()),
                (DecoderBackend.WicDirect, () => { System.Threading.Interlocked.Increment(ref providerRuns); System.Threading.Thread.SpinWait(2_000_000); return new WicDirectDecoder(WpfBitmapSourceCodec.Instance); })
            },
            decoderDecorator: (_, decoder) => { System.Threading.Interlocked.Increment(ref decoratorRuns); return decoder; });

        using var barrier = new System.Threading.Barrier(threads);
        var results = new IImageDecoder[threads];
        var workers = new System.Threading.Thread[threads];
        for (var i = 0; i < threads; i++)
        {
            var index = i;
            workers[i] = new System.Threading.Thread(() =>
            {
                barrier.SignalAndWait();
                results[index] = factory.Create(DecoderBackend.WicDirect);
            });
            workers[i].Start();
        }

        foreach (var worker in workers) worker.Join();

        Assert.Equal(1, providerRuns);
        Assert.Equal(1, decoratorRuns);
        Assert.All(results, r => Assert.Same(results[0], r));
    }

    [Fact(DisplayName = "ImageDecoderFactory.IsRegistered reflects the providers passed to it (AR01)")]
    public void IsRegistered_ReflectsProviders()
    {
        var factory = new ImageDecoderFactory(new (DecoderBackend, Func<IImageDecoder>)[]
        {
            (DecoderBackend.Wpf, () => new WpfBitmapImageDecoder()),
            (DecoderBackend.WicDirect, () => new WicDirectDecoder(WpfBitmapSourceCodec.Instance))
        });

        Assert.True(factory.IsRegistered(DecoderBackend.Wpf));
        Assert.True(factory.IsRegistered(DecoderBackend.WicDirect));
        Assert.False(factory.IsRegistered(DecoderBackend.TurboJpeg));
    }

    private sealed class MockFailingDecoder : IImageDecoder
    {
        private readonly Exception _exceptionToThrow;

        public MockFailingDecoder(Exception exceptionToThrow)
        {
            _exceptionToThrow = exceptionToThrow;
        }

        public IDecodedImage Decode(DecodeRequest request) => throw _exceptionToThrow;
        public ImageInfo ReadInfo(string path) => throw _exceptionToThrow;
    }
}


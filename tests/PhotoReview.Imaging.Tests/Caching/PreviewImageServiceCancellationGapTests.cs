using System.IO;
using PhotoReview.Imaging.Decoding;
using PhotoReview.TestSupport;

namespace PhotoReview.Imaging.Tests.Caching;

/// <summary>Mutation-gap test: the creator of a decode keeps a cancellation its own token did not request (only a joiner may retry).</summary>
public sealed class PreviewImageServiceCancellationGapTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private readonly TempRoot _root = new("preview-cancel-gap");
    private readonly CancelOnFirstCallDecoder _decoder = new();
    private readonly PreviewImageService _service;
    private readonly string _path;

    public PreviewImageServiceCancellationGapTests()
    {
        _path = _root.File(Path.Combine("images", "a.jpg"), 1, 2, 3, 4);
        _service = new PreviewImageService(new PhotoReview.Core.Diagnostics.ReviewMetrics(), () => false, () => 256, WpfBitmapSourceCodec.Instance, capacityBytes: 64L * 1024 * 1024,
            diskCacheDirectory: _root.Dir("cache"), disableDiskCacheOverride: true, decoder: _decoder);
    }

    public void Dispose()
    {
        _service.ShutdownPersistWorkersAsync().GetAwaiter().GetResult();
        _root.Dispose();
    }

    [Fact(DisplayName = "A creator whose decode throws OperationCanceledException (its own token not cancelled) sees it, instead of silently retrying")]
    public async Task GetPreview_CreatorDecodeCancelled_Propagates()
    {
        var key = _service.GetCurrentCacheKey(_path);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _service.GetPreviewAsync(_path, key).WaitAsync(Timeout));

        Assert.Equal(1, _decoder.Calls);
    }

    private sealed class CancelOnFirstCallDecoder : IImageDecoder
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        public IDecodedImage Decode(DecodeRequest request)
        {
            if (Interlocked.Increment(ref _calls) == 1) throw new OperationCanceledException("test: decoder-internal cancellation");
            return new FakeImage();
        }

        public ImageInfo ReadInfo(string path) => new(1, 1);
    }

    private sealed class FakeImage : IDecodedImage
    {
        public int PixelWidth => 1;
        public int PixelHeight => 1;
        public bool Downscaled => false;
        public int Orientation => 1;
        public long EstimatedBytes => 4;
        public object PlatformImage { get; } = new();
    }
}

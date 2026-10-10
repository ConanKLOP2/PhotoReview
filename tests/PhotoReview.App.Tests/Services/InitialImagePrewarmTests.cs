using System.IO;
using PhotoReview.App.Services;
using PhotoReview.Core.Diagnostics;
using Xunit;

namespace PhotoReview.App.Tests.Services;

/// <summary>
/// P-1 startup: <see cref="InitialImagePrewarm.Start"/> begins the launch file's viewer decode before the presenter asks
/// for it, keyed with the predicted box when one is given (else the service's current viewport box).
/// </summary>
[Trait("Category", "HotPath")]
public sealed class InitialImagePrewarmTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly DecodeBox Predicted = new(1280, 768);
    private static readonly DecodeBox Viewport = new(1920, 1152);

    private readonly string _dir;
    private readonly CountingDecoder _decoder = new();
    private readonly PreviewImageService _previews;

    public InitialImagePrewarmTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "PhotoReview_Prewarm_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _previews = new PreviewImageService(new ReviewMetrics(), () => false, () => Viewport, capacityBytes: 64L * 1024 * 1024,
            diskCacheDirectory: Path.Combine(_dir, "cache"), disableDiskCacheOverride: true, decoder: _decoder);
    }

    public void Dispose()
    {
        _previews.ShutdownPersistWorkersAsync().GetAwaiter().GetResult();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string CreateFile(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, [1, 2, 3, 4]);
        return path;
    }

    [Fact]
    public void Start_WithoutService_StartsNothing()
    {
        Assert.Null(InitialImagePrewarm.Start(null, new AppSettings(), CreateFile("a.jpg")));
        Assert.Equal(0, _decoder.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Start_BlankPath_StartsNothing(string? path)
    {
        Assert.Null(InitialImagePrewarm.Start(_previews, new AppSettings(), path));
        Assert.Equal(0, _decoder.Calls);
    }

    [Theory(DisplayName = "Unsupported files and camera RAW files are not prewarmed")]
    [InlineData("notes.txt")]
    [InlineData("shot.cr2")]
    [InlineData("shot.NEF")]
    public void Start_UnsupportedOrRawFile_StartsNothing(string name)
    {
        Assert.Null(InitialImagePrewarm.Start(_previews, new AppSettings(), CreateFile(name)));
        Assert.Equal(0, _decoder.Calls);
    }

    [Fact(DisplayName = "WebP is prewarmed only when WebP/HEIC support is enabled")]
    public async Task Start_WebpFile_FollowsTheWebpHeicSetting()
    {
        var path = CreateFile("pic.webp");

        Assert.Null(InitialImagePrewarm.Start(_previews, new AppSettings { WebpHeicSupportEnabled = false }, path));
        var started = InitialImagePrewarm.Start(_previews, new AppSettings { WebpHeicSupportEnabled = true }, path);

        Assert.NotNull(started);
        await started!.WaitAsync(Timeout);
        Assert.Equal(1, _decoder.Calls);
    }

    [Fact(DisplayName = "An explicit box keys the decode: its entry is cached under that box and not under the viewport box")]
    public async Task Start_WithExplicitBox_DecodesAndCachesUnderThatBox()
    {
        var path = Path.GetFullPath(CreateFile("launch.jpg"));

        var started = InitialImagePrewarm.Start(_previews, new AppSettings(), path, Predicted);

        Assert.NotNull(started);
        await started!.WaitAsync(Timeout);
        Assert.Equal(1, _decoder.Calls);
        Assert.True(_previews.TryGetCachedPreview(_previews.GetCurrentCacheKey(path, Predicted), out _));
        Assert.False(_previews.TryGetCachedPreview(_previews.GetCurrentCacheKey(path), out _));
        Assert.Equal(Predicted, _decoder.LastRequestedBox);
    }

    [Fact(DisplayName = "Without a box the decode is keyed with the service's current viewport box")]
    public async Task Start_WithoutBox_DecodesAndCachesUnderTheViewportBox()
    {
        var path = Path.GetFullPath(CreateFile("launch.jpg"));

        var started = InitialImagePrewarm.Start(_previews, new AppSettings(), path);

        Assert.NotNull(started);
        await started!.WaitAsync(Timeout);
        Assert.True(_previews.TryGetCachedPreview(_previews.GetCurrentCacheKey(path), out _));
        Assert.False(_previews.TryGetCachedPreview(_previews.GetCurrentCacheKey(path, Predicted), out _));
    }

    private sealed class CountingDecoder : IImageDecoder
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public DecodeBox LastRequestedBox { get; private set; }

        public IDecodedImage Decode(DecodeRequest request)
        {
            Interlocked.Increment(ref _calls);
            LastRequestedBox = request.Box;
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

using Microsoft.Extensions.DependencyInjection;
using PhotoReview.App.Composition;
using PhotoReview.Core;
using PhotoReview.Core.Abstractions;
using PhotoReview.Imaging.Caching;
using PhotoReview.Imaging.Pixels;
using PhotoReview.Shell.Integration.Tests.Startup;

namespace PhotoReview.Shell.Tests.Startup;

/// <summary>WP-20: đồ thị dùng chung với codec <c>PixelBufferImageCodec</c> giải mã ra <c>PixelBuffer</c> (không cần dispatcher/cửa sổ).</summary>
[Trait("Category", "HotPath")]
public sealed class ShellSharedGraphTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pr-graph-" + Guid.NewGuid().ToString("N")[..8]);

    public ShellSharedGraphTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Decoding_ThroughTheSharedGraph_YieldsAPixelBufferNotAWpfBitmap()
    {
        // Đồ thị dùng chung thuần (không dispatcher của fixture): test async nên không được gắn với thread của fixture.
        var jpeg = TestJpeg.WriteTo(Path.Combine(_root, "photos", "a.jpg"));
        var services = new ServiceCollection();
        services.AddSingleton<IAppPaths>(new AppPaths(_root, Path.Combine(_root, "data"), isolateConfig: true));
        services.AddPhotoReviewShared(new SharedServiceOptions(
            PhotoReview.Imaging.Decoding.PixelBufferImageCodec.Instance, null,
            _ => new PhotoReview.Imaging.Decoding.Wic.WicDirectDecoder(PhotoReview.Imaging.Decoding.PixelBufferImageCodec.Instance)));
        await using var provider = services.BuildServiceProvider();
        var previews = provider.GetRequiredService<PreviewImageService>();

        var image = await previews.GetViewerPreviewAsync(jpeg, previews.GetCurrentCacheKey(jpeg), CancellationToken.None);

        var pixels = Assert.IsType<PixelBuffer>(image.PlatformImage);
        Assert.Equal((TestJpeg.Width, TestJpeg.Height), (pixels.Width, pixels.Height));
        Assert.Equal((TestJpeg.Width, TestJpeg.Height), (image.OriginalWidth, image.OriginalHeight));
    }
}
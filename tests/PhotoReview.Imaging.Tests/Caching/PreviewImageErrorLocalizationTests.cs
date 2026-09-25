using System.IO;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.Localization;
using PhotoReview.Imaging.Tests.Fixtures;
using PhotoReview.TestSupport;

namespace PhotoReview.Imaging.Tests.Caching;

/// <summary>
/// The "source changed" errors of <see cref="PhotoReview.Imaging.Caching.PreviewImageService"/> reach the status bar as
/// "Image error"; they must be localized while staying <see cref="IOException"/>. A key taken before the file is
/// rewritten makes the post-decode verify fail deterministically (no timing involved).
/// </summary>
[Collection("UiLanguage")]
[Trait("Category", "HotPath")]
#pragma warning disable CA1001 // _root (TempRoot) is disposed in DisposeAsync via IAsyncLifetime, which xUnit invokes; CA1001 does not recognize async disposal
public sealed class PreviewImageErrorLocalizationTests : IAsyncLifetime
#pragma warning restore CA1001
{
    private readonly TempRoot _root = new("preview-errors");
    private readonly string _path;
    private readonly string _diskCache;
    private readonly PhotoReview.Imaging.Caching.PreviewImageService _service;

    public PreviewImageErrorLocalizationTests()
    {
        _path = _root.File("photo.png", TestImages.PreviewPng);
        _diskCache = _root.Dir("disk-cache");
        _service = new PhotoReview.Imaging.Caching.PreviewImageService(new ReviewMetrics(), () => false, () => 512,
            capacityBytes: 64L * 1024 * 1024, diskCacheDirectory: _diskCache);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _service.ShutdownPersistWorkersAsync();
        await _service.WaitForPruneAsync(TimeSpan.FromSeconds(5));
        _root.Dispose();
    }

    private ImageCacheKey StaleKey()
    {
        var key = _service.GetCurrentCacheKey(_path);
        File.SetLastWriteTimeUtc(_path, DateTime.UtcNow.AddDays(3)); // rewritten by "another program" after the key was taken
        return key;
    }

    private static (string English, string Vietnamese) Describe(Exception ex)
    {
        string english, vietnamese;
        using (TestLocalization.Use(TestLocalization.English)) english = UserFacingError.Describe(ex);
        using (TestLocalization.Use(TestLocalization.Vietnamese)) vietnamese = UserFacingError.Describe(ex);
        return (english, vietnamese);
    }

    [Fact(DisplayName = "Preview decode of a file rewritten during the decode: localized IOException")]
    public async Task Preview_SourceChangedDuringDecode_IsLocalized()
    {
        var key = StaleKey();

        var ex = await Assert.ThrowsAsync<IOException>(() => _service.GetPreviewAsync(_path, key));

        var (english, vietnamese) = Describe(ex);
        Assert.Equal($"The image file changed while it was being decoded: {_path}", english);
        Assert.Equal($"Tệp ảnh đã thay đổi trong lúc giải mã: {_path}", vietnamese);
        Assert.Equal($"Image source changed during decode: {_path}", ex.Message);
    }

    [Fact(DisplayName = "Original decode of a rewritten file: localized IOException")]
    public async Task Original_SourceChangedDuringDecode_IsLocalized()
    {
        var key = StaleKey();

        var ex = await Assert.ThrowsAsync<IOException>(() => _service.DecodeOriginalAsync(_path, key, CancellationToken.None));

        var (english, vietnamese) = Describe(ex);
        Assert.Equal($"The image file changed while it was being decoded: {_path}", english);
        Assert.Equal($"Tệp ảnh đã thay đổi trong lúc giải mã: {_path}", vietnamese);
    }

    [Fact(DisplayName = "Reading original dimensions of a rewritten file: localized IOException")]
    public async Task OriginalDimensions_SourceChanged_IsLocalized()
    {
        var key = StaleKey();

        var ex = await Assert.ThrowsAsync<IOException>(() => _service.GetOriginalDimensionsAsync(_path, key));

        var (english, vietnamese) = Describe(ex);
        Assert.Equal($"The image file changed while its size was being read: {_path}", english);
        Assert.Equal($"Tệp ảnh đã thay đổi trong lúc đọc kích thước: {_path}", vietnamese);
        Assert.Equal($"Image source changed while reading dimensions: {_path}", ex.Message);
    }
}

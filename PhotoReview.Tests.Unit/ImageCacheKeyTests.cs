using System.IO;
using PhotoReview.App;

namespace PhotoReview.Tests.Unit;

/// <summary>Migrated from PhotoReview.Tests/ImageCacheKeyTests.cs.</summary>
public sealed class ImageCacheKeyTests : IDisposable
{
    private readonly TempRoot _root = new("cache-key");
    private readonly string _path;

    public ImageCacheKeyTests() => _path = _root.File("cache-identity.jpg", 1, 2, 3);

    public void Dispose() => _root.Dispose();

    [Fact(DisplayName = "Cache key reuses unchanged source/mode/width")]
    public void CacheKeyReusesUnchangedSourceModeWidth()
    {
        var preview = ImageCacheKey.Create(_path, false, 2400);
        var same = ImageCacheKey.Create(_path, false, 2400);
        Assert.True(preview == same);
    }

    [Fact(DisplayName = "Cache key separates decode width and Original mode")]
    public void CacheKeySeparatesDecodeWidthAndOriginalMode()
    {
        var preview = ImageCacheKey.Create(_path, false, 2400);
        var resized = ImageCacheKey.Create(_path, false, 3200);
        var original = ImageCacheKey.Create(_path, true, 0);
        Assert.True(preview != resized && preview != original);
    }

    [Fact(DisplayName = "Cache key rejects a replacement at the same path")]
    public void CacheKeyRejectsReplacementAtSamePath()
    {
        var preview = ImageCacheKey.Create(_path, false, 2400);
        File.WriteAllBytes(_path, [4, 5, 6, 7]);
        var replacement = ImageCacheKey.Create(_path, false, 2400);
        Assert.True(preview != replacement && !preview.MatchesCurrentSource());
    }

    [Fact(DisplayName = "Cache key rejects a removed source")]
    public void CacheKeyRejectsRemovedSource()
    {
        File.WriteAllBytes(_path, [4, 5, 6, 7]);
        var replacement = ImageCacheKey.Create(_path, false, 2400);
        File.Delete(_path);
        Assert.False(replacement.MatchesCurrentSource());
    }

    [Fact(DisplayName = "Decoded cache identity separates resize and Original quality")]
    public void DecodedCacheIdentitySeparatesResizeAndOriginalQuality()
    {
        var previewKey = ImageCacheKey.Create(_path, isOriginal: false, targetWidth: 2048);
        var resizedKey = ImageCacheKey.Create(_path, isOriginal: false, targetWidth: 1024);
        var originalKey = ImageCacheKey.Create(_path, isOriginal: true, targetWidth: 2048);
        Assert.True(previewKey != resizedKey && previewKey != originalKey && previewKey.MatchesCurrentSource());
    }

    [Fact(DisplayName = "Replacing a source at the same path invalidates its decoded bitmap")]
    public void ReplacingSourceAtSamePathInvalidatesDecodedBitmap()
    {
        var previewKey = ImageCacheKey.Create(_path, isOriginal: false, targetWidth: 2048);
        File.WriteAllBytes(_path, [1, 2, 3, 4]);
        var changedKey = ImageCacheKey.Create(_path, isOriginal: false, targetWidth: 2048);
        Assert.True(changedKey != previewKey && !previewKey.MatchesCurrentSource());
    }
}

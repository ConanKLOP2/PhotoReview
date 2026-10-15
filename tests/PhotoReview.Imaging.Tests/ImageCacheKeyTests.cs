using System.IO;

namespace PhotoReview.Imaging.Tests;

[Trait("Category", "HotPath")]
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
        Assert.True(preview.MatchesCurrentSource());
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

    [Fact(DisplayName = "Cache key carries the decode box: height is part of the identity")]
    public void CacheKey_BoxHeight_IsPartOfIdentity()
    {
        var box = ImageCacheKey.Create(_path, false, new DecodeBox(2304, 1280));
        var same = ImageCacheKey.Create(_path, false, new DecodeBox(2304, 1280));
        var taller = ImageCacheKey.Create(_path, false, new DecodeBox(2304, 1408));
        var widthOnly = ImageCacheKey.Create(_path, false, 2304);

        Assert.Equal(new DecodeBox(2304, 1280), box.TargetBox);
        Assert.Equal(1280, box.TargetHeight);
        Assert.True(box == same);
        Assert.True(box != taller);
        Assert.True(box != widthOnly);
        Assert.Equal(0, widthOnly.TargetHeight);
    }

    [Fact(DisplayName = "Original key ignores the box (always full size)")]
    public void CacheKey_Original_IgnoresBox()
    {
        var original = ImageCacheKey.Create(_path, true, new DecodeBox(2304, 1280));

        Assert.True(original.TargetBox.IsUnbounded);
        Assert.True(original == ImageCacheKey.Create(_path, true, 0));
        Assert.True(ImageCacheKey.CreateOriginal(ImageCacheKey.Create(_path, false, new DecodeBox(2304, 1280))) == original);
    }

    [Fact(DisplayName = "WithPath keeps the source identity, normalizes the path and never touches the disk")]
    public void WithPath_NewNameOnNonexistentFile_KeepsIdentityAndNormalizesPath()
    {
        var box = new DecodeBox(2304, 1280);
        var key = ImageCacheKey.Create(_path, false, box, orientationApplied: false, backend: PhotoReview.Core.Model.DecoderBackend.Wpf, sourceKind: ImageSourceKind.RawPreview);
        var newPath = _root.Combine("sub", "..", "does-not-exist-renamed.jpg");

        var renamed = key.WithPath(newPath);

        Assert.Equal(System.IO.Path.GetFullPath(newPath).ToUpperInvariant(), renamed.Path);
        Assert.Equal(key.Length, renamed.Length);
        Assert.Equal(key.LastWriteUtcTicks, renamed.LastWriteUtcTicks);
        Assert.Equal(key.IsOriginal, renamed.IsOriginal);
        Assert.Equal(box, renamed.TargetBox);
        Assert.Equal(key.OrientationApplied, renamed.OrientationApplied);
        Assert.Equal(key.Backend, renamed.Backend);
        Assert.Equal(key.SourceKind, renamed.SourceKind);
        Assert.NotEqual(key, renamed);
        Assert.False(File.Exists(renamed.Path));
    }

    [Fact(DisplayName = "WithPath on an Original key stays Original and CreateOriginal(newKey) keeps the new path")]
    public void WithPath_OriginalKey_CreateOriginalOfNewKeyHasNewPath()
    {
        var preview = ImageCacheKey.Create(_path, false, 2400);
        var newPath = System.IO.Path.Combine(_root.Path, "renamed-original.jpg");

        var renamedPreview = preview.WithPath(newPath);
        var original = ImageCacheKey.CreateOriginal(renamedPreview);
        var renamedOriginal = ImageCacheKey.CreateOriginal(preview).WithPath(newPath);

        Assert.Equal(System.IO.Path.GetFullPath(newPath).ToUpperInvariant(), original.Path);
        Assert.True(original.IsOriginal);
        Assert.Equal(original, renamedOriginal);
        Assert.NotEqual(ImageCacheKey.CreateOriginal(preview), original);
    }

    [Fact(DisplayName = "WithPath rejects a blank path")]
    public void WithPath_BlankPath_Throws()
    {
        var key = ImageCacheKey.Create(_path, false, 2400);
        Assert.ThrowsAny<ArgumentException>(() => key.WithPath(" "));
    }
}

using PhotoReview.Core.Catalog;

namespace PhotoReview.Core.Tests.Catalog;

[Trait("Category", "HotPath")]
public class DragDropInputServiceTests
{
    [Fact]
    public void ParseEmptyOrNullReturnsInvalid()
    {
        var resultNull = DragDropInputService.Parse(null);
        Assert.False(resultNull.IsValid);
        Assert.Equal(DragDropInputKind.Invalid, resultNull.Kind);

        var resultEmpty = DragDropInputService.Parse([]);
        Assert.False(resultEmpty.IsValid);
    }

    [Fact]
    public void ParseExistingDirectoryReturnsFolderKind()
    {
        var tempDir = Path.GetTempPath();
        var result = DragDropInputService.Parse([tempDir]);

        Assert.True(result.IsValid);
        Assert.Equal(DragDropInputKind.Folder, result.Kind);
        Assert.Equal(Path.GetFullPath(tempDir), result.FolderPath);
    }

    [Fact(DisplayName = "Drag-drop image selects initial image")]
    public void ParseImageFileReturnsImageKindWithInitialImage()
    {
        using var root = new TempRoot("dragdrop");
        var folder = root.Dir("drag-folder");
        var image = root.File(Path.Combine("drag-folder", "first.JPG"), 1);

        var parsed = DragDropInputService.Parse([image]);

        Assert.Equal(DragDropInputKind.Image, parsed.Kind);
        Assert.Equal(folder, parsed.FolderPath);
        Assert.Equal(image, parsed.InitialImagePath);
    }

    [Theory(DisplayName = "Q-FMT-WEBP-HEIC: a dropped .heic opens only while the WebP/HEIC switch is on")]
    [InlineData(true)]
    [InlineData(false)]
    public void ParseHeicFile_FollowsTheWebpHeicSwitch(bool enabled)
    {
        using var root = new TempRoot("dragdrop");
        root.Dir("iphone");
        var image = root.File(Path.Combine("iphone", "IMG_0001.HEIC"), 1);

        var parsed = DragDropInputService.Parse([image], rawEnabled: false, webpHeicEnabled: enabled);

        Assert.Equal(enabled, parsed.IsValid);
        Assert.Equal(enabled ? image : null, parsed.InitialImagePath);
    }

    [Fact(DisplayName = "Drag-drop rejects unsupported input")]
    public void ParseUnsupportedFileIsInvalid()
    {
        using var root = new TempRoot("dragdrop");

        Assert.False(DragDropInputService.Parse([root.Combine("notes.txt")]).IsValid);
    }

    [Fact(DisplayName = "W2-CC-06: the same folder with and without a trailing separator is one folder, not an ignored extra")]
    public void ParseSameFolderWithTrailingSeparator_IsNotIgnored()
    {
        using var root = new TempRoot("dragdrop");
        var folder = root.Dir("same-folder");

        var parsed = DragDropInputService.Parse([folder, folder + Path.DirectorySeparatorChar, folder.ToUpperInvariant()]);

        Assert.Equal(DragDropInputKind.Folder, parsed.Kind);
        Assert.Equal(0, parsed.IgnoredPathCount);
        Assert.Null(parsed.Warning);
    }
}

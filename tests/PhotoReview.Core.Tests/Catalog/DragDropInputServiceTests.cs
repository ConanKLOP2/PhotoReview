using PhotoReview.Core.Catalog;
using Xunit;

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

    [Fact(DisplayName = "Drag-drop rejects unsupported input")]
    public void ParseUnsupportedFileIsInvalid()
    {
        using var root = new TempRoot("dragdrop");

        Assert.False(DragDropInputService.Parse([root.Combine("notes.txt")]).IsValid);
    }
}


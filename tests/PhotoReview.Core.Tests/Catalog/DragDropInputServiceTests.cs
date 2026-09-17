using PhotoReview.Core.Catalog;
using Xunit;

namespace PhotoReview.Core.Tests.Catalog;

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
}

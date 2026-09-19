using System.IO;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Diagnostics;

namespace PhotoReview.Core.Tests.Services;

/// <summary>Drag-and-drop input parsing.</summary>
public sealed class DragDropInputServiceTests : IDisposable
{
    private readonly TempRoot _root = new("dragdrop");
    private readonly string _dragFolder;
    private readonly string _dragImage;

    public DragDropInputServiceTests()
    {
        _dragFolder = _root.Dir("drag-folder");
        _dragImage = _root.File(Path.Combine("drag-folder", "first.JPG"), 1);
    }

    public void Dispose() => _root.Dispose();

    [Fact(DisplayName = "Drag-drop folder parser")]
    public void DragDropFolderParser()
    {
        var parsed = DragDropInputService.Parse([_dragFolder]);
        Assert.True(parsed.Kind == DragDropInputKind.Folder && parsed.FolderPath == _dragFolder);
    }

    [Fact(DisplayName = "Drag-drop image selects initial image")]
    public void DragDropImageSelectsInitialImage()
    {
        var parsed = DragDropInputService.Parse([_dragImage]);
        Assert.True(parsed.Kind == DragDropInputKind.Image && parsed.FolderPath == _dragFolder
            && parsed.InitialImagePath == _dragImage);
    }

    [Fact(DisplayName = "Drag-drop rejects unsupported input")]
    public void DragDropRejectsUnsupportedInput() =>
        Assert.False(DragDropInputService.Parse([_root.Combine("notes.txt")]).IsValid);
}


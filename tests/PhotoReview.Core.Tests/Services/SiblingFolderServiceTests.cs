using System.IO;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Diagnostics;

namespace PhotoReview.Core.Tests.Services;

/// <summary>Sibling folder navigation.</summary>
public sealed class SiblingFolderServiceTests : IDisposable
{
    private readonly TempRoot _root = new("siblings");

    public void Dispose() => _root.Dispose();

    [Fact(DisplayName = "Sibling folder navigation uses natural order")]
    public void SiblingFolderNavigationUsesNaturalOrder()
    {
        var siblingRoot = _root.Dir("folders");
        var folder1 = Directory.CreateDirectory(Path.Combine(siblingRoot, "Folder1")).FullName;
        var folder2 = Directory.CreateDirectory(Path.Combine(siblingRoot, "Folder2")).FullName;
        var folder10 = Directory.CreateDirectory(Path.Combine(siblingRoot, "Folder10")).FullName;
        var siblings = SiblingFolderService.GetSorted(folder10);
        Assert.True(siblings.SequenceEqual(new[] { folder1, folder2, folder10 }, StringComparer.OrdinalIgnoreCase)
            && SiblingFolderService.GetTarget(folder2, 1) == folder10
            && SiblingFolderService.GetTarget(folder2, -1) == folder1);
    }
}


using PhotoReview.Core.Catalog;

namespace PhotoReview.Core.Tests.Catalog;

[Trait("Category", "HotPath")]
public class SiblingFolderServiceTests : IDisposable
{
    private readonly string _root;
    private readonly string _folderA;
    private readonly string _folderB;
    private readonly string _folderC;

    public SiblingFolderServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "PhotoReview-SiblingTest-" + Guid.NewGuid().ToString("N"));
        _folderA = Path.Combine(_root, "Album 1");
        _folderB = Path.Combine(_root, "Album 2");
        _folderC = Path.Combine(_root, "Album 10");

        Directory.CreateDirectory(_folderA);
        Directory.CreateDirectory(_folderB);
        Directory.CreateDirectory(_folderC);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                foreach (var dir in new DirectoryInfo(_root).EnumerateDirectories())
                {
                    dir.Attributes = FileAttributes.Directory;
                }

                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
            // Best effort
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void GetSortedReturnsSiblingsInNaturalOrder()
    {
        var sorted = SiblingFolderService.GetSorted(_folderA);
        var names = sorted.Select(Path.GetFileName).ToList();

        Assert.Equal(["Album 1", "Album 2", "Album 10"], names);
    }

    [Fact]
    public void GetSorted_HiddenAndSystemSiblings_AreNotReturned()
    {
        var hidden = Directory.CreateDirectory(Path.Combine(_root, ".thumbnails"));
        var system = Directory.CreateDirectory(Path.Combine(_root, "Album 3 system"));
        hidden.Attributes |= FileAttributes.Hidden;
        system.Attributes |= FileAttributes.System;

        var names = SiblingFolderService.GetSorted(_folderA).Select(Path.GetFileName).ToList();

        Assert.Equal(["Album 1", "Album 2", "Album 10"], names);
    }

    [Fact]
    public void GetSorted_CurrentFolderIsHidden_StillReturnedAmongVisibleSiblings()
    {
        var hidden = Directory.CreateDirectory(Path.Combine(_root, "Album 5 hidden"));
        hidden.Attributes |= FileAttributes.Hidden;
        var other = Directory.CreateDirectory(Path.Combine(_root, "Album 6 hidden"));
        other.Attributes |= FileAttributes.Hidden;

        var names = SiblingFolderService.GetSorted(hidden.FullName).Select(Path.GetFileName).ToList();

        Assert.Equal(["Album 1", "Album 2", "Album 5 hidden", "Album 10"], names);
    }

    // GetSorted_EnumerationFailure_ReturnsEmptyAndLogs: skipped, SiblingFolderService has no IFileSystem seam
    // (direct Directory.* call, see core-filesystem-boundary-allowlist.txt), so a failure cannot be injected.

}

using PhotoReview.Core.Catalog;
using Xunit;

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
    public void GetTargetNavigatesNextAndPrevious()
    {
        var next = SiblingFolderService.GetTarget(_folderA, 1);
        Assert.Equal(_folderB, next);

        var prev = SiblingFolderService.GetTarget(_folderB, -1);
        Assert.Equal(_folderA, prev);

        var end = SiblingFolderService.GetTarget(_folderC, 1);
        Assert.Null(end);
    }

    [Fact]
    public void GetTarget_FolderPathWithTrailingSeparator_StillFindsSiblings()
    {
        var target = SiblingFolderService.GetTarget(_folderA + Path.DirectorySeparatorChar, 1);
        Assert.Equal(_folderB, target);
    }
}

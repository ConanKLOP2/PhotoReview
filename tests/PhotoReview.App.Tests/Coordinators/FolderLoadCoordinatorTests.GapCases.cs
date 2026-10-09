using System.IO;
using PhotoReview.App.Coordinators;
using PhotoReview.App.Tests.ViewModels;
using PhotoReview.Core.Abstractions;

namespace PhotoReview.App.Tests.Coordinators;

/// <summary>RV-T25: initialPath edge cases, a drive-root folder and an empty folder whose subfolder listing throws.</summary>
public sealed partial class FolderLoadCoordinatorTests
{
    [Fact]
    public async Task LoadAsync_InitialPathNotInFolder_KeepsTheScanOrderAndPresentsIndexZero()
    {
        var (a, b, c) = CreateThreeImages(@"C:\photos");

        using var coordinator = CreateCoordinator();
        await coordinator.LoadAsync(@"C:\photos", initialPath: @"C:\photos\ghost.jpg");

        Assert.Equal([a, b, c], _catalog.Paths);
        Assert.Equal(0, Assert.Single(_sink.Presented).Index);
    }

    [Fact]
    public async Task LoadAsync_InitialPathAlreadyFirst_LeavesTheOrderUntouchedAndPresentsIt()
    {
        var (a, b, c) = CreateThreeImages(@"C:\photos");

        using var coordinator = CreateCoordinator();
        await coordinator.LoadAsync(@"C:\photos", initialPath: a);

        Assert.Equal([a, b, c], _catalog.Paths);
        Assert.Equal(a, _catalog.Current?.Path);
        Assert.Equal(0, Assert.Single(_sink.Presented).Index);
    }

    [Fact]
    public async Task LoadAsync_InitialPathInTheMiddleDifferentCase_IsPresentedWhereItSits()
    {
        var (a, b, c) = CreateThreeImages(@"C:\photos");

        using var coordinator = CreateCoordinator();
        await coordinator.LoadAsync(@"C:\photos", initialPath: b.ToUpperInvariant());

        Assert.Equal([a, b, c], _catalog.Paths);
        Assert.Equal(b, _catalog.Current?.Path);
        Assert.Equal(1, Assert.Single(_sink.Presented).Index);
    }

    [Fact]
    public async Task LoadAsync_DriveRootFolder_LoadsItsImages()
    {
        var root = @"C:\";
        _fs.CreateDirectory(root);
        var one = @"C:\root1.jpg";
        var two = @"C:\root2.jpg";
        _fs.WriteAllTextAtomic(one, "1");
        _fs.WriteAllTextAtomic(two, "2");

        using var coordinator = CreateCoordinator();
        await coordinator.LoadAsync(root);

        Assert.Empty(_sink.Failures);
        Assert.Equal([one, two], _catalog.Paths);
        Assert.Equal(1, _sink.CatalogReadyCount);
        Assert.Equal(0, Assert.Single(_sink.Presented).Index);
    }

    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    public async Task LoadAsync_EmptyFolderWhoseSubfolderListingThrows_ReportsPlainEmptyNotAFailure(Type exceptionType)
    {
        _fs.CreateDirectory(@"C:\emptyfolder");
        var throwing = new ThrowingDirectoriesFileSystem(_fs, (Exception)Activator.CreateInstance(exceptionType, "listing failed")!);
        using var coordinator = new FolderLoadCoordinator(_catalog, _genClock, _explorerOrder, throwing, _sessionStore, _settingsStore, _sink);

        await coordinator.LoadAsync(@"C:\emptyfolder");

        Assert.Empty(_sink.Failures);
        Assert.Equal(1, _sink.EmptyCount);
        Assert.Empty(_sink.EmptyWithSubfoldersCounts);
        Assert.Equal(1, throwing.ListCalls);
    }

    private sealed class ThrowingDirectoriesFileSystem(IFileSystem inner, Exception toThrow) : MainViewModelFileActionTests.DelegatingFileSystem(inner)
    {
        public int ListCalls { get; private set; }

        public override System.Collections.Generic.IEnumerable<string> EnumerateDirectories(string directory)
        {
            ListCalls++;
            throw toThrow;
        }
    }
}

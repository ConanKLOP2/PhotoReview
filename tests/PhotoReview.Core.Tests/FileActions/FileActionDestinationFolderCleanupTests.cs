using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Model;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.FileActions;

/// <summary>
/// ADR 0007 / data safety: the destination folder is created BEFORE the journal's Prepared line is written. When that line cannot
/// be written nothing was moved or copied, so a folder this very call created (and that is still empty) must not be left behind;
/// a folder that already existed, or that has content, is never removed, and a failure AFTER Prepared keeps its existing behaviour.
/// Fakes only (in-memory disk and journal); the real Recycle Bin is never touched.
/// </summary>
public sealed class FileActionDestinationFolderCleanupTests
{
    private static readonly DateTime Stamp = new(2026, 9, 29, 0, 0, 0, DateTimeKind.Utc);
    private const string Jpeg = @"C:\photos\a.jpg";
    private const string Raw = @"C:\photos\a.cr2";
    private const string Folder = @"C:\photos\selected";
    private const string Other = @"C:\photos\selected\existing.txt";

    private sealed class FixedClock : IClock
    {
        public DateTime UtcNow => Stamp;
    }

    private sealed class Bin : IRecycleBin
    {
        public bool CanRecycle(string path) => true;
        public void SendToRecycleBin(string path) => throw new InvalidOperationException("the real bin must not be used");
        public void DeletePermanently(string path) => throw new InvalidOperationException("the real bin must not be used");
        public bool TryRestore(string originalPath, long expectedSize, DateTime expectedLastWriteUtc) => false;
    }

    private sealed class World
    {
        public InMemoryFileSystem Disk { get; } = new();
        public OperationJournal Journal { get; }
        public FileActionService Service { get; }

        public World()
        {
            Disk.AddFile(Jpeg, "jpeg", Stamp);
            Disk.AddFile(Raw, "raw data", Stamp);
            Journal = new OperationJournal(new AppPaths(@"C:\Users\test\AppData\Local"), Disk, new FixedClock());
            Service = new FileActionService(Journal, Disk, new FixedClock(), new Bin());
        }

        /// <summary>The first journal append (the Prepared line) fails.</summary>
        public void FailPreparedAppend()
        {
            var appends = 0;
            Disk.OpenAppendHook = _ => ++appends == 1 ? new IOException("journal unavailable") : null;
        }

        public void AssertSourcesIntact()
        {
            Assert.True(Disk.FileExists(Jpeg));
            Assert.True(Disk.FileExists(Raw));
            Assert.False(Disk.FileExists(Folder + @"\a.jpg"));
            Assert.False(Disk.FileExists(Folder + @"\a.cr2"));
        }
    }

    private static CaptureGroupActionRequest GroupRequest(FileOperationType operation, string destination = "selected") =>
        new(new CaptureGroup(Jpeg, Raw), operation, destination, false);

    private static FileActionRequest SingleRequest(FileOperationType operation) =>
        new(Jpeg, operation, "selected");

    [Theory]
    [InlineData(FileOperationType.Move)]
    [InlineData(FileOperationType.Copy)]
    public async Task Group_JournalFailsAtBegin_DestinationFolderCreatedByThisCall_IsRemoved(FileOperationType operation)
    {
        var world = new World();
        world.FailPreparedAppend();

        var result = await world.Service.ExecuteGroupAsync(GroupRequest(operation));

        Assert.False(result.Succeeded);
        Assert.False(world.Disk.DirectoryExists(Folder));
        world.AssertSourcesIntact();
        Assert.Empty(world.Journal.ReadPendingAndFailedOperations());
    }

    [Theory]
    [InlineData(FileOperationType.Move)]
    [InlineData(FileOperationType.Copy)]
    public async Task Single_JournalFailsAtBegin_DestinationFolderCreatedByThisCall_IsRemoved(FileOperationType operation)
    {
        var world = new World();
        world.FailPreparedAppend();

        var result = await world.Service.ExecuteAsync(SingleRequest(operation));

        Assert.False(result.Succeeded);
        Assert.Equal("journal unavailable", result.Error); // the original failure is reported, not hidden by the cleanup
        Assert.False(world.Disk.DirectoryExists(Folder));
        world.AssertSourcesIntact();
        Assert.Empty(world.Journal.ReadPendingAndFailedOperations());
    }

    [Fact]
    public async Task Group_JournalFailsAtBegin_MissingParentsCreatedByThisCall_AreRemovedToo()
    {
        var world = new World();
        world.FailPreparedAppend();

        var result = await world.Service.ExecuteGroupAsync(GroupRequest(FileOperationType.Move, @"x\y"));

        Assert.False(result.Succeeded);
        Assert.False(world.Disk.DirectoryExists(@"C:\photos\x\y"));
        Assert.False(world.Disk.DirectoryExists(@"C:\photos\x"));
        Assert.True(world.Disk.DirectoryExists(@"C:\photos"));
    }

    [Fact]
    public async Task Group_JournalFailsAtBegin_PreExistingEmptyDestinationFolder_IsKept()
    {
        var world = new World();
        world.Disk.CreateDirectory(Folder);
        world.FailPreparedAppend();

        var result = await world.Service.ExecuteGroupAsync(GroupRequest(FileOperationType.Move));

        Assert.False(result.Succeeded);
        Assert.True(world.Disk.DirectoryExists(Folder));
        world.AssertSourcesIntact();
    }

    [Fact]
    public async Task Single_JournalFailsAtBegin_PreExistingEmptyDestinationFolder_IsKept()
    {
        var world = new World();
        world.Disk.CreateDirectory(Folder);
        world.FailPreparedAppend();

        var result = await world.Service.ExecuteAsync(SingleRequest(FileOperationType.Move));

        Assert.False(result.Succeeded);
        Assert.True(world.Disk.DirectoryExists(Folder));
        world.AssertSourcesIntact();
    }

    [Fact]
    public async Task Group_JournalFailsAtBegin_PreExistingDestinationFolderWithFiles_IsKeptWithItsFiles()
    {
        var world = new World();
        world.Disk.AddFile(Other, "keep me", Stamp);
        world.FailPreparedAppend();

        var result = await world.Service.ExecuteGroupAsync(GroupRequest(FileOperationType.Copy));

        Assert.False(result.Succeeded);
        Assert.True(world.Disk.DirectoryExists(Folder));
        Assert.True(world.Disk.FileExists(Other));
        world.AssertSourcesIntact();
    }

    [Fact]
    public async Task Single_JournalFailsAtBegin_PreExistingDestinationFolderWithFiles_IsKeptWithItsFiles()
    {
        var world = new World();
        world.Disk.AddFile(Other, "keep me", Stamp);
        world.FailPreparedAppend();

        var result = await world.Service.ExecuteAsync(SingleRequest(FileOperationType.Copy));

        Assert.False(result.Succeeded);
        Assert.True(world.Disk.DirectoryExists(Folder));
        Assert.True(world.Disk.FileExists(Other));
        world.AssertSourcesIntact();
    }

    [Fact]
    public async Task Group_FailureAfterPrepared_KeepsTheDestinationFolderLikeBefore()
    {
        var world = new World();
        world.Disk.MoveHook = (_, _) => new IOException("move failed"); // Begin succeeded, the first Move throws

        var result = await world.Service.ExecuteGroupAsync(GroupRequest(FileOperationType.Move));

        Assert.False(result.Succeeded);
        Assert.True(world.Disk.DirectoryExists(Folder));
        world.AssertSourcesIntact();
    }

    [Fact]
    public async Task Single_FailureAfterPrepared_KeepsTheDestinationFolderLikeBefore()
    {
        var world = new World();
        world.Disk.MoveHook = (_, _) => new IOException("move failed");

        var result = await world.Service.ExecuteAsync(SingleRequest(FileOperationType.Move));

        Assert.False(result.Succeeded);
        Assert.True(world.Disk.DirectoryExists(Folder));
        world.AssertSourcesIntact();
    }

    [Fact]
    public async Task Single_JournalFailsAtBegin_CleanupThatThrows_DoesNotHideTheOriginalFailure()
    {
        var world = new World();
        world.Disk.DeleteDirectoryHook = _ => new IOException("cannot clean up");
        world.FailPreparedAppend();

        var result = await world.Service.ExecuteAsync(SingleRequest(FileOperationType.Move));

        Assert.False(result.Succeeded);
        Assert.Equal("journal unavailable", result.Error);
        world.AssertSourcesIntact();
    }

    [Fact]
    public async Task Group_JournalFailsAtBegin_CleanupThatThrows_DoesNotHideTheOriginalFailure()
    {
        var world = new World();
        world.Disk.DeleteDirectoryHook = _ => new IOException("cannot clean up");
        world.FailPreparedAppend();

        var result = await world.Service.ExecuteGroupAsync(GroupRequest(FileOperationType.Move));

        Assert.False(result.Succeeded);
        Assert.Contains("journal unavailable", result.Error);
        world.AssertSourcesIntact();
    }
}

using PhotoReview.Core;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.FileActions;
using PhotoReview.Core.Model;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.FileActions;

public sealed class CaptureGroupActionServiceTests
{
    private static readonly DateTime Stamp = new(2026, 9, 29, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task ExecuteGroupAsync_PreflightsEverySourceBeforeJournalOrMutation()
    {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllTextAtomic(@"C:\photos\a.jpg", "jpeg");
        var journal = new OperationJournal(new AppPaths(@"C:\Users\test\AppData\Local"), fileSystem, new FixedClock());
        var service = CreateService(fileSystem, journal);

        var result = await service.ExecuteGroupAsync(new CaptureGroupActionRequest(
            new CaptureGroup(@"C:\photos\a.jpg", @"C:\photos\a.cr2"), FileOperationType.Move, "selected"));

        Assert.False(result.Succeeded);
        Assert.Empty(result.Members);
        Assert.True(fileSystem.FileExists(@"C:\photos\a.jpg"));
        Assert.False(fileSystem.FileExists(@"C:\photos\selected\a.jpg"));
        Assert.Empty(journal.ReadPendingAndFailedOperations());
        Assert.DoesNotContain(fileSystem.Events, item => item.Kind is "move" or "copy");
    }

    [Fact]
    public async Task ExecuteGroupAsync_PreparesManifestBeforeMovingAndReportsPartialFailure()
    {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.WriteAllTextAtomic(@"C:\photos\a.jpg", "jpeg");
        fileSystem.WriteAllTextAtomic(@"C:\photos\a.cr2", "raw data");
        var journal = new OperationJournal(new AppPaths(@"C:\Users\test\AppData\Local"), fileSystem, new FixedClock());
        var sawCompletePreparedManifest = false;
        var service = CreateService(fileSystem, journal, async (source, destination) =>
        {
            var prepared = Assert.Single(journal.ReadPendingOperations());
            Assert.Equal(2, prepared.GroupMembers!.Count);
            sawCompletePreparedManifest = true;
            if (source.EndsWith(".cr2", StringComparison.OrdinalIgnoreCase)) throw new IOException("simulated second-member failure");
            await Task.Run(() => fileSystem.Move(source, destination));
        });

        var result = await service.ExecuteGroupAsync(new CaptureGroupActionRequest(
            new CaptureGroup(@"C:\photos\a.jpg", @"C:\photos\a.cr2"), FileOperationType.Move, "selected"));

        Assert.False(result.Succeeded);
        Assert.True(sawCompletePreparedManifest);
        Assert.True(result.Members[0].Completed);
        Assert.False(result.Members[1].Completed);
        Assert.False(result.Members[1].Conflict);
        Assert.False(fileSystem.FileExists(@"C:\photos\a.jpg"));
        Assert.True(fileSystem.FileExists(@"C:\photos\a.cr2"));
        var failed = Assert.Single(journal.ReadFailedOperations());
        Assert.Equal(result.GroupId, failed.GroupId);
        Assert.Equal(2, failed.GroupMembers!.Count);
    }

    private static FileActionService CreateService(
        InMemoryFileSystem fileSystem,
        OperationJournal journal,
        Func<string, string, Task>? moveOverride = null) =>
        new(journal, fileSystem, new FixedClock(), new FakeRecycleBin(), moveOverride);

    private sealed class FixedClock : IClock
    {
        public DateTime UtcNow => Stamp;
    }

    private sealed class FakeRecycleBin : IRecycleBin
    {
        public void SendToRecycleBin(string path) => throw new NotSupportedException();
        public bool TryRestore(string originalPath, long expectedSize, DateTime expectedLastWriteUtc) => false;
    }
}

using PhotoReview.Core;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.FileActions;
using PhotoReview.Core.Model;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.FileActions;

/// <summary>Compensation, cumulative Recycle Bin capacity and stale-group behavior of <see cref="FileActionService.ExecuteGroupAsync"/>.</summary>
public sealed class CaptureGroupActionRollbackTests
{
    private static readonly DateTime Stamp = new(2026, 9, 29, 0, 0, 0, DateTimeKind.Utc);
    private const string Jpeg = @"C:\photos\a.jpg";
    private const string Raw = @"C:\photos\a.cr2";
    private const string MovedJpeg = @"C:\photos\selected\a.jpg";
    private const string MovedRaw = @"C:\photos\selected\a.cr2";

    private static (InMemoryFileSystem Fs, OperationJournal Journal) CreateWorld()
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile(Jpeg, "jpeg", Stamp);
        fs.AddFile(Raw, "raw data", Stamp);
        var journal = new OperationJournal(new AppPaths(@"C:\Users\test\AppData\Local"), fs, new FixedClock());
        return (fs, journal);
    }

    private static FileActionService CreateService(InMemoryFileSystem fs, OperationJournal journal, IRecycleBin? bin = null) =>
        new(journal, fs, new FixedClock(), bin ?? new CapacityBin(fs, long.MaxValue));

    private static CaptureGroupActionRequest Request(FileOperationType operation, string? destination = "selected") =>
        new(new CaptureGroup(Jpeg, Raw), operation, destination);

    [Fact]
    public async Task ExecuteGroupAsync_MoveSecondMemberFails_RollsFirstMemberBackAndJournalsFailure()
    {
        var (fs, journal) = CreateWorld();
        fs.MoveHook = (source, _) => source == Raw ? new IOException("simulated second-member failure") : null;

        var result = await CreateService(fs, journal).ExecuteGroupAsync(Request(FileOperationType.Move));

        Assert.False(result.Succeeded);
        Assert.True(fs.FileExists(Jpeg));
        Assert.True(fs.FileExists(Raw));
        Assert.False(fs.FileExists(MovedJpeg));
        Assert.False(fs.FileExists(MovedRaw));
        Assert.All(result.Members, member =>
        {
            Assert.False(member.Completed);
            Assert.True(member.SourceExists);
            Assert.False(member.DestinationExists);
        });
        var failed = Assert.Single(journal.ReadFailedOperations());
        Assert.Equal(result.GroupId, failed.GroupId);
        Assert.Equal("simulated second-member failure", failed.Error);
        Assert.Empty(journal.ReadPendingOperations());
    }

    [Fact]
    public async Task ExecuteGroupAsync_MoveFailsAfterFirstMember_RollbackDoesNotUseTheForwardMoveOverride()
    {
        var (fs, journal) = CreateWorld();
        var overrideCalls = new List<string>();
        var service = new FileActionService(journal, fs, new FixedClock(), new CapacityBin(fs, long.MaxValue), (source, destination) =>
        {
            overrideCalls.Add(source);
            if (source == Raw) throw new IOException("simulated second-member failure");
            fs.Move(source, destination);
            return Task.CompletedTask;
        });

        await service.ExecuteGroupAsync(Request(FileOperationType.Move));

        Assert.Equal([Jpeg, Raw], overrideCalls); // the rollback is a plain file-system move, not a second override call
        Assert.True(fs.FileExists(Jpeg));
    }

    [Fact]
    public async Task ExecuteGroupAsync_MoveRollbackBlockedByChangedDestination_KeepsMovedFileAndReportsIt()
    {
        var (fs, journal) = CreateWorld();
        fs.MoveHook = (source, _) =>
        {
            if (source != Raw) return null;
            fs.AddFile(MovedJpeg, "edited elsewhere, different length", Stamp); // the moved JPEG changed meanwhile
            return new IOException("simulated second-member failure");
        };

        var result = await CreateService(fs, journal).ExecuteGroupAsync(Request(FileOperationType.Move));

        Assert.False(result.Succeeded);
        Assert.False(fs.FileExists(Jpeg)); // NOT moved back over an unknown file's identity
        Assert.True(fs.FileExists(MovedJpeg));
        Assert.True(fs.FileExists(Raw));
        var failed = Assert.Single(journal.ReadFailedOperations());
        Assert.Contains("simulated second-member failure", failed.Error, StringComparison.Ordinal);
        Assert.Contains("1", failed.Error, StringComparison.Ordinal);
        Assert.Contains(result.Members, member => member.Conflict);
    }

    [Fact]
    public async Task ExecuteGroupAsync_MoveRollback_NeverOverwritesFileThatAppearedAtSource()
    {
        var (fs, journal) = CreateWorld();
        fs.MoveHook = (source, _) =>
        {
            if (source != Raw) return null;
            fs.AddFile(Jpeg, "someone else's new a.jpg", Stamp);
            return new IOException("simulated second-member failure");
        };

        var result = await CreateService(fs, journal).ExecuteGroupAsync(Request(FileOperationType.Move));

        Assert.False(result.Succeeded);
        using var stream = fs.OpenReadShared(Jpeg);
        using var reader = new StreamReader(stream);
        Assert.Equal("someone else's new a.jpg", await reader.ReadToEndAsync());
        Assert.True(fs.FileExists(MovedJpeg));
        Assert.Single(journal.ReadFailedOperations());
    }

    [Fact]
    public async Task ExecuteGroupAsync_MoveCancelledAfterFirstMember_RollsBack()
    {
        var (fs, journal) = CreateWorld();
        using var cts = new CancellationTokenSource();
        var service = new FileActionService(journal, fs, new FixedClock(), new CapacityBin(fs, long.MaxValue), (source, destination) =>
        {
            fs.Move(source, destination);
            cts.Cancel(); // cancel while the first member is done
            return Task.CompletedTask;
        });

        var result = await service.ExecuteGroupAsync(Request(FileOperationType.Move), cts.Token);

        Assert.False(result.Succeeded);
        Assert.True(fs.FileExists(Jpeg));
        Assert.True(fs.FileExists(Raw));
        Assert.False(fs.FileExists(MovedJpeg));
        Assert.Single(journal.ReadFailedOperations());
    }

    [Fact]
    public async Task ExecuteGroupAsync_CopySecondMemberFails_DeletesOnlyTheCopiesItCreatedAndAllowsRerun()
    {
        var (fs, journal) = CreateWorld();
        fs.CopyHook = (source, _) => source == Raw ? new IOException("simulated copy failure") : null;
        var service = CreateService(fs, journal);

        var failed = await service.ExecuteGroupAsync(Request(FileOperationType.Copy));

        Assert.False(failed.Succeeded);
        Assert.False(fs.FileExists(MovedJpeg));
        Assert.False(fs.FileExists(MovedRaw));
        Assert.True(fs.FileExists(Jpeg));
        Assert.True(fs.FileExists(Raw));

        fs.CopyHook = null;
        var rerun = await service.ExecuteGroupAsync(Request(FileOperationType.Copy));
        Assert.True(rerun.Succeeded, rerun.Error);
        Assert.True(fs.FileExists(MovedJpeg));
        Assert.True(fs.FileExists(MovedRaw));
    }

    [Fact]
    public async Task ExecuteGroupAsync_CopyLeavesPartialFile_PartialFileIsDeleted()
    {
        var (fs, journal) = CreateWorld();
        fs.CopyHook = (source, destination) =>
        {
            if (source != Raw) return null;
            fs.AddFile(destination, "par", Stamp); // a half-written copy
            return new IOException("disk full");
        };

        var result = await CreateService(fs, journal).ExecuteGroupAsync(Request(FileOperationType.Copy));

        Assert.False(result.Succeeded);
        Assert.False(fs.FileExists(MovedRaw));
        Assert.False(fs.FileExists(MovedJpeg));
    }

    [Fact]
    public async Task ExecuteGroupAsync_CopyDestinationAppearsAfterPreflight_ForeignFileIsNeverDeleted()
    {
        var (fs, journal) = CreateWorld();
        fs.CopyHook = (source, _) =>
        {
            if (source == Jpeg) fs.AddFile(MovedRaw, "foreign file", Stamp); // appears after preflight, before member 2
            return null;
        };

        var result = await CreateService(fs, journal).ExecuteGroupAsync(Request(FileOperationType.Copy));

        Assert.False(result.Succeeded);
        Assert.False(fs.FileExists(MovedJpeg)); // our own copy was compensated
        Assert.Equal(12, fs.GetFileStat(MovedRaw)!.Length); // "foreign file": untouched
    }

    [Fact]
    public async Task ExecuteGroupAsync_RecycleBinThatFitsEachMemberButNotTheGroup_IsRefusedBeforeAnyMutation()
    {
        var (fs, journal) = CreateWorld(); // 4 + 8 bytes
        var bin = new CapacityBin(fs, 10);

        var result = await CreateService(fs, journal, bin).ExecuteGroupAsync(Request(FileOperationType.Recycle, null));

        Assert.False(result.Succeeded);
        Assert.Empty(result.Members);
        Assert.Equal(0, bin.RecycleCalls);
        Assert.True(fs.FileExists(Jpeg));
        Assert.True(fs.FileExists(Raw));
        Assert.Empty(journal.ReadPendingAndFailedOperations());
    }

    [Fact]
    public async Task ExecuteGroupAsync_RecycleBinThatFitsTheWholeGroup_Succeeds()
    {
        var (fs, journal) = CreateWorld();
        var bin = new CapacityBin(fs, 12);

        var result = await CreateService(fs, journal, bin).ExecuteGroupAsync(Request(FileOperationType.Recycle, null));

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(2, bin.RecycleCalls);
    }

    [Theory]
    [InlineData(FileOperationType.Recycle)]
    [InlineData(FileOperationType.Move)]
    [InlineData(FileOperationType.Copy)]
    public async Task ExecuteGroupAsync_PartnerVanishedExternally_ActsOnRemainingMembersAndReportsTheMissingOne(FileOperationType operation)
    {
        var (fs, journal) = CreateWorld();
        fs.Delete(Raw);
        var bin = new CapacityBin(fs, long.MaxValue);
        var destination = operation == FileOperationType.Recycle ? null : "selected";

        var result = await CreateService(fs, journal, bin).ExecuteGroupAsync(Request(operation, destination));

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal([Raw], result.SkippedMissing);
        var member = Assert.Single(result.Members);
        Assert.Equal(Jpeg, member.Member.Source);
        Assert.Equal(operation != FileOperationType.Copy, !fs.FileExists(Jpeg));
        Assert.Equal(operation == FileOperationType.Recycle ? 1 : 0, bin.RecycleCalls);
        Assert.Empty(journal.ReadPendingAndFailedOperations());
        if (operation == FileOperationType.Move)
            Assert.Equal([Jpeg], Assert.Single(journal.ReadCommittedMoves()).GroupMembers!.Select(m => m.Source));
    }

    [Fact]
    public async Task ExecuteGroupAsync_OnlySidecarLeft_IsRefusedAsSourceMissing()
    {
        var (fs, journal) = CreateWorld();
        fs.Delete(Jpeg);
        fs.Delete(Raw);
        fs.AddFile(@"C:\photos\a.xmp", "xmp", Stamp);
        var bin = new CapacityBin(fs, long.MaxValue);

        var result = await CreateService(fs, journal, bin).ExecuteGroupAsync(new CaptureGroupActionRequest(
            new CaptureGroup(Jpeg, Raw, @"C:\photos\a.xmp"), FileOperationType.Recycle));

        Assert.False(result.Succeeded);
        Assert.Equal(0, bin.RecycleCalls);
        Assert.True(fs.FileExists(@"C:\photos\a.xmp"));
    }

    private sealed class FixedClock : IClock
    {
        public DateTime UtcNow => Stamp;
    }

    /// <summary>Fake Recycle Bin (never the real one): accepts at most <c>capacity</c> bytes per call and removes the file from the in-memory disk.</summary>
    private sealed class CapacityBin(InMemoryFileSystem fs, long capacity) : IRecycleBin
    {
        public int RecycleCalls { get; private set; }
        public void SendToRecycleBin(string path)
        {
            RecycleCalls++;
            fs.Delete(path);
        }

        public bool TryRestore(string originalPath, long expectedSize, DateTime expectedLastWriteUtc) => false;
        public bool FitsInRecycleBin(string path, long fileSize) => fileSize <= capacity;
    }
}

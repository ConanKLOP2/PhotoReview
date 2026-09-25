using System.IO;
using System.Threading.Tasks;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.FileActions;
using PhotoReview.Core.Model;
using PhotoReview.Core.Tests.Fakes;
using Xunit;

namespace PhotoReview.Core.Tests.FileActions;

[Trait("Category", "HotPath")]
public sealed class FileActionServiceTests
{
    private sealed class FakeClock : IClock
    {
        public FakeClock(DateTime utcNow) => UtcNow = utcNow;
        public DateTime UtcNow { get; set; }
    }

    private sealed class FakeAppPaths : IAppPaths
    {
        public FakeAppPaths(string journalPath) => JournalFile = journalPath;
        public string ConfigFile => @"C:\data\config.json";
        public string JournalFile { get; }
        public string SessionsDir => @"C:\data\Sessions";
        public string LogFile => @"C:\data\logs\app.log";
        public string PreviewCacheDir => @"C:\data\cache";
        public string ThumbnailCacheDir => @"C:\data\thumbnails";
        public string WindowPlacementFile => @"C:\data\window-placement.json";
    }

    private sealed class FakeRecycleBin : IRecycleBin
    {
        public List<string> RecycledPaths { get; } = [];
        public Func<string, Exception?>? RecycleHook { get; set; }

        public void SendToRecycleBin(string path)
        {
            if (RecycleHook?.Invoke(path) is { } ex) throw ex;
            RecycledPaths.Add(path);
        }

        public bool TryRestore(string originalPath, long expectedSize, DateTime expectedLastWriteUtc) => true;
    }

    private readonly InMemoryFileSystem _fs = new();
    private readonly FakeClock _clock = new(new DateTime(2026, 9, 19, 10, 0, 0, DateTimeKind.Utc));
    private readonly OperationJournal _journal;
    private readonly FakeRecycleBin _recycleBin = new();
    private readonly FileActionService _service;

    public FileActionServiceTests()
    {
        _journal = new OperationJournal(new FakeAppPaths(@"C:\data\operations.jsonl"), _fs, _clock);
        _service = new FileActionService(_journal, _fs, _clock, _recycleBin);
    }

    [Theory]
    [InlineData(FileOperationType.Move)]
    [InlineData(FileOperationType.Copy)]
    public async Task ExecuteAsync_MissingSource_DoesNotCreateDestinationFolder(FileOperationType operation)
    {
        var result = await _service.ExecuteAsync(new FileActionRequest(@"C:\photos\gone.jpg", operation, "sel"));

        Assert.False(result.Succeeded);
        Assert.False(_fs.DirectoryExists(@"C:\photos\sel"));
    }

    [Theory]
    [InlineData(FileOperationType.Move)]
    [InlineData(FileOperationType.Copy)]
    public async Task ExecuteAsync_RelativeDestinationOutsideSource_FailsWithoutJournalOrMove(FileOperationType operation)
    {
        var source = @"C:\photos\a.jpg";
        _fs.WriteAllTextAtomic(source, "hello photo");

        var result = await _service.ExecuteAsync(new FileActionRequest(source, operation, @"..\outside"));

        Assert.False(result.Succeeded);
        Assert.Equal(Core.Localization.Tr.CoreFileActionDestinationOutsideSource, result.Error);
        Assert.True(_fs.FileExists(source));
        Assert.False(_fs.FileExists(@"C:\outside\a.jpg"));
        Assert.DoesNotContain(_fs.Events, e => e.Kind is "move" or "copy" or "append");
        Assert.False(_fs.FileExists(@"C:\data\operations.jsonl"));
        Assert.Empty(_journal.ReadFailedOperations());
    }

    [Theory]
    [InlineData(FileOperationType.Move)]
    [InlineData(FileOperationType.Copy)]
    public async Task ExecuteAsync_DriveRelativeDestination_FailsWithoutJournalOrMove(FileOperationType operation)
    {
        var source = @"C:\photos.jpg";
        _fs.WriteAllTextAtomic(source, "hello photo");

        var result = await _service.ExecuteAsync(new FileActionRequest(source, operation, "D:Backup"));

        Assert.False(result.Succeeded);
        Assert.Equal(Core.Localization.Tr.CoreFileActionDestinationOutsideSource, result.Error);
        Assert.True(_fs.FileExists(source));
        Assert.DoesNotContain(_fs.Events, e => e.Kind is "move" or "copy" or "append");
        Assert.False(_fs.FileExists(@"C:\data\operations.jsonl"));
    }

    [Theory(DisplayName = "A rooted destination without a drive (\\Loai-2) is refused at run time, nothing journaled or moved")]
    [InlineData(FileOperationType.Move, @"\Loai-2")]
    [InlineData(FileOperationType.Copy, @"\Loai-2")]
    [InlineData(FileOperationType.Move, "/Loai-2")]
    public async Task ExecuteAsync_RootedDestinationWithoutDrive_FailsWithoutJournalOrMove(FileOperationType operation, string destination)
    {
        var source = @"C:\photos\a.jpg";
        _fs.WriteAllTextAtomic(source, "hello photo");

        var result = await _service.ExecuteAsync(new FileActionRequest(source, operation, destination));

        Assert.False(result.Succeeded);
        Assert.Equal(Core.Localization.Tr.CoreFileActionDestinationOutsideSource, result.Error);
        Assert.True(_fs.FileExists(source));
        Assert.False(_fs.FileExists(@"C:\Loai-2\a.jpg"));
        Assert.DoesNotContain(_fs.Events, e => e.Kind is "move" or "copy" or "append");
        Assert.False(_fs.FileExists(@"C:\data\operations.jsonl"));
    }

    [Fact(DisplayName = "Cross-volume Move that copied but could not delete the source fails with MoveSourceNotRemoved and keeps both copies")]
    public async Task ExecuteAsync_MoveLeavesSource_FailsWithDistinctCodeAndKeepsBoth()
    {
        var source = @"C:\photos\a.jpg";
        _fs.WriteAllTextAtomic(source, "hello photo");
        _fs.MoveLeavesSource = true;

        var result = await _service.ExecuteAsync(new FileActionRequest(source, FileOperationType.Move, @"D:\Backup"));

        Assert.False(result.Succeeded);
        Assert.Equal(Core.Localization.Tr.CoreFileActionMoveSourceNotRemoved, result.Error);
        Assert.True(result.JournalPersisted);
        Assert.True(_fs.FileExists(source));
        Assert.True(_fs.FileExists(@"D:\Backup\a.jpg"));
        var failed = Assert.Single(_journal.ReadFailedOperations());
        Assert.Equal(JournalErrors.MoveSourceNotRemoved, failed.ErrorCode);
        Assert.Equal(JournalErrors.EnglishText(JournalErrors.MoveSourceNotRemoved), failed.Error);
        Assert.Empty(_journal.ReadCommittedMoves());
    }

    [Fact(DisplayName = "Copy is unaffected by the Move source check (source is expected to stay)")]
    public async Task ExecuteAsync_Copy_SourceStays_Succeeds()
    {
        var source = @"C:\photos\a.jpg";
        _fs.WriteAllTextAtomic(source, "hello photo");

        var result = await _service.ExecuteAsync(new FileActionRequest(source, FileOperationType.Copy, @"D:\Backup"));

        Assert.True(result.Succeeded);
        Assert.True(_fs.FileExists(source));
    }

    [Fact]
    public async Task ExecuteAsync_RelativeDestinationInsideSource_Succeeds()
    {
        var source = @"C:\photos\a.jpg";
        _fs.WriteAllTextAtomic(source, "hello photo");

        var result = await _service.ExecuteAsync(new FileActionRequest(source, FileOperationType.Move, @"sub\deeper"));

        Assert.True(result.Succeeded);
        Assert.Equal(@"C:\photos\sub\deeper\a.jpg", result.DestinationPath);
    }

    [Fact]
    public async Task ExecuteAsync_AbsoluteDestinationOutsideSource_StaysAllowed()
    {
        var source = @"C:\photos\a.jpg";
        _fs.WriteAllTextAtomic(source, "hello photo");

        var result = await _service.ExecuteAsync(new FileActionRequest(source, FileOperationType.Move, @"D:\Backup"));

        Assert.True(result.Succeeded);
        Assert.Equal(@"D:\Backup\a.jpg", result.DestinationPath);
    }

    [Fact]
    public async Task ExecuteAsync_Move_Success()
    {
        var source = @"C:\photos\a.jpg";
        var destFolder = @"C:\photos\selected";
        var expectedDest = @"C:\photos\selected\a.jpg";
        _fs.WriteAllTextAtomic(source, "hello photo");

        var request = new FileActionRequest(source, FileOperationType.Move, destFolder);
        var result = await _service.ExecuteAsync(request);

        Assert.True(result.Succeeded);
        Assert.Equal(FileOperationType.Move, result.Operation);
        Assert.Equal(source, result.Source);
        Assert.Equal(expectedDest, result.DestinationPath);
        Assert.Null(result.Error);
        Assert.False(_fs.FileExists(source));
        Assert.True(_fs.FileExists(expectedDest));

        var moves = _journal.ReadCommittedMoves();
        Assert.Single(moves);
        Assert.Equal(source, moves[0].Source);
        Assert.Equal(expectedDest, moves[0].Destination);
    }

    [Fact]
    public async Task ExecuteAsync_Copy_Success()
    {
        var source = @"C:\photos\a.jpg";
        var destFolder = @"C:\photos\copy_dest";
        var expectedDest = @"C:\photos\copy_dest\a.jpg";
        _fs.WriteAllTextAtomic(source, "hello copy");

        var request = new FileActionRequest(source, FileOperationType.Copy, destFolder);
        var result = await _service.ExecuteAsync(request);

        Assert.True(result.Succeeded);
        Assert.Equal(FileOperationType.Copy, result.Operation);
        Assert.Equal(expectedDest, result.DestinationPath);
        Assert.True(_fs.FileExists(source));
        Assert.True(_fs.FileExists(expectedDest));
    }

    [Fact]
    public async Task ExecuteAsync_Recycle_Success()
    {
        var source = @"C:\photos\to_delete.jpg";
        _fs.WriteAllTextAtomic(source, "delete me");

        var request = new FileActionRequest(source, FileOperationType.Recycle);
        var result = await _service.ExecuteAsync(request);

        Assert.True(result.Succeeded);
        Assert.Equal(FileOperationType.Recycle, result.Operation);
        Assert.Contains(source, _recycleBin.RecycledPaths);
    }

    [Fact]
    public async Task ExecuteAsync_DestinationMissing_Fails()
    {
        var source = @"C:\photos\a.jpg";
        _fs.WriteAllTextAtomic(source, "test");

        var request = new FileActionRequest(source, FileOperationType.Move, "   ");
        var result = await _service.ExecuteAsync(request);

        Assert.False(result.Succeeded);
        Assert.Equal("Hành động chưa có thư mục đích.", result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_SameFolder_Fails()
    {
        var source = @"C:\photos\a.jpg";
        _fs.WriteAllTextAtomic(source, "test");

        var request = new FileActionRequest(source, FileOperationType.Move, @"C:\photos");
        var result = await _service.ExecuteAsync(request);

        Assert.False(result.Succeeded);
        Assert.Equal("Không thể Di chuyển/Sao chép vào chính thư mục nguồn.", result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_DestinationExists_Fails()
    {
        var source = @"C:\photos\a.jpg";
        var destFolder = @"C:\photos\dest";
        var existing = @"C:\photos\dest\a.jpg";
        _fs.WriteAllTextAtomic(source, "test");
        _fs.WriteAllTextAtomic(existing, "already exists");

        var request = new FileActionRequest(source, FileOperationType.Move, destFolder);
        var result = await _service.ExecuteAsync(request);

        Assert.False(result.Succeeded);
        Assert.Contains("Đích đã tồn tại:", result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_SourceNotFound_FailsWithoutPreparedInJournal()
    {
        var source = @"C:\photos\nonexistent.jpg";
        var request = new FileActionRequest(source, FileOperationType.Move, @"C:\photos\dest");
        var result = await _service.ExecuteAsync(request);

        Assert.False(result.Succeeded);
        Assert.Contains("Nguồn không tồn tại", result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_IOErrorDuringMove_RecordsFailedInJournal()
    {
        var source = @"C:\photos\error.jpg";
        _fs.WriteAllTextAtomic(source, "will fail");

        _fs.MoveHook = (src, dst) => new IOException("Disk error simulated");

        var request = new FileActionRequest(source, FileOperationType.Move, @"C:\photos\dest");
        var result = await _service.ExecuteAsync(request);

        Assert.False(result.Succeeded);
        Assert.Equal("Disk error simulated", result.Error);

        var failedEntries = _journal.ReadFailedOperations();
        Assert.Single(failedEntries);
        Assert.Equal(JournalState.Failed, failedEntries[0].State);
        Assert.Equal("Disk error simulated", failedEntries[0].Error);
    }

    [Fact]
    public async Task ExecuteAsync_MoveCompletedButCommitJournalFails_PreservesCompletedOutcome()
    {
        var source = @"C:\photos\journal-failure.jpg";
        var destination = @"C:\photos\dest\journal-failure.jpg";
        _fs.WriteAllTextAtomic(source, "content");
        var appendCalls = 0;
        _fs.OpenAppendHook = _ => ++appendCalls == 2
            ? new IOException("journal unavailable")
            : null;

        var result = await _service.ExecuteAsync(new FileActionRequest(source, FileOperationType.Move, @"C:\photos\dest"));

        Assert.True(result.Succeeded);
        Assert.False(result.JournalPersisted);
        Assert.Equal("journal unavailable", result.JournalError);
        Assert.Null(result.Error);
        Assert.False(_fs.FileExists(source));
        Assert.True(_fs.FileExists(destination));
    }

    [Fact]
    public async Task ExecuteAsync_WhenGateBusy_RejectsSecondAction()
    {
        var source1 = @"C:\photos\a.jpg";
        var source2 = @"C:\photos\b.jpg";
        _fs.WriteAllTextAtomic(source1, "content1");
        _fs.WriteAllTextAtomic(source2, "content2");

        var tcs = new TaskCompletionSource<bool>();
        _fs.MoveHook = (src, dst) =>
        {
            tcs.Task.Wait();
            return null;
        };

        var task1 = Task.Run(() => _service.ExecuteAsync(new FileActionRequest(source1, FileOperationType.Move, @"C:\photos\dest")));

        // Wait until task1 has acquired the gate
        while (!_service.IsBusy)
        {
            await Task.Delay(10);
        }

        var result2 = await _service.ExecuteAsync(new FileActionRequest(source2, FileOperationType.Move, @"C:\photos\dest"));

        Assert.True(result2.Rejected);
        Assert.False(result2.Succeeded);
        Assert.Equal("Thao tác trước đó vẫn đang chạy.", result2.Error);

        tcs.SetResult(true);
        var result1 = await task1;
        Assert.True(result1.Succeeded);
        Assert.False(_service.IsBusy);
    }

    [Fact]
    public void TryBegin_And_End_ManageBusyGate()
    {
        Assert.False(_service.IsBusy);
        Assert.True(_service.TryBegin());
        Assert.True(_service.IsBusy);
        Assert.False(_service.TryBegin());

        _service.End();
        Assert.False(_service.IsBusy);
        Assert.True(_service.TryBegin());
        _service.End();
    }
}

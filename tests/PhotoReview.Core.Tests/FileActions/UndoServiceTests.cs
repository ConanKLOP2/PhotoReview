using System.IO;
using System.Threading.Tasks;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.FileActions;
using PhotoReview.Core.Model;
using PhotoReview.Core.Tests.Fakes;
using Xunit;

namespace PhotoReview.Core.Tests.FileActions;

[Trait("Category", "HotPath")]
public sealed class UndoServiceTests
{
    private sealed class FakeClock : IClock
    {
        public FakeClock(DateTime utcNow) => UtcNow = utcNow;
        public DateTime UtcNow { get; set; }
        public long Timestamp => 0;
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
        public bool TryRestoreResult { get; set; } = true;
        public string? LastRestoredPath { get; private set; }

        public void SendToRecycleBin(string path) => RecycledPaths.Add(path);

        public bool TryRestore(string originalPath, long expectedSize, DateTime expectedLastWriteUtc)
        {
            LastRestoredPath = originalPath;
            return TryRestoreResult;
        }
    }

    private readonly InMemoryFileSystem _fs = new();
    private readonly FakeClock _clock = new(new DateTime(2026, 9, 19, 10, 0, 0, DateTimeKind.Utc));
    private readonly OperationJournal _journal;
    private readonly FakeRecycleBin _recycleBin = new();
    private readonly FileActionService _fileActionService;
    private readonly UndoService _service;

    public UndoServiceTests()
    {
        _journal = new OperationJournal(new FakeAppPaths(@"C:\data\operations.jsonl"), _fs, _clock);
        _fileActionService = new FileActionService(_journal, _fs, _clock, _recycleBin);
        _service = new UndoService(_journal, _fs, _recycleBin, _fileActionService);
    }

    [Fact]
    public async Task UndoMoveAsync_Success()
    {
        var source = @"C:\photos\photo1.jpg";
        var destination = @"C:\photos\sorted\photo1.jpg";
        var writeTime = new DateTime(2026, 9, 19, 9, 0, 0, DateTimeKind.Utc);
        _fs.AddFile(destination, "image-content", writeTime);

        _journal.Append(new JournalEntry("1", FileOperationType.Move, JournalState.Committed, source, destination, 13, writeTime, _clock.UtcNow));
        _service.LoadFromJournal();

        Assert.True(_service.CanUndoMove);
        Assert.Equal(1, _service.MoveHistoryCount);

        var result = await _service.UndoMoveAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(FileOperationType.Move, result.Operation);
        Assert.Equal(source, result.Source);
        Assert.Equal(destination, result.Destination);
        Assert.Null(result.ErrorMessage);

        Assert.True(_fs.FileExists(source));
        Assert.False(_fs.FileExists(destination));
        Assert.False(_service.CanUndoMove);
    }

    [Fact(DisplayName = "In-session undo retains more than the startup journal tail")]
    public async Task RegisterMoreThanStartupTail_UndoLatestUsesRegisteredFingerprint()
    {
        var writeTime = _clock.UtcNow.AddMinutes(-1);
        for (var i = 0; i < 250; i++)
        {
            var source = $@"C:\photos\source-{i}.jpg";
            var destination = $@"C:\photos\sorted\source-{i}.jpg";
            _fs.AddFile(destination, new string('x', 7 + i), writeTime);
            _journal.Append(new JournalEntry($"move-{i}", FileOperationType.Move, JournalState.Committed,
                source, destination, 7 + i, writeTime, _clock.UtcNow));
            _service.Register(new FileActionResult(true, FileOperationType.Move, source, destination,
                7 + i, writeTime, null));
        }

        Assert.Equal(250, _service.MoveHistoryCount);
        var result = await _service.UndoMoveAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(@"C:\photos\source-249.jpg", result.Source);
        Assert.Equal(249, _service.MoveHistoryCount);
    }

    [Fact]
    public async Task UndoMoveAsync_DestinationModified_FailsAndRestoresStack()
    {
        var source = @"C:\photos\photo1.jpg";
        var destination = @"C:\photos\sorted\photo1.jpg";
        var writeTime = new DateTime(2026, 9, 19, 9, 0, 0, DateTimeKind.Utc);
        _fs.AddFile(destination, "modified-longer-content", writeTime);

        _journal.Append(new JournalEntry("1", FileOperationType.Move, JournalState.Committed, source, destination, 10, writeTime, _clock.UtcNow));
        _service.LoadFromJournal();

        var result = await _service.UndoMoveAsync();

        Assert.False(result.Succeeded);
        Assert.Contains("Tệp đích đã thay đổi sau khi Di chuyển", result.ErrorMessage);
        Assert.True(_service.CanUndoMove);
        Assert.False(_fs.FileExists(source));
        Assert.True(_fs.FileExists(destination));
    }

    [Fact]
    public async Task UndoMoveAsync_SourceAlreadyExists_FailsAndRestoresStack()
    {
        var source = @"C:\photos\photo1.jpg";
        var destination = @"C:\photos\sorted\photo1.jpg";
        var writeTime = new DateTime(2026, 9, 19, 9, 0, 0, DateTimeKind.Utc);
        _fs.AddFile(source, "source-already-there", writeTime);
        _fs.AddFile(destination, "dest", writeTime);

        _service.Register(new FileActionResult(true, FileOperationType.Move, source, destination, 4, writeTime, null));

        var result = await _service.UndoMoveAsync();

        Assert.False(result.Succeeded);
        Assert.Contains("Nguồn hoặc đích đã thay đổi", result.ErrorMessage);
        Assert.True(_service.CanUndoMove);
    }

    [Fact]
    public async Task UndoMoveAsync_CaseInsensitiveDestinationMatch_P12()
    {
        var source = @"C:\Photos\Photo1.JPG";
        var destinationInJournal = @"C:\Photos\Sorted\Photo1.JPG";
        var destinationInMove = @"c:\photos\sorted\photo1.jpg";
        var writeTime = new DateTime(2026, 9, 19, 9, 0, 0, DateTimeKind.Utc);
        _fs.AddFile(destinationInMove, "test-data", writeTime);

        _journal.Append(new JournalEntry("1", FileOperationType.Move, JournalState.Committed, source, destinationInJournal, 9, writeTime, _clock.UtcNow));
        _service.Register(new FileActionResult(true, FileOperationType.Move, source, destinationInMove, 9, writeTime, null));

        var result = await _service.UndoMoveAsync();

        Assert.True(result.Succeeded);
        Assert.True(_fs.FileExists(source));
    }

    [Fact]
    public async Task UndoMoveAsync_EmptyHistory_ReturnsError()
    {
        var result = await _service.UndoMoveAsync();

        Assert.False(result.Succeeded);
        Assert.Equal("Không có thao tác Di chuyển nào để hoàn tác.", result.ErrorMessage);
    }

    [Fact]
    public async Task Register_Recycle_SetsLastAction_UndoLastRestores()
    {
        var source = @"C:\photos\deleted.jpg";
        var writeTime = new DateTime(2026, 9, 19, 9, 0, 0, DateTimeKind.Utc);

        _service.Register(new FileActionResult(true, FileOperationType.Recycle, source, null, 100, writeTime, null));

        Assert.True(_service.HasLastAction);
        Assert.False(_service.CanUndoMove);

        var result = await _service.UndoLastAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(FileOperationType.Recycle, result.Operation);
        Assert.Equal(source, result.Source);
        Assert.Equal(source, _recycleBin.LastRestoredPath);
        Assert.False(_service.HasLastAction);
    }

    [Fact]
    public async Task UndoLastAsync_Recycle_FailsWhenRestoreReturnsFalse()
    {
        var source = @"C:\photos\deleted.jpg";
        var writeTime = new DateTime(2026, 9, 19, 9, 0, 0, DateTimeKind.Utc);
        _recycleBin.TryRestoreResult = false;

        _service.Register(new FileActionResult(true, FileOperationType.Recycle, source, null, 100, writeTime, null));

        var result = await _service.UndoLastAsync();

        Assert.False(result.Succeeded);
        Assert.Contains("Không thể khôi phục từ Thùng rác", result.ErrorMessage);
    }

    [Fact]
    public async Task UndoLastAsync_NoLastAction_ReturnsError()
    {
        var result = await _service.UndoLastAsync();

        Assert.False(result.Succeeded);
        Assert.Equal("Không có thao tác Di chuyển/Xóa nào vừa thực hiện để hoàn tác.", result.ErrorMessage);
    }

    [Fact]
    public async Task UndoMoveAsync_WhenGateBusy_ReturnsRejected()
    {
        _fileActionService.TryBegin();
        Assert.True(_service.IsBusy);

        var result = await _service.UndoMoveAsync();

        Assert.False(result.Succeeded);
        Assert.True(result.Rejected);
        Assert.Contains("Đang bận", result.ErrorMessage);

        _fileActionService.End();
        Assert.False(_service.IsBusy);
    }
}


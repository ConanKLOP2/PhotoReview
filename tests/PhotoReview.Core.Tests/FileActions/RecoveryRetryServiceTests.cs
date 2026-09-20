using PhotoReview.Core.Abstractions;
using PhotoReview.Core.FileActions;
using PhotoReview.Core.Model;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.FileActions;

[Trait("Category", "HotPath")]
public sealed class RecoveryRetryServiceTests
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

    private readonly InMemoryFileSystem _fs = new();
    private readonly FakeClock _clock = new(new DateTime(2026, 9, 18, 12, 0, 0, DateTimeKind.Utc));
    private readonly OperationJournal _journal;
    private readonly RecoveryRetryService _service;

    public RecoveryRetryServiceTests()
    {
        _journal = new OperationJournal(new FakeAppPaths(@"C:\data\operations.jsonl"), _fs, _clock);
        _service = new RecoveryRetryService(_journal, _fs, _clock);
    }

    [Fact(DisplayName = "RetryMoveOrCopy executes Move successfully")]
    public void RetryMove_Success()
    {
        var source = @"C:\photos\a.jpg";
        var dest = @"C:\photos\sub\a.jpg";
        _fs.WriteAllTextAtomic(source, "12345");
        var stat = _fs.GetFileStat(source)!;

        var failed = new JournalEntry("op-1", FileOperationType.Move, JournalState.Failed,
            source, dest, stat.Length, stat.LastWriteUtc, _clock.UtcNow, "Previous error");

        var result = _service.RetryMoveOrCopy(failed);

        Assert.True(result.Succeeded);
        Assert.Equal("Retry thÃ nh cÃ´ng.", result.Message);
        Assert.NotNull(result.Entry);
        Assert.Equal(JournalState.Committed, result.Entry.State);
        Assert.False(_fs.FileExists(source));
        Assert.True(_fs.FileExists(dest));
    }

    [Fact(DisplayName = "RetryMoveOrCopy executes Copy successfully leaving source")]
    public void RetryCopy_Success()
    {
        var source = @"C:\photos\a.jpg";
        var dest = @"C:\photos\sub\a.jpg";
        _fs.WriteAllTextAtomic(source, "12345");
        var stat = _fs.GetFileStat(source)!;

        var failed = new JournalEntry("op-copy", FileOperationType.Copy, JournalState.Failed,
            source, dest, stat.Length, stat.LastWriteUtc, _clock.UtcNow, "Previous error");

        var result = _service.RetryMoveOrCopy(failed);

        Assert.True(result.Succeeded);
        Assert.Equal("Retry thÃ nh cÃ´ng.", result.Message);
        Assert.NotNull(result.Entry);
        Assert.Equal(JournalState.Committed, result.Entry.State);
        Assert.True(_fs.FileExists(source));
        Assert.True(_fs.FileExists(dest));
    }

    [Fact(DisplayName = "RetryMoveOrCopy preserves completed move when commit journal fails")]
    public void RetryMove_CommitJournalFailure_PreservesCompletedOutcome()
    {
        var source = @"C:\photos\journal-failure.jpg";
        var dest = @"C:\photos\sub\journal-failure.jpg";
        _fs.WriteAllTextAtomic(source, "12345");
        var stat = _fs.GetFileStat(source)!;
        var appendCalls = 0;
        _fs.OpenAppendHook = _ => ++appendCalls == 2
            ? new IOException("journal unavailable")
            : null;
        var failed = new JournalEntry("op-journal-failure", FileOperationType.Move, JournalState.Failed,
            source, dest, stat.Length, stat.LastWriteUtc, _clock.UtcNow, "Previous error");

        var result = _service.RetryMoveOrCopy(failed);

        Assert.True(result.Succeeded);
        Assert.False(result.JournalPersisted);
        Assert.Equal("journal unavailable", result.JournalError);
        Assert.False(_fs.FileExists(source));
        Assert.True(_fs.FileExists(dest));
    }

    [Fact(DisplayName = "RetryMoveOrCopy preserves mutation failure when failed journal append also fails")]
    public void RetryMove_MutationAndFailureJournalFailure_PreservesOriginalFailure()
    {
        var source = @"C:\photos\mutation-failure.jpg";
        var dest = @"C:\photos\sub\mutation-failure.jpg";
        _fs.WriteAllTextAtomic(source, "12345");
        var stat = _fs.GetFileStat(source)!;
        var appendCalls = 0;
        _fs.OpenAppendHook = _ => ++appendCalls == 2
            ? new IOException("journal unavailable")
            : null;
        _fs.MoveHook = (_, _) => new IOException("move unavailable");
        var failed = new JournalEntry("op-mutation-journal-failure", FileOperationType.Move, JournalState.Failed,
            source, dest, stat.Length, stat.LastWriteUtc, _clock.UtcNow, "Previous error");

        var result = _service.RetryMoveOrCopy(failed);

        Assert.False(result.Succeeded);
        Assert.False(result.JournalPersisted);
        Assert.Equal("journal unavailable", result.JournalError);
        Assert.Contains("move unavailable", result.Message);
        Assert.True(_fs.FileExists(source));
        Assert.False(_fs.FileExists(dest));
    }

    [Fact(DisplayName = "RetryMoveOrCopy rejects Recycle operation")]
    public void Retry_RejectsRecycle()
    {
        var failed = new JournalEntry("op-recycle", FileOperationType.Recycle, JournalState.Failed,
            @"C:\photos\a.jpg", null, 100, _clock.UtcNow, _clock.UtcNow);

        var result = _service.RetryMoveOrCopy(failed);

        Assert.False(result.Succeeded);
        Assert.Contains("Chá»‰ cho phÃ©p retry Move/Copy", result.Message);
    }

    [Fact(DisplayName = "RetryMoveOrCopy rejects missing destination")]
    public void Retry_RejectsMissingDestination()
    {
        var failed = new JournalEntry("op-nodest", FileOperationType.Move, JournalState.Failed,
            @"C:\photos\a.jpg", null, 100, _clock.UtcNow, _clock.UtcNow);

        var result = _service.RetryMoveOrCopy(failed);

        Assert.False(result.Succeeded);
        Assert.Equal("Operation khÃ´ng cÃ³ Ä‘Ã­ch.", result.Message);
    }

    [Fact(DisplayName = "RetryMoveOrCopy rejects when source no longer exists")]
    public void Retry_RejectsMissingSource()
    {
        var failed = new JournalEntry("op-nosrc", FileOperationType.Move, JournalState.Failed,
            @"C:\photos\missing.jpg", @"C:\photos\dest.jpg", 100, _clock.UtcNow, _clock.UtcNow);

        var result = _service.RetryMoveOrCopy(failed);

        Assert.False(result.Succeeded);
        Assert.Equal("Nguá»“n khÃ´ng cÃ²n tá»“n táº¡i.", result.Message);
    }

    [Fact(DisplayName = "RetryMoveOrCopy rejects when source fingerprint has changed")]
    public void Retry_RejectsChangedSource()
    {
        var source = @"C:\photos\a.jpg";
        _fs.WriteAllTextAtomic(source, "new content with different length");

        var failed = new JournalEntry("op-changed", FileOperationType.Move, JournalState.Failed,
            source, @"C:\photos\dest.jpg", 5, _clock.UtcNow, _clock.UtcNow);

        var result = _service.RetryMoveOrCopy(failed);

        Assert.False(result.Succeeded);
        Assert.Equal("Nguá»“n Ä‘Ã£ thay Ä‘á»•i; tá»« chá»‘i retry Ä‘á»ƒ báº£o vá»‡ dá»¯ liá»‡u.", result.Message);
    }

    [Fact(DisplayName = "RetryMoveOrCopy rejects when destination already exists")]
    public void Retry_RejectsExistingDestination()
    {
        var source = @"C:\photos\a.jpg";
        var dest = @"C:\photos\dest.jpg";
        _fs.WriteAllTextAtomic(source, "12345");
        _fs.WriteAllTextAtomic(dest, "already exists");
        var stat = _fs.GetFileStat(source)!;

        var failed = new JournalEntry("op-destexists", FileOperationType.Move, JournalState.Failed,
            source, dest, stat.Length, stat.LastWriteUtc, _clock.UtcNow);

        var result = _service.RetryMoveOrCopy(failed);

        Assert.False(result.Succeeded);
        Assert.Equal("ÄÃ­ch Ä‘Ã£ tá»“n táº¡i; khÃ´ng ghi Ä‘Ã¨.", result.Message);
    }

    [Fact(DisplayName = "Constructor validates null arguments")]
    public void Constructor_NullValidation()
    {
        Assert.Throws<ArgumentNullException>(() => new RecoveryRetryService(null!, _fs, _clock));
        Assert.Throws<ArgumentNullException>(() => new RecoveryRetryService(_journal, null!, _clock));
        Assert.Throws<ArgumentNullException>(() => new RecoveryRetryService(_journal, _fs, null!));
    }
}


using System.IO;
using System.Threading.Tasks;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.FileActions;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Model;
using PhotoReview.Core.Settings;
using PhotoReview.Core.Tests.Fakes;
using Xunit;

namespace PhotoReview.Core.Tests.FileActions;

/// <summary>
/// L06 / Q-L3 (ADR 0006): journal failures persist a stable code + invariant English text, the UI localizes by code,
/// and records written before codes existed keep showing their stored text. Also covers Core messages in English.
/// These tests switch the process-wide <see cref="Localizer.Current"/>, so they run in the serial GlobalState collection.
/// </summary>
[Collection("GlobalState")]
public sealed class JournalErrorCodeTests : IDisposable
{
    private const string JournalPath = @"C:\data\operations.jsonl";

    private sealed class FakeClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow { get; } = utcNow;
        public long Timestamp => 0;
    }

    private sealed class FakeAppPaths : IAppPaths
    {
        public string ConfigFile => @"C:\data\config.json";
        public string JournalFile => JournalPath;
        public string SessionsDir => @"C:\data\Sessions";
        public string LogFile => @"C:\data\logs\app.log";
        public string PreviewCacheDir => @"C:\data\cache";
        public string ThumbnailCacheDir => @"C:\data\thumbnails";
        public string WindowPlacementFile => @"C:\data\window-placement.json";
    }

    private sealed class NoRecycleBin : IRecycleBin
    {
        public void SendToRecycleBin(string path) { }
        public bool TryRestore(string originalPath, long expectedSize, DateTime expectedLastWriteUtc) => false;
    }

    private sealed class AllKeysValid : IKeyNameValidator
    {
        public bool IsValidKeyName(string keyName) => keyName != "Nope";
    }

    private readonly Localizer _previous = Localizer.Current;
    private readonly InMemoryFileSystem _fs = new();
    private readonly FakeClock _clock = new(new DateTime(2026, 9, 24, 8, 0, 0, DateTimeKind.Utc));
    private readonly OperationJournal _journal;

    public JournalErrorCodeTests() => _journal = new OperationJournal(new FakeAppPaths(), _fs, _clock);

    public void Dispose() => Localizer.SetCurrent(_previous);

    [Fact]
    public void Reconcile_FailedRecycle_PersistsCodeAndEnglishText_EvenWhenUiIsVietnamese()
    {
        Localizer.SetCurrent(TestLocalization.Vietnamese);
        _fs.AddFile(@"C:\photos\present.jpg", "hello");
        _journal.Append(new JournalEntry("rec", FileOperationType.Recycle, JournalState.Prepared,
            @"C:\photos\present.jpg", null, 5, _clock.UtcNow, _clock.UtcNow));

        _journal.ReconcilePendingOperations();

        var lastLine = _fs.ReadLines(JournalPath).Last();
        Assert.Contains("\"Error\":\"The source still exists after session recovery.\"", lastLine, StringComparison.Ordinal);
        Assert.EndsWith(",\"ErrorCode\":\"SourceStillExistsAfterRecovery\"}", lastLine, StringComparison.Ordinal);

        var failed = Assert.Single(_journal.ReadFailedOperations());
        Assert.Equal(JournalErrors.SourceStillExistsAfterRecovery, failed.ErrorCode);
        Assert.Equal("The source still exists after session recovery.", failed.Error);
        Assert.Equal("Nguồn vẫn tồn tại sau khi khôi phục phiên.", JournalErrors.Describe(failed));

        Localizer.SetCurrent(TestLocalization.English);
        Assert.Equal("The source still exists after session recovery.", JournalErrors.Describe(failed));
    }

    [Fact]
    public void Reconcile_FailedMove_PersistsPendingUnconfirmedCode()
    {
        _fs.AddFile(@"C:\photos\src.jpg", "1234");
        _journal.Append(new JournalEntry("mv", FileOperationType.Move, JournalState.Prepared,
            @"C:\photos\src.jpg", @"C:\photos\dest.jpg", 4, _clock.UtcNow, _clock.UtcNow));

        var entry = Assert.Single(_journal.ReconcilePendingOperations());

        Assert.Equal(JournalState.Failed, entry.State);
        Assert.Equal(JournalErrors.PendingUnconfirmed, entry.ErrorCode);
        Assert.Equal("Could not confirm the pending operation; it will not be replayed automatically.", entry.Error);
    }

    [Fact]
    public void OldRecordWithoutCode_Loads_AndDescribeShowsStoredText()
    {
        // Exact line shape written before L06 (Vietnamese text, no ErrorCode member).
        _fs.AddFile(JournalPath,
            "{\"Id\":\"old\",\"Type\":\"Recycle\",\"State\":\"Failed\",\"Source\":\"C:\\\\photos\\\\a.jpg\",\"Destination\":null," +
            "\"Size\":5,\"LastWriteUtc\":\"2026-09-18T12:00:00Z\",\"TimestampUtc\":\"2026-09-18T12:00:00Z\"," +
            "\"Error\":\"Ngu\\u1ED3n v\\u1EABn t\\u1ED3n t\\u1EA1i sau khi kh\\u00F4i ph\\u1EE5c phi\\u00EAn.\"}\n");
        Localizer.SetCurrent(TestLocalization.English);

        var old = Assert.Single(_journal.ReadFailedOperations());

        Assert.Null(old.ErrorCode);
        Assert.Equal("Nguồn vẫn tồn tại sau khi khôi phục phiên.", JournalErrors.Describe(old));
    }

    [Fact]
    public void UnknownCode_FromNewerBuild_ShowsStoredText()
    {
        Assert.False(JournalErrors.IsKnown("SomethingNew"));
        Assert.Equal("stored", JournalErrors.Describe("SomethingNew", "stored"));
        Assert.Null(JournalErrors.Describe(null, null));
    }

    [Fact]
    public void EntryWithoutError_KeepsPreviousJsonShape()
    {
        _journal.Append(new JournalEntry("ok", FileOperationType.Move, JournalState.Committed,
            @"C:\photos\a.jpg", @"C:\photos\b.jpg", 1, _clock.UtcNow, _clock.UtcNow));

        var line = Assert.Single(_fs.ReadLines(JournalPath));

        Assert.EndsWith(",\"Error\":null}", line, StringComparison.Ordinal);
        Assert.DoesNotContain("ErrorCode", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FileAction_SizeChangedAfterMove_JournalsCode_ResultShowsUiLanguage()
    {
        Localizer.SetCurrent(TestLocalization.Vietnamese);
        _fs.AddFile(@"C:\photos\a.jpg", "hello photo");
        var service = new FileActionService(_journal, _fs, _clock, new NoRecycleBin(), (source, destination) =>
        {
            _fs.Move(source, destination);
            _fs.WriteAllTextAtomic(destination, "x");
            return Task.CompletedTask;
        });

        var result = await service.ExecuteAsync(new FileActionRequest(@"C:\photos\a.jpg", FileOperationType.Move, "picked"));

        Assert.False(result.Succeeded);
        Assert.Equal("Kiểm tra sau thao tác thất bại: kích thước đích đã thay đổi.", result.Error);
        var failed = Assert.Single(_journal.ReadFailedOperations());
        Assert.Equal(JournalErrors.VerifySizeChanged, failed.ErrorCode);
        Assert.Equal("Post-operation check failed: the destination size changed.", failed.Error);
    }

    [Fact]
    public async Task FileAction_OsFailure_JournalsRawMessageWithoutCode()
    {
        _fs.AddFile(@"C:\photos\a.jpg", "hello photo");
        var service = new FileActionService(_journal, _fs, _clock, new NoRecycleBin(),
            (_, _) => Task.FromException(new IOException("OS says no")));

        var result = await service.ExecuteAsync(new FileActionRequest(@"C:\photos\a.jpg", FileOperationType.Move, "picked"));

        Assert.Equal("OS says no", result.Error);
        var failed = Assert.Single(_journal.ReadFailedOperations());
        Assert.Null(failed.ErrorCode);
        Assert.Equal("OS says no", failed.Error);
        Assert.Equal("OS says no", JournalErrors.Describe(failed));
    }

    [Fact]
    public async Task CoreMessages_English()
    {
        Localizer.SetCurrent(TestLocalization.English);

        Assert.Equal("No valid file or folder.", DragDropInputService.Parse([]).Warning);

        var settings = new AppSettings();
        settings.Shortcuts.Next = "Nope";
        Assert.Equal("Shortcut Next is not valid.", new SettingsValidator(new AllKeysValid()).ValidateShortcuts(settings));

        var retry = await new RecoveryRetryService(_journal, _fs, _clock).RetryMoveOrCopyAsync(
            new JournalEntry("r", FileOperationType.Recycle, JournalState.Failed, @"C:\photos\a.jpg", null, 1, _clock.UtcNow, _clock.UtcNow));
        Assert.Equal("Only Move/Copy can be retried; Recycle Bin operations are not retried automatically.", retry.Message);
    }

    [Fact]
    public async Task UndoMessages_English()
    {
        Localizer.SetCurrent(TestLocalization.English);
        var undo = new UndoService(_journal, _fs, new NoRecycleBin());

        var nothing = await undo.UndoLastAsync();
        var noMove = await undo.UndoMoveAsync();

        Assert.Equal("There is no recent Move/Delete to undo.", nothing.ErrorMessage);
        Assert.Equal("There is no Move to undo.", noMove.ErrorMessage);
    }
}

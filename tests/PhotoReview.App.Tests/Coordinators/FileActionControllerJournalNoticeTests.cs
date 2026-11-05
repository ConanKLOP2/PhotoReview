using System.IO;
using PhotoReview.App.Coordinators;
using PhotoReview.Core;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.IO;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.FileActions;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Model;

namespace PhotoReview.App.Tests.Coordinators;

/// <summary>
/// RP-JOURNAL-NOTICE-01 (P-72 option A): a file action that SUCCEEDED but whose journal line could not be written
/// (<c>JournalPersisted == false</c>) logs one error line and shows one non-blocking status-bar notice, for the single-file and
/// the capture-group result alike; nothing is shown when the journal was written. Fake executor, temp folder, no Recycle Bin.
/// </summary>
[Collection("GlobalState")]
public sealed class FileActionControllerJournalNoticeTests : IDisposable
{
    private const string JournalError = "simulated journal failure (disk full)";

    private readonly FileActionControllerMutationGapHarness _h = new();
    private readonly NoticeSink _sink = new();
    private readonly string _logPath;
    private readonly FileLog _log;

    public FileActionControllerJournalNoticeTests()
    {
        _logPath = Path.Combine(_h.Root, "log", "notice.log");
        _log = new FileLog(_logPath);
        AppLog.Instance = _log;
        AppLog.Enabled = true;
    }

    public void Dispose()
    {
        AppLog.Enabled = false;
        AppLog.Instance = null!;
        _log.Dispose();
        _h.Dispose();
    }

    private sealed class NoticeSink : IFileActionSink
    {
        public List<string> Statuses { get; } = [];
        public List<string> LateStatuses { get; } = [];

        public void SetStatusText(string status) => Statuses.Add(status);
        public void ShowLateActionStatus(string status) => LateStatuses.Add(status);
        public void OnCatalogChanged(string? removedPath) { }
        public Task PresentAsync(int index) => Task.CompletedTask;
        public void UpdateSessionPath(string currentPath) { }
        public void NotifyNavigationStateChanged() { }
    }

    private sealed class FakeExecutor(FileActionResult single, CaptureGroupActionResult? group = null) : IFileActionExecutor
    {
        public bool IsBusy => false;
        public bool LacksRecycleBin(string path) => false;
        public Task<FileActionResult> ExecuteAsync(FileActionRequest request, CancellationToken cancellationToken = default) => Task.FromResult(single);
        public Task<CaptureGroupActionResult> ExecuteGroupAsync(CaptureGroupActionRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(group ?? throw new NotSupportedException());
    }

    private FileActionController NewController(IFileActionExecutor executor) => new(
        _h.Catalog, _h.Clock, fileActionService: null, undoService: null, dialogService: null, preloadController: null,
        ManagedNaturalComparer.Instance,
        () => FileActionControllerMutationGapHarness.Settings(FileActionControllerMutationGapHarness.MoveAction()), _sink,
        null, _h.Fs)
    {
        FileActionsOverride = executor,
    };

    private static FileActionResult Single(string source, bool journalPersisted) =>
        new(true, FileOperationType.Move, source, Path.Combine("Sorted", Path.GetFileName(source)), 4, default, null,
            JournalPersisted: journalPersisted, JournalError: journalPersisted ? null : JournalError);

    private static CaptureGroupActionResult Group(string groupId, bool journalPersisted) =>
        new(true, false, FileOperationType.Move, groupId, null, [], null,
            JournalPersisted: journalPersisted, JournalError: journalPersisted ? null : JournalError);

    private string ReadLog()
    {
        _log.Flush();
        return File.Exists(_logPath) ? File.ReadAllText(_logPath) : string.Empty;
    }

    private (string A, string Z) OpenTwoFiles()
    {
        var a = _h.Make("a.jpg");
        var z = _h.Make("z.jpg");
        _h.Catalog.Reset([a, z]);
        return (a, z);
    }

    [Fact]
    public async Task SingleMove_SucceededButJournalNotPersisted_ShowsNoticeAndLogsOneErrorLine()
    {
        var (a, _) = OpenTwoFiles();
        var controller = NewController(new FakeExecutor(Single(a, journalPersisted: false)));

        await controller.RunActionAsync(0, null, a);

        Assert.Equal([Tr.StatusActionJournalNotSaved], _sink.Statuses);
        var log = ReadLog();
        Assert.Contains(JournalError, log, StringComparison.Ordinal);
        Assert.Single(log.Split('\n'), line => line.Contains(JournalError, StringComparison.Ordinal));
    }

    [Fact]
    public async Task SingleMove_SucceededAndJournalPersisted_ShowsNoNoticeAndLogsNothing()
    {
        var (a, _) = OpenTwoFiles();
        var controller = NewController(new FakeExecutor(Single(a, journalPersisted: true)));

        await controller.RunActionAsync(0, null, a);

        Assert.DoesNotContain(Tr.StatusActionJournalNotSaved, _sink.Statuses);
        Assert.Empty(_sink.LateStatuses);
        Assert.DoesNotContain("journal", ReadLog(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SingleMove_JournalNotPersistedAfterFolderSwitch_AnnouncedThroughTheLateChannel()
    {
        var (a, _) = OpenTwoFiles();
        var other = _h.Make("b.jpg");
        var inner = new FakeExecutor(Single(a, journalPersisted: false));
        var controller = NewController(new SwitchingExecutor(inner, () => { _h.Clock.NextFolder(); _h.Catalog.Reset([other]); }));

        await controller.RunActionAsync(0, null, a);

        Assert.Contains(Tr.StatusActionJournalNotSaved, _sink.LateStatuses);
        Assert.Empty(_sink.Statuses);
        Assert.Contains(JournalError, ReadLog(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SingleMove_JournalNotPersistedAfterFolderSwitch_IsStillRegisteredForUndoAndTheNoticeIsShown()
    {
        var (a, _) = OpenTwoFiles();
        var other = _h.Make("b.jpg");
        var journal = new OperationJournal(new AppPaths(_h.Root), _h.Fs, new SystemClock());
        var undo = new UndoService(journal, _h.Fs, _h.Bin, new FileActionService(journal, _h.Fs, new SystemClock(), _h.Bin));
        var inner = new FakeExecutor(Single(a, journalPersisted: false));
        var controller = new FileActionController(
            _h.Catalog, _h.Clock, fileActionService: null, undo, dialogService: null, preloadController: null,
            ManagedNaturalComparer.Instance,
            () => FileActionControllerMutationGapHarness.Settings(FileActionControllerMutationGapHarness.MoveAction()), _sink,
            null, _h.Fs)
        {
            FileActionsOverride = new SwitchingExecutor(inner, () => { _h.Clock.NextFolder(); _h.Catalog.Reset([other]); }),
        };

        await controller.RunActionAsync(0, null, a);

        Assert.NotNull(undo.LastUndoAction);
        Assert.Equal([Tr.StatusActionJournalNotSaved], _sink.LateStatuses);
        Assert.Empty(_sink.Statuses);
    }

    private sealed class SwitchingExecutor(IFileActionExecutor inner, Action beforeResult) : IFileActionExecutor
    {
        public bool IsBusy => false;
        public bool LacksRecycleBin(string path) => false;

        public async Task<FileActionResult> ExecuteAsync(FileActionRequest request, CancellationToken cancellationToken = default)
        {
            var result = await inner.ExecuteAsync(request, cancellationToken);
            beforeResult();
            return result;
        }

        public Task<CaptureGroupActionResult> ExecuteGroupAsync(CaptureGroupActionRequest request, CancellationToken cancellationToken = default)
            => inner.ExecuteGroupAsync(request, cancellationToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GroupMove_JournalPersistedFlag_DecidesWhetherTheNoticeAndLogLineAppear(bool journalPersisted)
    {
        var before = _h.Make("zbefore.jpg");
        var jpeg = _h.Make("pair.jpg");
        var raw = _h.Make("pair.cr2", 8);
        _h.Catalog.Reset([new CatalogEntry(before), new CatalogEntry(jpeg), new CatalogEntry(raw)], RawPairMode.PreferJpeg);
        Assert.NotNull(_h.Catalog.Find(jpeg)?.CaptureGroup);
        var controller = NewController(new FakeExecutor(Single(jpeg, true), Group("g1", journalPersisted)));

        await controller.RunActionAsync(0, null, jpeg);

        if (journalPersisted)
        {
            Assert.DoesNotContain(Tr.StatusActionJournalNotSaved, _sink.Statuses);
            Assert.DoesNotContain(JournalError, ReadLog(), StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal([Tr.StatusActionJournalNotSaved], _sink.Statuses);
            Assert.Contains(JournalError, ReadLog(), StringComparison.Ordinal);
        }
    }
}

using System.IO;
using PhotoReview.App.Coordinators;
using PhotoReview.App.ViewModels;
using PhotoReview.Core;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.FileActions;
using PhotoReview.Core.IO;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Model;
using PhotoReview.Core.Settings;

namespace PhotoReview.App.Tests.Coordinators;

/// <summary>
/// Capture-group (JPEG+RAW+XMP) behavior of <see cref="FileActionController"/>: catalog rollback after a refused or failed
/// action, position and grouping of an undone Move, permanent-delete gating, partial undo results, no double present.
/// A temp folder with a fake Recycle Bin that only deletes files inside it (never the real Recycle Bin).
/// </summary>
public sealed class FileActionControllerGroupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PhotoReview_GroupCtl_" + Guid.NewGuid().ToString("N"));
    private readonly GenerationClock _clock = new();
    private readonly ReviewCatalog _catalog = new();
    private readonly RecordingSink _sink = new();
    private readonly FakeBin _bin = new();
    private readonly RecordingDialog _dialog = new(response: true);

    public FileActionControllerGroupTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string Make(string name, int length = 4)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[length]);
        return path;
    }

    private FileActionController NewController(AppSettings? settings = null, Func<string, string, Task>? undoMoveOverride = null, string moveDestination = "Sorted")
    {
        settings ??= new AppSettings();
        settings.Actions =
        [
            new ReviewAction { Name = "MoveToSub", Operation = FileOperationType.Move, Destination = moveDestination },
        ];
        var fs = new PhysicalFileSystem();
        var journal = new OperationJournal(new AppPaths(_root), fs, new SystemClock());
        var fileActions = new FileActionService(journal, fs, new SystemClock(), _bin);
        var undo = new UndoService(journal, fs, _bin, fileActions, undoMoveOverride);
        return new FileActionController(
            _catalog, _clock, fileActions, undo, _dialog, preloadController: null,
            ManagedNaturalComparer.Instance, () => settings, _sink, fileSystem: fs);
    }

    /// <summary>Catalog of [before.jpg, pair (jpg+cr2[+xmp] as one entry), after.jpg] in the given pair mode.</summary>
    private (string Before, string Jpeg, string Raw, string? Xmp, string After) LoadPairBetweenTwoPhotos(RawPairMode mode = RawPairMode.PreferJpeg, bool withXmp = false)
    {
        var before = Make("zbefore.jpg");
        var jpeg = Make("pair.jpg");
        var raw = Make("pair.cr2", 8);
        var xmp = withXmp ? Make("pair.xmp", 3) : null;
        var after = Make("aafter.jpg");
        _catalog.Reset(
            [new CatalogEntry(before), new CatalogEntry(jpeg), new CatalogEntry(raw), new CatalogEntry(after)],
            mode, xmp is null ? null : [xmp]);
        return (before, jpeg, raw, xmp, after);
    }

    [Fact]
    public async Task GroupMove_PreflightRefused_RestoresTheWholeGroupedEntryAtItsOriginalIndex()
    {
        var (before, jpeg, raw, _, after) = LoadPairBetweenTwoPhotos();
        Make(Path.Combine("Sorted", "pair.cr2")); // destination of the second member exists: preflight throws, Members = []
        var controller = NewController();

        await controller.RunActionAsync(0, null, jpeg);

        Assert.Equal([before, jpeg, after], _catalog.Paths);
        var entry = _catalog.Entries[1];
        Assert.NotNull(entry.CaptureGroup);
        Assert.Equal(raw, entry.CaptureGroup!.RawPath);
        Assert.Equal(1, _catalog.IndexOf(raw));
        Assert.True(File.Exists(jpeg) && File.Exists(raw));
        Assert.Contains("MoveToSub", _sink.LastStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GroupRecycle_BinCannotHoldTheWholeCapture_RestoresGroupedEntryAndDeletesNothing()
    {
        var (before, jpeg, _, xmp, after) = LoadPairBetweenTwoPhotos(withXmp: true);
        _bin.FitsAll = false;
        var controller = NewController();

        await controller.RecycleAsync(null, jpeg);

        Assert.Equal([before, jpeg, after], _catalog.Paths);
        Assert.NotNull(_catalog.Entries[1].CaptureGroup);
        Assert.Empty(_bin.Recycled);
        Assert.True(File.Exists(xmp!));
    }

    [Fact]
    public async Task GroupMoveRefused_KeepsCurrentIndexOnTheDisplayedPhoto()
    {
        var (_, jpeg, _, _, after) = LoadPairBetweenTwoPhotos();
        Make(Path.Combine("Sorted", "pair.cr2"));
        _catalog.SetCurrent(_catalog.IndexOf(jpeg));
        var controller = NewController();

        await controller.RunActionAsync(0, null, jpeg);

        // After the removal the catalog points at the photo after the pair, which the presenter shows. The restore must not
        // yank the selection back to the restored capture.
        Assert.Equal(after, _catalog.Current!.Path);
        Assert.Equal(2, _catalog.CurrentIndex);
    }

    [Fact]
    public async Task GroupMovePartialFailure_RollsBackSoTheWholeGroupReturnsAsOneEntry()
    {
        var (before, jpeg, raw, _, after) = LoadPairBetweenTwoPhotos();
        var controller = NewController();
        // The RAW cannot be moved: make its destination a directory of the same name (the JPEG moves first and is rolled back).
        Directory.CreateDirectory(Path.Combine(_root, "Sorted", "pair.cr2"));

        await controller.RunActionAsync(0, null, jpeg);

        Assert.True(File.Exists(jpeg));
        Assert.True(File.Exists(raw));
        Assert.Equal([before, jpeg, after], _catalog.Paths);
        Assert.NotNull(_catalog.Entries[1].CaptureGroup);
    }

    [Theory]
    [InlineData(RawPairMode.PreferJpeg)]
    [InlineData(RawPairMode.PreferRaw)]
    public async Task UndoGroupMove_RestoresOneGroupedEntryAtItsFormerPosition_AndNeverListsTheSidecar(RawPairMode mode)
    {
        var (before, jpeg, raw, xmp, after) = LoadPairBetweenTwoPhotos(mode, withXmp: true);
        var representative = mode == RawPairMode.PreferJpeg ? jpeg : raw;
        var controller = NewController();
        await controller.RunActionAsync(0, null, representative);
        Assert.Equal([before, after], _catalog.Paths);

        var result = await controller.UndoLastAsync(_root);

        Assert.True(result!.Succeeded, result.ErrorMessage);
        Assert.Equal([before, representative, after], _catalog.Paths); // not index 0
        var entry = _catalog.Entries[1];
        Assert.NotNull(entry.CaptureGroup);
        Assert.Equal(xmp, entry.CaptureGroup!.XmpPath);
        Assert.Equal(-1, _catalog.IndexOf(xmp!));
        Assert.Equal(1, _catalog.IndexOf(jpeg));
        Assert.Equal(1, _catalog.IndexOf(raw));
        Assert.True(File.Exists(xmp!)); // the sidecar travelled back too
    }

    [Fact]
    public async Task UndoGroupMove_OfTheFirstPhoto_RestoresAtFront()
    {
        var jpeg = Make("a.jpg");
        var raw = Make("a.cr2", 8);
        var other = Make("b.jpg");
        _catalog.Reset([new CatalogEntry(jpeg), new CatalogEntry(raw), new CatalogEntry(other)], RawPairMode.PreferJpeg);
        var controller = NewController();
        await controller.RunActionAsync(0, null, jpeg);

        await controller.UndoLastAsync(_root);

        Assert.Equal([jpeg, other], _catalog.Paths);
        Assert.NotNull(_catalog.Entries[0].CaptureGroup);
    }

    [Fact]
    public async Task UndoGroupRecycle_DoesNotRestoreIntoCatalogOrPresentAgain_BecauseTheCallerReloadsTheFolder()
    {
        var (_, jpeg, _, _, _) = LoadPairBetweenTwoPhotos();
        var controller = NewController();
        await controller.RecycleAsync(null, jpeg);
        var presentsBeforeUndo = _sink.Presented.Count;

        var result = await controller.UndoLastAsync(_root);

        Assert.True(result!.Succeeded, result.ErrorMessage);
        Assert.Equal(presentsBeforeUndo, _sink.Presented.Count); // MainViewModel.UndoCoreAsync reloads and presents once
        Assert.True(FileActionController.RestoresOutsideFolder(result, _root));
    }

    [Fact]
    public async Task UndoGroupRecycle_FailingMidWay_ShowsTheAlreadyRestoredMemberInTheCatalog()
    {
        var (before, jpeg, raw, _, after) = LoadPairBetweenTwoPhotos();
        var controller = NewController();
        await controller.RecycleAsync(null, jpeg);
        Assert.Equal([before, after], _catalog.Paths);
        _bin.FailRestoreFor = raw;

        var result = await controller.UndoLastAsync(_root);

        Assert.False(result!.Succeeded);
        Assert.True(File.Exists(jpeg));
        Assert.False(File.Exists(raw));
        Assert.Contains(jpeg, _catalog.Paths); // reflected without waiting for a reload
        Assert.Equal(result.ErrorMessage, _sink.LastStatus);
    }

    [Fact]
    public async Task UndoGroupMove_FailingMidWay_AppliesTheRestoredMembersEvenThoughItFailed()
    {
        var (before, jpeg, raw, _, after) = LoadPairBetweenTwoPhotos();
        var controller = NewController(undoMoveOverride: (source, destination) =>
        {
            if (source.EndsWith(".cr2", StringComparison.OrdinalIgnoreCase)) throw new IOException("simulated undo failure");
            File.Move(source, destination);
            return Task.CompletedTask;
        });
        await controller.RunActionAsync(0, null, jpeg);
        Assert.Equal([before, after], _catalog.Paths);

        var result = await controller.UndoLastAsync(_root);

        Assert.False(result!.Succeeded);
        Assert.True(File.Exists(jpeg));
        Assert.False(File.Exists(raw));
        Assert.Equal([before, jpeg, after], _catalog.Paths); // the JPEG is back at its position, ungrouped (its partner is not)
        Assert.Null(_catalog.Entries[1].CaptureGroup);
    }

    [Fact]
    public async Task UndoGroupRecycle_FailingMidWay_WhenTheOnlyCaptureWasRemoved_PresentsTheRestoredEntry()
    {
        var jpeg = Make("only.jpg");
        var raw = Make("only.cr2", 8);
        _catalog.Reset([new CatalogEntry(jpeg), new CatalogEntry(raw)], RawPairMode.PreferJpeg);
        var controller = NewController();
        await controller.RecycleAsync(null, jpeg);
        Assert.Equal(0, _catalog.Count);
        _sink.Presented.Clear();
        _bin.FailRestoreFor = raw;

        var result = await controller.UndoLastAsync(_root);

        Assert.False(result!.Succeeded);
        Assert.Equal([jpeg], _catalog.Paths);
        Assert.Equal([0], _sink.Presented); // otherwise the view stays blank although HasImages is true
        Assert.Equal(result.ErrorMessage, _sink.LastStatus);
    }

    [Fact]
    public async Task UndoGroupRecycle_FailingMidWay_WhileAnotherPhotoIsShown_KeepsItDisplayed()
    {
        var (_, jpeg, raw, _, after) = LoadPairBetweenTwoPhotos();
        var controller = NewController();
        await controller.RecycleAsync(null, jpeg);
        var presentsBefore = _sink.Presented.Count;
        _bin.FailRestoreFor = raw;

        await controller.UndoLastAsync(_root);

        Assert.Equal(presentsBefore, _sink.Presented.Count);
        Assert.Equal(after, _catalog.Current!.Path);
    }

    [Fact]
    public async Task UndoGroupRecycle_FailingMidWay_RestoresTheMemberAtTheOriginalPosition()
    {
        var (before, jpeg, raw, _, after) = LoadPairBetweenTwoPhotos();
        var controller = NewController();
        await controller.RecycleAsync(null, jpeg);
        _bin.FailRestoreFor = raw;

        await controller.UndoLastAsync(_root);

        Assert.Equal([before, jpeg, after], _catalog.Paths); // not appended at the end
    }

    [Fact]
    public async Task UndoGroupMove_MembersSplitAcrossFolders_DoesNotInsertOrPresentBecauseTheCallerReloads()
    {
        var before = Make("zbefore.jpg");
        var after = Make("aafter.jpg");
        var jpeg = Make("pair.jpg");
        var raw = Make(Path.Combine("other", "pair.cr2"), 8);
        _catalog.Reset([new CatalogEntry(before), new CatalogEntry(after)], RawPairMode.PreferJpeg);
        _catalog.RestoreMembers([jpeg, raw], 1, new CaptureGroup(jpeg, raw));
        Assert.Equal([before, jpeg, after], _catalog.Paths);
        var controller = NewController(moveDestination: Path.Combine(_root, "Elsewhere")); // absolute: the only way a capture spans folders
        await controller.RunActionAsync(0, null, jpeg);
        Assert.True(_catalog.Paths.SequenceEqual([before, after]), _sink.LastStatus);
        var presentsBeforeUndo = _sink.Presented.Count;

        var result = await controller.UndoLastAsync(_root);

        Assert.True(result!.Succeeded, result.ErrorMessage);
        Assert.True(FileActionController.RestoresOutsideFolder(result, _root));
        Assert.Equal(presentsBeforeUndo, _sink.Presented.Count);
        Assert.Equal([before, after], _catalog.Paths);
    }

    [Fact]
    public void ReloadPathAfterUndo_SourceWasNotRestored_PicksARestoredImageMember()
    {
        var source = Path.Combine(_root, "gone.jpg");
        var xmp = Path.Combine(_root, "kept.xmp");
        var raw = Path.Combine(_root, "kept.cr2");
        var result = new UndoResult(true, FileOperationType.Recycle, source, null, "note", RestoredPaths: [xmp, raw]);

        Assert.Equal(raw, FileActionController.ReloadPathAfterUndo(result));
    }

    [Fact]
    public void ReloadPathAfterUndo_SourceWasRestored_KeepsTheSource()
    {
        var source = Path.Combine(_root, "a.jpg");
        var raw = Path.Combine(_root, "a.cr2");
        var result = new UndoResult(true, FileOperationType.Recycle, source, null, null, RestoredPaths: [raw, source]);

        Assert.Equal(source, FileActionController.ReloadPathAfterUndo(result));
    }

    [Fact]
    public async Task GroupMove_PartnerVanishedAndNothingLeftToReview_KeepsAllImagesProcessedAndNamesTheMissingFile()
    {
        var jpeg = Make("pair.jpg");
        var raw = Make("pair.cr2", 8);
        _catalog.Reset([new CatalogEntry(jpeg), new CatalogEntry(raw)], RawPairMode.PreferJpeg);
        File.Delete(raw);
        var controller = NewController();

        await controller.RunActionAsync(0, null, jpeg);

        Assert.Equal(0, _catalog.Count);
        Assert.StartsWith(StatusFormatter.AllImagesProcessed(), _sink.LastStatus, StringComparison.Ordinal);
        Assert.Contains(Tr.StatusGroupPartnerMissing("pair.jpg", "pair.cr2"), _sink.LastStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GroupMove_PartnerVanished_FolderSwitchedWhileThePresenterRuns_DoesNotOverwriteTheNewFoldersStatus()
    {
        var (_, jpeg, raw, _, _) = LoadPairBetweenTwoPhotos();
        File.Delete(raw);
        var gate = new TaskCompletionSource();
        _sink.PresentTask = () => gate.Task;
        var controller = NewController();

        var run = controller.RunActionAsync(0, null, jpeg);
        Assert.False(run.IsCompleted); // waiting for the presenter
        _clock.NextFolder(); // the user opens another folder meanwhile
        _sink.SetStatusText("new folder status");
        gate.SetResult();
        await run;

        Assert.Equal("new folder status", _sink.LastStatus);
    }

    [Fact]
    public async Task GroupRecycle_PermanentDeleteSettingOff_RefusesWithoutPromptAndDeletesNothing()
    {
        var (before, jpeg, raw, xmp, after) = LoadPairBetweenTwoPhotos(withXmp: true);
        _bin.NoBin = true;
        var controller = NewController(new AppSettings { AllowPermanentDeleteWithoutRecycleBin = false });

        await controller.RecycleAsync(null, jpeg);

        Assert.Empty(_dialog.Confirmations); // no "permanently delete" prompt when the setting is off
        Assert.Empty(_bin.Deleted);
        Assert.True(File.Exists(jpeg) && File.Exists(raw) && File.Exists(xmp!));
        Assert.Equal([before, jpeg, after], _catalog.Paths);
        Assert.Contains(Tr.CoreRecycleUnsupportedDrive("pair.jpg"), _sink.LastStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GroupRecycle_PermanentDeleteSettingOffWithConfirmBeforeDelete_OnlyAsksTheOrdinaryQuestionAndStillRefuses()
    {
        var (_, jpeg, raw, _, _) = LoadPairBetweenTwoPhotos();
        _bin.NoBin = true;
        var controller = NewController(new AppSettings { AllowPermanentDeleteWithoutRecycleBin = false, ConfirmBeforeDelete = true });

        await controller.RecycleAsync(null, jpeg);

        var prompt = Assert.Single(_dialog.Confirmations);
        Assert.Equal(Tr.DialogConfirmActionTitle, prompt.Title); // not the permanent-delete title
        Assert.Empty(_bin.Deleted);
        Assert.True(File.Exists(jpeg) && File.Exists(raw));
    }

    [Fact]
    public async Task GroupRecycle_PermanentDeleteSettingOn_PromptsPermanentlyAndDeletesAllMembers()
    {
        var (_, jpeg, raw, xmp, _) = LoadPairBetweenTwoPhotos(withXmp: true);
        _bin.NoBin = true;
        var controller = NewController(new AppSettings { AllowPermanentDeleteWithoutRecycleBin = true });

        await controller.RecycleAsync(null, jpeg);

        var prompt = Assert.Single(_dialog.Confirmations);
        Assert.Equal(Tr.DialogConfirmPermanentDeleteTitle, prompt.Title);
        Assert.Equal(new[] { jpeg, raw, xmp! }.Order(StringComparer.Ordinal), _bin.Deleted.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task GroupMove_PartnerVanishedExternally_MovesTheRemainingFileAndSaysWhichWasMissing()
    {
        var (before, jpeg, raw, _, after) = LoadPairBetweenTwoPhotos();
        File.Delete(raw);
        var controller = NewController();

        await controller.RunActionAsync(0, null, jpeg);

        Assert.False(File.Exists(jpeg));
        Assert.True(File.Exists(Path.Combine(_root, "Sorted", "pair.jpg")));
        Assert.Equal([before, after], _catalog.Paths);
        Assert.Equal(Tr.StatusGroupPartnerMissing("pair.jpg", "pair.cr2"), _sink.LastStatus);
    }

    private sealed class FakeBin : IRecycleBin
    {
        public List<string> Recycled { get; } = [];
        public List<string> Deleted { get; } = [];
        public bool NoBin { get; set; }
        public bool FitsAll { get; set; } = true;
        public string? FailRestoreFor { get; set; }

        public HashSet<string> NoBinExtensions { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool CanRecycle(string path) => !NoBin && !NoBinExtensions.Contains(Path.GetExtension(path));
        public bool FitsInRecycleBin(string path, long fileSize) => FitsAll;

        public void SendToRecycleBin(string path)
        {
            if (!CanRecycle(path)) throw new IOException("no bin");
            Recycled.Add(path);
            File.Delete(path);
        }

        public void DeletePermanently(string path)
        {
            Deleted.Add(path);
            File.Delete(path);
        }

        public bool TryRestore(string originalPath, long expectedSize, DateTime expectedLastWriteUtc)
        {
            if (string.Equals(originalPath, FailRestoreFor, StringComparison.OrdinalIgnoreCase)) return false;
            File.WriteAllBytes(originalPath, new byte[expectedSize]);
            File.SetLastWriteTimeUtc(originalPath, expectedLastWriteUtc);
            return true;
        }
    }

    private sealed class RecordingDialog(bool response) : IDialogService
    {
        public List<(string Title, string Message)> Confirmations { get; } = [];

        public bool ShowConfirmation(string title, string message)
        {
            Confirmations.Add((title, message));
            return response;
        }

        public void ShowMessage(string title, string message) { }
        public void ShowError(string title, string message) { }
        public string? PickFolder(string? initialFolder = null) => null;
        public bool ShowBatchReview(IReadOnlyList<string> paths) => false;
        public void ShowRecovery() { }
        public void ShowDiagnostics() { }
        public bool ShowSettings() => false;
        public void ShowBenchmark(string? folder = null) { }
        public void ShowSkippedFiles(IReadOnlyList<SkippedEntry> entries) { }
    }

    private sealed class RecordingSink : IFileActionSink
    {
        public string? LastStatus { get; private set; }
        public List<int> Presented { get; } = [];
        public List<string> LateStatuses { get; } = [];
        /// <summary>Optional: the task PresentAsync returns (a gate the test completes later); default completed.</summary>
        public Func<Task>? PresentTask { get; set; }
        public void SetStatusText(string status) => LastStatus = status;
        public void ShowLateActionStatus(string status) => LateStatuses.Add(status);
        public void OnCatalogChanged(string? removedPath) { }
        public Task PresentAsync(int index)
        {
            Presented.Add(index);
            return PresentTask?.Invoke() ?? Task.CompletedTask;
        }

        public void UpdateSessionPath(string currentPath) { }
        public void NotifyNavigationStateChanged() { }
    }
}

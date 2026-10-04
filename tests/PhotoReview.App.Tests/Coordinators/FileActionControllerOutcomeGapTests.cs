using System.IO;
using PhotoReview.App.Coordinators;
using PhotoReview.App.ViewModels;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Model;
using static PhotoReview.App.Tests.Coordinators.FileActionControllerMutationGapHarness;

namespace PhotoReview.App.Tests.Coordinators;

/// <summary>
/// Stryker round 1 (App): the outcome of a file action -- final status texts, restore of the catalog entry after an
/// unexpected throw, the Move-to / Copy-to pipeline and the review-order position an undone Move returns to.
/// Temp folder and fakes only.
/// </summary>
public sealed class FileActionControllerOutcomeGapTests : IDisposable
{
    private readonly FileActionControllerMutationGapHarness _h = new();

    public void Dispose() => _h.Dispose();

    private string Make(string name, int length = 4) => _h.Make(name, length);
    private string Root => _h.Root;
    private ReviewCatalog Catalog => _h.Catalog;
    private GenerationClock Clock => _h.Clock;
    private FakeBin Bin => _h.Bin;
    private RecordingSink Sink => _h.Sink;
    private HookFileSystem Fs => _h.Fs;

    private FileActionController NewController(AppSettings settings, IFolderPicker? picker = null) =>
        _h.NewController(settings, null, picker);

    private string MakeTarget()
    {
        var target = Path.Combine(Root, "Target");
        Directory.CreateDirectory(target);
        return target;
    }

    private static string Failed(string action) =>
        StatusFormatter.ActionFailed(action, UserFacingError.Describe(new InvalidOperationException("boom")));

    [Fact]
    public async Task RunAction_MoveAfterMovingTheFirstOfTwo_SessionPathIsTheNewCurrentPhoto()
    {
        var first = Make("a.jpg");
        var second = Make("b.jpg");
        Catalog.Reset([first, second]);
        var controller = NewController(Settings(MoveAction()));

        await controller.RunActionAsync(0, null, first);

        Assert.Equal([second], Sink.Sessions);
    }

    [Fact]
    public async Task RunAction_CopySingleFileWithOthersRemaining_StatusNamesTheCopyDestination()
    {
        var a = Make("a.jpg");
        var b = Make("b.jpg");
        Catalog.Reset([a, b]);
        var controller = NewController(Settings(
            new ReviewAction { Name = "Backup", Operation = FileOperationType.Copy, Destination = "Backup" }));

        await controller.RunActionAsync(0, null, a);

        Assert.Equal(StatusFormatter.CopiedTo("a.jpg"), Sink.LastStatus);
        Assert.Equal([a, b], Catalog.Paths);
        Assert.True(File.Exists(Path.Combine(Root, "Backup", "a.jpg")));
    }

    [Fact]
    public async Task RunAction_CopyCapture_StatusNamesTheFirstCopiedFile()
    {
        var jpeg = Make("pair.jpg");
        var raw = Make("pair.cr2");
        Catalog.Reset([new CatalogEntry(jpeg), new CatalogEntry(raw)], RawPairMode.PreferJpeg);
        var controller = NewController(new AppSettings
        {
            RawSupportEnabled = true,
            Actions = [new ReviewAction { Name = "Backup", Operation = FileOperationType.Copy, Destination = "Backup" }],
        });

        await controller.RunActionAsync(0, null, jpeg);

        Assert.Equal(1, Catalog.Count);
        var status = Assert.Single(Sink.Statuses);
        Assert.Equal(StatusFormatter.CopiedTo("pair.jpg"), status);
        Assert.True(File.Exists(Path.Combine(Root, "Backup", "pair.jpg")));
        Assert.True(File.Exists(Path.Combine(Root, "Backup", "pair.cr2")));
    }

    [Fact]
    public async Task RunAction_MoveWithOthersRemaining_WritesNoStatusAtAll()
    {
        var a = Make("a.jpg");
        var b = Make("b.jpg");
        Catalog.Reset([a, b]);
        var controller = NewController(Settings(MoveAction()));

        await controller.RunActionAsync(0, null, a);

        Assert.Empty(Sink.Statuses);
    }

    [Fact]
    public async Task RunAction_MovingTheLastPhoto_SaysAllImagesProcessedBeforeTheMoveEvenIfItFails()
    {
        var a = Make("a.jpg");
        Catalog.Reset([a]);
        Fs.ThrowOnMove = new InvalidOperationException("boom");
        var controller = NewController(Settings(MoveAction()));

        await controller.RunActionAsync(0, null, a);

        Assert.Equal([StatusFormatter.AllImagesProcessed(), Failed("MoveToSub")], Sink.Statuses);
        Assert.Equal([a], Catalog.Paths);
    }

    [Fact]
    public async Task RunAction_MoveFailsWithAnException_RestoresTheEntryAtItsPositionAndReportsFailure()
    {
        var a = Make("a.jpg");
        var b = Make("b.jpg");
        var c = Make("c.jpg");
        Catalog.Reset([a, b, c]);
        Fs.ThrowOnMove = new InvalidOperationException("boom");
        var controller = NewController(Settings(MoveAction()));

        await controller.RunActionAsync(0, null, b);

        Assert.Equal([a, b, c], Catalog.Paths);
        Assert.Equal(Failed("MoveToSub"), Sink.LastStatus);
        Assert.True(File.Exists(b));
    }

    [Fact]
    public async Task RunAction_MoveFailsWithAnExceptionForACapture_RestoresTheGroupedEntryAndReportsFailure()
    {
        var jpeg = Make("pair.jpg");
        var raw = Make("pair.cr2");
        var other = Make("z.jpg");
        Catalog.Reset([new CatalogEntry(jpeg), new CatalogEntry(raw), new CatalogEntry(other)], RawPairMode.PreferJpeg);
        Fs.ThrowOnMove = new InvalidOperationException("boom");
        var controller = NewController(new AppSettings { RawSupportEnabled = true, Actions = [MoveAction()] });

        await controller.RunActionAsync(0, null, jpeg);

        Assert.Equal(2, Catalog.Count);
        Assert.Equal(jpeg, Catalog.PathAt(0));
        Assert.NotNull(Catalog.Find(jpeg)?.CaptureGroup);
        Assert.Equal(Failed("MoveToSub"), Sink.LastStatus);
    }

    [Fact]
    public async Task RunAction_MoveFailsAndThePresentOfTheNextPhotoFaults_DoesNotThrowAndRestoresTheEntry()
    {
        var a = Make("a.jpg");
        var b = Make("b.jpg");
        Catalog.Reset([a, b]);
        Fs.ThrowOnMove = new InvalidOperationException("boom");
        Sink.PresentResult = () => Task.FromException(new InvalidOperationException("present failed"));
        var controller = NewController(Settings(MoveAction()));

        await controller.RunActionAsync(0, null, a);

        Assert.Equal([a, b], Catalog.Paths);
        Assert.StartsWith(Tr.StatusActionFailed("MoveToSub", "").TrimEnd(), Sink.LastStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAction_MoveFailsAfterTheFolderSwitched_DoesNotRestoreIntoTheNewCatalog()
    {
        var a = Make("a.jpg");
        var b = Make("b.jpg");
        var other = Make("other.jpg");
        Catalog.Reset([a, b]);
        Fs.OnMove = () => { Clock.NextFolder(); Catalog.Reset([other]); };
        Fs.ThrowOnMove = new InvalidOperationException("boom");
        var controller = NewController(Settings(MoveAction()));

        await controller.RunActionAsync(0, null, a);

        Assert.Equal([other], Catalog.Paths);
    }

    [Fact]
    public async Task RunAction_CaptureMoveFinishingAfterAFolderSwitch_ReportsTheLateMoveWithItsDestinationFolder()
    {
        var jpeg = Make("pair.jpg");
        var raw = Make("pair.cr2");
        var other = Make("other.jpg");
        Catalog.Reset([new CatalogEntry(jpeg), new CatalogEntry(raw)], RawPairMode.PreferJpeg);
        Fs.OnMove = () => { Clock.NextFolder(); Catalog.Reset([other]); };
        var controller = NewController(new AppSettings { RawSupportEnabled = true, Actions = [MoveAction()] });

        await controller.RunActionAsync(0, null, jpeg);

        var late = Assert.Single(Sink.LateStatuses);
        Assert.Equal(Tr.StatusLateMoveUndoable("pair.jpg", Path.Combine(Root, "Sorted")), late);
        Assert.Equal([other], Catalog.Paths);
    }

    [Fact]
    public async Task MoveToFolder_CompareSelectionDifferentFromCurrentAndLastFolderReused_MovesTheCompareSelection()
    {
        var current = Make("a.jpg");
        var selected = Make("b.jpg");
        Catalog.Reset([current, selected]);
        var target = MakeTarget();
        var controller = NewController(new AppSettings { MoveCopyReuseLastFolder = true, LastMoveToFolder = target });

        await controller.MoveOrCopyToFolderAsync(FileOperationType.Move, forcePicker: false, () => (selected, current));

        Assert.True(File.Exists(current));
        Assert.True(File.Exists(Path.Combine(target, "b.jpg")));
        Assert.Equal([current], Catalog.Paths);
    }

    [Fact]
    public async Task MoveToFolder_PickerReturnsAndTheCompareSelectionStillWinsOverCurrent_Proceeds()
    {
        var current = Make("a.jpg");
        var selected = Make("b.jpg");
        Catalog.Reset([current, selected]);
        var target = MakeTarget();
        var controller = NewController(new AppSettings(), new FixedPicker(target));

        await controller.MoveOrCopyToFolderAsync(FileOperationType.Move, forcePicker: true, () => (selected, current));

        Assert.True(File.Exists(Path.Combine(target, "b.jpg")));
        Assert.DoesNotContain(Tr.StatusMoveCopyToStateChanged(Tr.ActionMoveToFolderName), Sink.Statuses);
    }

    [Fact]
    public async Task MoveToFolder_PhotoAtLaterIndexStillInCatalogAfterPicker_Proceeds()
    {
        var a = Make("a.jpg");
        var b = Make("b.jpg");
        Catalog.Reset([a, b]);
        var target = MakeTarget();
        var controller = NewController(new AppSettings(), new FixedPicker(target));

        await controller.MoveOrCopyToFolderAsync(FileOperationType.Move, forcePicker: true, () => (null, b));

        Assert.True(File.Exists(Path.Combine(target, "b.jpg")));
        Assert.False(File.Exists(b));
    }

    [Fact]
    public async Task MoveToFolder_PhotoRemovedFromCatalogWhilePickerOpen_ReportsStateChangedAndKeepsTheFile()
    {
        var a = Make("a.jpg");
        var b = Make("b.jpg");
        Catalog.Reset([a, b]);
        var target = MakeTarget();
        var controller = NewController(new AppSettings(), new FixedPicker(target) { OnPick = () => Catalog.Remove(b) });

        await controller.MoveOrCopyToFolderAsync(FileOperationType.Move, forcePicker: true, () => (null, b));

        Assert.Equal(Tr.StatusMoveCopyToStateChanged(Tr.ActionMoveToFolderName), Sink.LastStatus);
        Assert.True(File.Exists(b));
        Assert.False(File.Exists(Path.Combine(target, "b.jpg")));
    }

    [Fact]
    public async Task MoveToFolder_SelectionChangesWhilePickerOpen_ReportsStateChangedAndKeepsTheFile()
    {
        var a = Make("a.jpg");
        var b = Make("b.jpg");
        Catalog.Reset([a, b]);
        var target = MakeTarget();
        var controller = NewController(new AppSettings(), new FixedPicker(target));
        var calls = 0;

        await controller.MoveOrCopyToFolderAsync(FileOperationType.Move, forcePicker: true,
            () => calls++ == 0 ? ((string?)b, (string?)a) : ((string?)null, (string?)a));

        Assert.Equal(Tr.StatusMoveCopyToStateChanged(Tr.ActionMoveToFolderName), Sink.LastStatus);
        Assert.True(File.Exists(b));
    }

    [Fact]
    public async Task MoveToFolder_MovingTheOnlyPhoto_FinalStatusStaysAllImagesProcessed()
    {
        var a = Make("a.jpg");
        Catalog.Reset([a]);
        var target = MakeTarget();
        var controller = NewController(new AppSettings { MoveCopyReuseLastFolder = true, LastMoveToFolder = target });

        await controller.MoveOrCopyToFolderAsync(FileOperationType.Move, forcePicker: false, () => (null, a));

        Assert.Equal(StatusFormatter.AllImagesProcessed(), Sink.LastStatus);
        Assert.DoesNotContain(Tr.StatusMovedToFolder("a.jpg", target), Sink.Statuses);
    }

    [Fact]
    public async Task MoveToFolder_WithMorePhotos_FinalStatusIsMovedToFolder()
    {
        var a = Make("a.jpg");
        var b = Make("b.jpg");
        Catalog.Reset([a, b]);
        var target = MakeTarget();
        var controller = NewController(new AppSettings { MoveCopyReuseLastFolder = true, LastMoveToFolder = target });

        await controller.MoveOrCopyToFolderAsync(FileOperationType.Move, forcePicker: false, () => (null, a));

        Assert.Equal(Tr.StatusMovedToFolder("a.jpg", target), Sink.LastStatus);
    }

    [Fact]
    public async Task MoveToFolder_FileSystemSaysDestinationFolderMissing_ReportsMissingEvenWhenItExistsOnDisk()
    {
        var a = Make("a.jpg");
        Catalog.Reset([a]);
        var target = MakeTarget();
        Fs.HideDirectory = target;
        var controller = NewController(new AppSettings(), new FixedPicker(target));

        await controller.MoveOrCopyToFolderAsync(FileOperationType.Move, forcePicker: true, () => (null, a));

        Assert.Equal(Tr.StatusMoveCopyToFolderMissing(Tr.ActionMoveToFolderName, target), Sink.LastStatus);
        Assert.True(File.Exists(a));
    }

    [Fact]
    public async Task UndoLast_MoveWhosePredecessorIsFirstInTheCatalog_ReturnsAfterItNotByName()
    {
        var z = Make("z.jpg");
        var a = Make("a.jpg");
        Catalog.Reset([z, a]);
        var controller = NewController(Settings(MoveAction()));
        await controller.RunActionAsync(0, null, a);

        var result = await controller.UndoLastAsync(Root);

        Assert.True(result!.Succeeded);
        Assert.Equal([z, a], Catalog.Paths);
    }

    [Fact]
    public async Task UndoLast_MoveWhosePredecessorLeftTheCatalog_FallsBackToNaturalNameOrder()
    {
        var a = Make("a.jpg");
        var b = Make("b.jpg");
        var c = Make("c.jpg");
        var d = Make("d.jpg");
        Catalog.Reset([a, b, c, d]);
        var controller = NewController(Settings(MoveAction()));
        await controller.RunActionAsync(0, null, c);
        Catalog.Remove(b);

        var result = await controller.UndoLastAsync(Root);

        Assert.True(result!.Succeeded);
        Assert.Equal([a, c, d], Catalog.Paths);
    }

    [Fact]
    public async Task UndoLast_PartialCaptureUndoWhileAnotherPhotoIsCurrent_SessionPathStaysOnTheCurrentPhoto()
    {
        var first = Make("a.jpg");
        var jpeg = Make("p.jpg");
        var raw = Make("p.cr2", 8);
        Catalog.Reset([new CatalogEntry(first), new CatalogEntry(jpeg), new CatalogEntry(raw)], RawPairMode.PreferJpeg);
        var controller = NewController(new AppSettings { RawSupportEnabled = true });
        await controller.RecycleAsync(null, jpeg);
        Sink.Reset();
        Bin.FailRestoreFor = raw;

        var result = await controller.UndoLastAsync(Root);

        Assert.False(result!.Succeeded);
        Assert.Equal([first], Sink.Sessions);
        Assert.Contains(jpeg, Catalog.Paths);
    }

    [Fact]
    public void RestoresOutsideFolder_CurrentFolderWithInvalidCharacters_CountsAsOutsideInsteadOfThrowing()
    {
        var result = new UndoResult(true, FileOperationType.Move, Path.Combine(Root, "a.jpg"), null, null);
        var invalidFolder = Root + new string((char)0, 1) + "bad";

        Assert.True(FileActionController.RestoresOutsideFolder(result, invalidFolder));
    }
}

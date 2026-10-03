using System.IO;
using PhotoReview.App.Coordinators;
using PhotoReview.App.ViewModels;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Model;
using static PhotoReview.App.Tests.Coordinators.FileActionControllerMutationGapHarness;

namespace PhotoReview.App.Tests.Coordinators;

/// <summary>
/// Stryker round 1 (App): which file a Compare selection acts on, "one prompt, not two", a state change while a
/// confirmation dialog is open (must never act on the wrong or a vanished file) and the guards of
/// <c>RunActionAsync</c>. Temp folder, fake bin and fake dialog only.
/// </summary>
public sealed class FileActionControllerConfirmGapTests : IDisposable
{
    private readonly FileActionControllerMutationGapHarness _h = new();

    public void Dispose() => _h.Dispose();

    private string Make(string name) => _h.Make(name);
    private string Root => _h.Root;
    private ReviewCatalog Catalog => _h.Catalog;
    private GenerationClock Clock => _h.Clock;
    private FakeBin Bin => _h.Bin;
    private RecordingSink Sink => _h.Sink;

    private FileActionController NewController(AppSettings settings, IDialogService? dialog = null, bool withFileSystem = true) =>
        _h.NewController(settings, dialog, null, withFileSystem);

    [Fact]
    public async Task RunAction_MoveWithCompareSelectionDifferentFromCurrent_MovesTheCompareSelectionOnly()
    {
        var current = Make("a.jpg");
        var selected = Make("b.jpg");
        Catalog.Reset([current, selected]);
        var controller = NewController(Settings(MoveAction()));

        await controller.RunActionAsync(0, selected, current);

        Assert.True(File.Exists(current));
        Assert.False(File.Exists(selected));
        Assert.True(File.Exists(Path.Combine(Root, "Sorted", "b.jpg")));
        Assert.Equal([current], Catalog.Paths);
    }

    [Fact]
    public async Task RunAction_RecycleConfirmWithCompareSelectionOnDriveWithoutBin_AsksOnlyThePermanentDeletePrompt()
    {
        var current = Make("a.jpg");
        var selected = Make("b.jpg");
        Catalog.Reset([current, selected]);
        Bin.NoBinFor.Add(selected);
        var dialog = new RecordingDialog(response: true);
        var controller = NewController(
            new AppSettings { AllowPermanentDeleteWithoutRecycleBin = true, Actions = [RecycleAction(confirm: true)] }, dialog);

        await controller.RunActionAsync(0, selected, current);

        Assert.Single(dialog.Confirmations);
        Assert.Equal(Tr.DialogConfirmPermanentDeleteTitle, dialog.Confirmations[0].Title);
        Assert.Contains(selected, Bin.Deleted);
        Assert.True(File.Exists(current));
    }

    [Fact]
    public async Task RunAction_RecycleConfirmWithCompareSelectionInCapture_AsksOnlyTheGroupPrompt()
    {
        var current = Make("a.jpg");
        var jpeg = Make("pair.jpg");
        var raw = Make("pair.cr2");
        Catalog.Reset([new CatalogEntry(current), new CatalogEntry(jpeg), new CatalogEntry(raw)], RawPairMode.PreferJpeg);
        var dialog = new RecordingDialog(response: true);
        var controller = NewController(
            new AppSettings { RawSupportEnabled = true, Actions = [RecycleAction(confirm: true)] }, dialog);

        await controller.RunActionAsync(0, jpeg, current);

        Assert.Single(dialog.Confirmations);
        Assert.Equal(Tr.DialogConfirmCompareGroupRecycleMessage("pair.jpg", "pair.jpg, pair.cr2"), dialog.Confirmations[0].Message);
        Assert.Contains(jpeg, Bin.Recycled);
        Assert.Contains(raw, Bin.Recycled);
        Assert.True(File.Exists(current));
    }

    [Fact]
    public async Task RunAction_RecycleConfirmGroupWithoutBinButPermanentDeleteOff_AsksTheGenericPromptOnce()
    {
        var jpeg = Make("pair.jpg");
        var raw = Make("pair.cr2");
        Catalog.Reset([new CatalogEntry(jpeg), new CatalogEntry(raw)], RawPairMode.PreferJpeg);
        Bin.NoBin = true;
        var dialog = new RecordingDialog(response: true);
        var controller = NewController(
            new AppSettings { RawSupportEnabled = true, AllowPermanentDeleteWithoutRecycleBin = false, Actions = [RecycleAction(confirm: true)] }, dialog);

        await controller.RunActionAsync(0, null, jpeg);

        Assert.Single(dialog.Confirmations);
        Assert.Equal(Tr.DialogConfirmActionTitle, dialog.Confirmations[0].Title);
        Assert.True(File.Exists(jpeg));
        Assert.True(File.Exists(raw));
    }

    [Fact]
    public async Task RunAction_RecycleConfirmCaptureWithoutFileSystem_AsksTheGenericPromptAndChangesNothing()
    {
        var jpeg = Make("pair.jpg");
        var raw = Make("pair.cr2");
        Catalog.Reset([new CatalogEntry(jpeg), new CatalogEntry(raw)], RawPairMode.PreferJpeg);
        var dialog = new RecordingDialog(response: true);
        var controller = NewController(
            new AppSettings { RawSupportEnabled = true, ConfirmBeforeDelete = true, Actions = [RecycleAction(confirm: true)] },
            dialog, withFileSystem: false);

        await controller.RunActionAsync(0, null, jpeg);

        Assert.Single(dialog.Confirmations);
        Assert.Equal(Tr.DialogConfirmActionTitle, dialog.Confirmations[0].Title);
        Assert.True(File.Exists(jpeg));
        Assert.True(File.Exists(raw));
        Assert.Empty(Bin.Recycled);
        Assert.Equal(1, Catalog.Count);
    }

    [Fact]
    public async Task RunAction_MoveConfirmedWithoutDialogService_RunsWithoutPrompt()
    {
        var a = Make("a.jpg");
        Catalog.Reset([a]);
        var controller = NewController(Settings(MoveAction(confirm: true)), dialog: null);

        await controller.RunActionAsync(0, null, a);

        Assert.True(File.Exists(Path.Combine(Root, "Sorted", "a.jpg")));
    }

    [Fact]
    public async Task RunAction_MoveWithoutConfirmFlagButDialogServicePresent_DoesNotAsk()
    {
        var a = Make("a.jpg");
        Catalog.Reset([a]);
        var dialog = new RecordingDialog(response: false);
        var controller = NewController(Settings(MoveAction(confirm: false)), dialog);

        await controller.RunActionAsync(0, null, a);

        Assert.Empty(dialog.Confirmations);
        Assert.True(File.Exists(Path.Combine(Root, "Sorted", "a.jpg")));
    }

    [Fact]
    public async Task RunAction_MoveConfirmDeclined_LeavesFileAndCatalogUntouched()
    {
        var a = Make("a.jpg");
        Catalog.Reset([a]);
        var controller = NewController(Settings(MoveAction(confirm: true)), new RecordingDialog(response: false));

        await controller.RunActionAsync(0, null, a);

        Assert.True(File.Exists(a));
        Assert.Equal([a], Catalog.Paths);
    }

    [Fact]
    public async Task RunAction_ConfirmThenCompareSelectionRemovedFromCatalogMeanwhile_DoesNotActOnTheFile()
    {
        var current = Make("a.jpg");
        var selected = Make("b.jpg");
        Catalog.Reset([current, selected]);
        var dialog = new RecordingDialog(response: true) { OnConfirm = () => Catalog.Remove(selected) };
        var controller = NewController(Settings(RecycleAction(confirm: true)), dialog);

        await controller.RunActionAsync(0, selected, current);

        Assert.True(File.Exists(selected));
        Assert.True(File.Exists(current));
        Assert.Empty(Bin.Recycled);
    }

    [Fact]
    public async Task RunAction_ConfirmThenFolderSwitchedMeanwhile_DoesNotActOnTheFile()
    {
        var a = Make("a.jpg");
        Catalog.Reset([a]);
        var dialog = new RecordingDialog(response: true) { OnConfirm = () => Clock.NextFolder() };
        var controller = NewController(Settings(RecycleAction(confirm: true)), dialog);

        await controller.RunActionAsync(0, null, a);

        Assert.True(File.Exists(a));
        Assert.Empty(Bin.Recycled);
    }

    [Fact]
    public async Task Recycle_ConfirmThenFolderSwitchedMeanwhile_DoesNotActOnTheFile()
    {
        var a = Make("a.jpg");
        Catalog.Reset([a]);
        var dialog = new RecordingDialog(response: true) { OnConfirm = () => Clock.NextFolder() };
        var controller = NewController(new AppSettings { ConfirmBeforeDelete = true }, dialog);

        await controller.RecycleAsync(null, a);

        Assert.True(File.Exists(a));
        Assert.Empty(Bin.Recycled);
    }

    [Fact]
    public async Task Recycle_ConfirmThenPhotoRemovedFromCatalogMeanwhile_DoesNotActOnTheFile()
    {
        var a = Make("a.jpg");
        var b = Make("b.jpg");
        Catalog.Reset([a, b]);
        var dialog = new RecordingDialog(response: true) { OnConfirm = () => Catalog.Remove(a) };
        var controller = NewController(new AppSettings { ConfirmBeforeDelete = true }, dialog);

        await controller.RecycleAsync(null, a);

        Assert.True(File.Exists(a));
        Assert.Empty(Bin.Recycled);
    }

    [Fact]
    public async Task Recycle_PermanentDeletePromptThenFolderSwitchedMeanwhile_DoesNotDeleteTheFile()
    {
        var a = Make("a.jpg");
        Catalog.Reset([a]);
        Bin.NoBin = true;
        var dialog = new RecordingDialog(response: true) { OnConfirm = () => Clock.NextFolder() };
        var controller = NewController(new AppSettings { AllowPermanentDeleteWithoutRecycleBin = true }, dialog);

        await controller.RecycleAsync(null, a);

        Assert.True(File.Exists(a));
        Assert.Empty(Bin.Deleted);
    }

    [Fact]
    public async Task Recycle_PermanentDeletePromptThenPhotoRemovedFromCatalogMeanwhile_DoesNotDeleteTheFile()
    {
        var a = Make("a.jpg");
        var b = Make("b.jpg");
        Catalog.Reset([a, b]);
        Bin.NoBin = true;
        var dialog = new RecordingDialog(response: true) { OnConfirm = () => Catalog.Remove(a) };
        var controller = NewController(new AppSettings { AllowPermanentDeleteWithoutRecycleBin = true }, dialog);

        await controller.RecycleAsync(null, a);

        Assert.True(File.Exists(a));
        Assert.Empty(Bin.Deleted);
    }

    [Fact]
    public async Task Recycle_GroupPromptThenFolderSwitchedMeanwhile_DoesNotRecycleTheCapture()
    {
        var jpeg = Make("pair.jpg");
        var raw = Make("pair.cr2");
        Catalog.Reset([new CatalogEntry(jpeg), new CatalogEntry(raw)], RawPairMode.PreferJpeg);
        var dialog = new RecordingDialog(response: true) { OnConfirm = () => Clock.NextFolder() };
        var controller = NewController(new AppSettings { RawSupportEnabled = true, ConfirmBeforeDelete = true }, dialog);

        await controller.RecycleAsync(null, jpeg);

        Assert.True(File.Exists(jpeg));
        Assert.True(File.Exists(raw));
        Assert.Empty(Bin.Recycled);
    }

    [Fact]
    public async Task Recycle_GroupPromptThenCaptureRemovedFromCatalogMeanwhile_ReturnsSilentlyWithoutGroupChangedStatus()
    {
        var jpeg = Make("pair.jpg");
        var raw = Make("pair.cr2");
        var other = Make("z.jpg");
        Catalog.Reset([new CatalogEntry(jpeg), new CatalogEntry(raw), new CatalogEntry(other)], RawPairMode.PreferJpeg);
        var dialog = new RecordingDialog(response: true) { OnConfirm = () => Catalog.Remove(jpeg) };
        var controller = NewController(new AppSettings { RawSupportEnabled = true, ConfirmBeforeDelete = true }, dialog);

        await controller.RecycleAsync(null, jpeg);

        Assert.True(File.Exists(jpeg));
        Assert.Empty(Bin.Recycled);
        Assert.Empty(Sink.Statuses);
    }

    [Fact]
    public async Task RunAction_IndexEqualToActionCount_DoesNothing()
    {
        var a = Make("a.jpg");
        Catalog.Reset([a]);
        var controller = NewController(Settings(MoveAction()));

        await controller.RunActionAsync(1, null, a);

        Assert.True(File.Exists(a));
        Assert.Empty(Sink.Statuses);
    }

    [Fact]
    public async Task RunAction_UndefinedOperation_ReportsInvalidOperationAndTouchesNothing()
    {
        var a = Make("a.jpg");
        Catalog.Reset([a]);
        var controller = NewController(Settings(
            new ReviewAction { Name = "Broken", Operation = (FileOperationType)99, Destination = "Sorted" }));

        await controller.RunActionAsync(0, null, a);

        Assert.Equal([StatusFormatter.ActionInvalidOperation("Broken")], Sink.Statuses);
        Assert.True(File.Exists(a));
        Assert.Equal([a], Catalog.Paths);
    }

    [Fact]
    public async Task RunAction_RecycleProfile_RecyclesTheCurrentPhotoWithoutADestination()
    {
        var a = Make("a.jpg");
        var b = Make("b.jpg");
        Catalog.Reset([a, b]);
        var controller = NewController(Settings(RecycleAction()));

        await controller.RunActionAsync(0, null, a);

        Assert.Contains(a, Bin.Recycled);
        Assert.DoesNotContain(StatusFormatter.ActionNoDestination("DeleteIt"), Sink.Statuses);
        Assert.Equal([b], Catalog.Paths);
    }

    [Fact]
    public async Task RunAction_MoveProfileWithBlankDestination_ReportsNoDestinationAndKeepsTheFile()
    {
        var a = Make("a.jpg");
        Catalog.Reset([a]);
        var controller = NewController(Settings(
            new ReviewAction { Name = "NoDest", Operation = FileOperationType.Move, Destination = "  " }));

        await controller.RunActionAsync(0, null, a);

        Assert.Equal([StatusFormatter.ActionNoDestination("NoDest")], Sink.Statuses);
        Assert.True(File.Exists(a));
        Assert.Equal([a], Catalog.Paths);
    }
}

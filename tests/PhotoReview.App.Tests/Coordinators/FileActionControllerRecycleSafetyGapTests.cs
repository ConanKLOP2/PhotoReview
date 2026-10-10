using System.IO;
using PhotoReview.App.Coordinators;
using PhotoReview.Core;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.IO;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Model;

namespace PhotoReview.App.Tests.Coordinators;

/// <summary>
/// Audit B (mutation gaps C2/C3): the permanent-delete permission that <see cref="FileActionController"/> hands to the service
/// must be exactly what the user was asked about, and a capture is refused up front when ANY member lacks a Recycle Bin.
/// The fake bin is shared-nothing: it never touches the real Recycle Bin.
/// </summary>
public sealed class FileActionControllerRecycleSafetyGapTests : IDisposable
{
    private readonly FileActionControllerMutationGapHarness _h = new();

    public void Dispose() => _h.Dispose();

    /// <summary>
    /// Reports a Recycle Bin for the first <paramref name="binAnswers"/> CanRecycle queries and none afterwards: the drive loses
    /// its bin (USB stick pulled, bin turned off) between the controller's own checks and the service's check.
    /// </summary>
    private sealed class VanishingBin(int binAnswers) : IRecycleBin
    {
        public int CanRecycleCalls { get; private set; }
        public int BinAnswers => binAnswers;
        public List<string> Recycled { get; } = [];
        public List<string> Deleted { get; } = [];

        public bool CanRecycle(string path) => ++CanRecycleCalls <= binAnswers;
        public bool FitsInRecycleBin(string path, long fileSize) => true;

        public void SendToRecycleBin(string path)
        {
            Recycled.Add(path);
            File.Delete(path);
        }

        public void DeletePermanently(string path)
        {
            Deleted.Add(path);
            File.Delete(path);
        }

        public bool TryRestore(string originalPath, long expectedSize, DateTime expectedLastWriteUtc) => false;
    }

    private FileActionController NewController(IRecycleBin bin, AppSettings settings, IDialogService dialog)
    {
        var journal = new OperationJournal(new AppPaths(_h.Root), _h.Fs, new SystemClock());
        var fileActions = new FileActionService(journal, _h.Fs, new SystemClock(), bin);
        var undo = new UndoService(journal, _h.Fs, bin, fileActions);
        return new FileActionController(
            _h.Catalog, _h.Clock, fileActions, undo, dialog, preloadController: null,
            ManagedNaturalComparer.Instance, () => settings, _h.Sink, folderPicker: null, _h.Fs);
    }

    private (string Jpeg, string Raw) LoadPair()
    {
        var jpeg = _h.Make("pair.jpg");
        var raw = _h.Make("pair.cr2", 8);
        _h.Catalog.Reset([new CatalogEntry(jpeg), new CatalogEntry(raw)], RawPairMode.PreferJpeg, null);
        return (jpeg, raw);
    }

    [Fact]
    public async Task GroupRecycle_RaceAfterTheUpFrontCheckWithSettingOff_NeverAsksPermanentDeleteNorDeletes()
    {
        var (jpeg, raw) = LoadPair();
        var bin = new VanishingBin(binAnswers: 2); // the up-front refusal check queries both members
        var dialog = new FileActionControllerMutationGapHarness.RecordingDialog(response: true);
        var controller = NewController(bin, new AppSettings { AllowPermanentDeleteWithoutRecycleBin = false, ConfirmBeforeDelete = false }, dialog);

        await controller.RecycleAsync(null, jpeg);

        Assert.True(bin.CanRecycleCalls > bin.BinAnswers); // the service really saw the bin vanish
        Assert.Empty(dialog.Confirmations); // the permanent-delete prompt exists only with the setting ON
        Assert.Empty(bin.Deleted);
        Assert.Empty(bin.Recycled);
        Assert.True(File.Exists(jpeg) && File.Exists(raw));
    }

    [Fact]
    public async Task GroupRecycle_RaceAfterTheUpFrontCheckAfterTheGenericGroupPrompt_StaysNonPermanent()
    {
        var (jpeg, raw) = LoadPair();
        var bin = new VanishingBin(binAnswers: 2);
        var dialog = new FileActionControllerMutationGapHarness.RecordingDialog(response: true);
        var controller = NewController(bin, new AppSettings { AllowPermanentDeleteWithoutRecycleBin = false, ConfirmBeforeDelete = true }, dialog);

        await controller.RecycleAsync(null, jpeg);

        Assert.True(bin.CanRecycleCalls > bin.BinAnswers);
        // The user confirmed a plain "recycle this capture", never a permanent delete.
        Assert.Equal(Tr.DialogConfirmActionTitle, Assert.Single(dialog.Confirmations).Title);
        Assert.Empty(bin.Deleted);
        Assert.Empty(bin.Recycled);
        Assert.True(File.Exists(jpeg) && File.Exists(raw));
    }

    [Fact]
    public async Task GroupRecycle_RaceWithSettingOnButNoPromptedMember_DoesNotGrantPermanentDelete()
    {
        var (jpeg, raw) = LoadPair();
        var bin = new VanishingBin(binAnswers: 2); // permanentGroupPaths query: both members still have a bin
        var dialog = new FileActionControllerMutationGapHarness.RecordingDialog(response: true);
        var controller = NewController(bin, new AppSettings { AllowPermanentDeleteWithoutRecycleBin = true, ConfirmBeforeDelete = false }, dialog);

        await controller.RecycleAsync(null, jpeg);

        Assert.True(bin.CanRecycleCalls > bin.BinAnswers);
        Assert.Empty(dialog.Confirmations); // nothing was asked, so nothing may be permanent
        Assert.Empty(bin.Deleted);
        Assert.True(File.Exists(jpeg) && File.Exists(raw));
    }

    [Fact]
    public async Task SingleRecycle_RaceAfterTheUpFrontCheckWithSettingOff_NeverDeletesPermanently()
    {
        var jpeg = _h.Make("solo.jpg");
        _h.Catalog.Reset([jpeg]);
        var bin = new VanishingBin(binAnswers: 1); // the up-front refusal check queries the file once
        var dialog = new FileActionControllerMutationGapHarness.RecordingDialog(response: true);
        var controller = NewController(bin, new AppSettings { AllowPermanentDeleteWithoutRecycleBin = false }, dialog);

        await controller.RecycleAsync(null, jpeg);

        Assert.True(bin.CanRecycleCalls > bin.BinAnswers);
        Assert.Empty(dialog.Confirmations);
        Assert.Empty(bin.Deleted);
        Assert.True(File.Exists(jpeg));
    }

    [Fact]
    public async Task GroupRecycle_OnlyANonSelectedMemberLacksABin_RefusesUpFrontNamingThatMemberWithoutAnyPrompt()
    {
        var (jpeg, raw) = LoadPair();
        _h.Bin.NoBinFor.Add(raw); // the selected JPEG has a bin, its RAW partner does not
        var dialog = new FileActionControllerMutationGapHarness.RecordingDialog(response: true);
        var controller = _h.NewController(
            new AppSettings { AllowPermanentDeleteWithoutRecycleBin = false, ConfirmBeforeDelete = true }, dialog);

        await controller.RecycleAsync(null, jpeg);

        Assert.Empty(dialog.Confirmations); // no "delete?" question for a delete that cannot happen
        var error = Assert.Single(dialog.Errors);
        Assert.Equal(Tr.DialogRecycleNoBinTitle, error.Title);
        Assert.Contains("pair.cr2", error.Message);
        Assert.Equal(Tr.CoreRecycleUnsupportedDrive("pair.cr2"), _h.Sink.LastStatus);
        Assert.Empty(_h.Bin.Recycled);
        Assert.Empty(_h.Bin.Deleted);
        Assert.True(File.Exists(jpeg) && File.Exists(raw));
    }
}

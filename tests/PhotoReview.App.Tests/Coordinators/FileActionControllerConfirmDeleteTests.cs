using System.IO;
using PhotoReview.App.Coordinators;
using PhotoReview.Core;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.FileActions;
using PhotoReview.Core.IO;
using PhotoReview.Core.Model;
using PhotoReview.Core.Settings;

namespace PhotoReview.App.Tests.Coordinators;

/// <summary>
/// Q-R44: <see cref="AppSettings.ConfirmBeforeDelete"/> (default off) gates a Yes/No confirmation in front of the
/// general Recycle path (<see cref="FileActionController.RecycleAsync"/>). Off by default keeps today's behaviour
/// (no prompt); when on, declining the prompt must leave the file and catalog untouched.
/// </summary>
public sealed class FileActionControllerConfirmDeleteTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PhotoReview_ConfirmDelete_" + Guid.NewGuid().ToString("N"));
    private readonly GenerationClock _clock = new();
    private readonly ReviewCatalog _catalog = new();
    private readonly RecordingSink _sink = new();
    private readonly FakeBin _bin = new();

    public FileActionControllerConfirmDeleteTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string Make(string name)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, [1, 2, 3, 4]);
        return path;
    }

    private FileActionController NewController(AppSettings settings, IDialogService? dialog)
    {
        var fs = new PhysicalFileSystem();
        var journal = new OperationJournal(new AppPaths(_root), fs, new SystemClock());
        var fileActions = new FileActionService(journal, fs, new SystemClock(), _bin);
        var undo = new UndoService(journal, fs, _bin, fileActions);
        return new FileActionController(
            _catalog, _clock, fileActions, undo, dialog, preloadController: null,
            ManagedNaturalComparer.Instance, () => settings, _sink, fileSystem: fs);
    }

    [Fact(DisplayName = "ConfirmBeforeDelete off (default): Recycle runs without asking")]
    public async Task ConfirmBeforeDelete_Off_RecyclesWithoutPrompt()
    {
        var a = Make("a.jpg");
        _catalog.Reset([a]);
        var dialog = new RecordingDialog(response: false); // would refuse if ever asked
        var controller = NewController(new AppSettings(), dialog);

        await controller.RecycleAsync(null, a);

        Assert.Empty(dialog.Confirmations);
        Assert.Contains(a, _bin.Recycled);
        Assert.Equal(0, _catalog.Count);
    }

    [Fact(DisplayName = "ConfirmBeforeDelete on, confirmed: Recycle runs after the prompt")]
    public async Task ConfirmBeforeDelete_On_Confirmed_Recycles()
    {
        var a = Make("a.jpg");
        _catalog.Reset([a]);
        var dialog = new RecordingDialog(response: true);
        var controller = NewController(new AppSettings { ConfirmBeforeDelete = true }, dialog);

        await controller.RecycleAsync(null, a);

        Assert.Single(dialog.Confirmations);
        Assert.Contains(a, _bin.Recycled);
        Assert.Equal(0, _catalog.Count);
    }

    [Fact(DisplayName = "ConfirmBeforeDelete on, declined: nothing happens")]
    public async Task ConfirmBeforeDelete_On_Declined_DoesNothing()
    {
        var a = Make("a.jpg");
        _catalog.Reset([a]);
        var dialog = new RecordingDialog(response: false);
        var controller = NewController(new AppSettings { ConfirmBeforeDelete = true }, dialog);

        await controller.RecycleAsync(null, a);

        Assert.Single(dialog.Confirmations);
        Assert.Empty(_bin.Recycled);
        Assert.True(File.Exists(a));
        Assert.Equal(1, _catalog.Count);
    }

    [Fact(DisplayName = "ConfirmBeforeDelete on, but the permanent-delete prompt already applies: only one prompt is shown")]
    public async Task ConfirmBeforeDelete_On_WithPermanentDeletePrompt_AsksOnlyOnce()
    {
        var a = Make("a.jpg");
        _catalog.Reset([a]);
        _bin.NoBin = true; // this volume has no Recycle Bin -> the narrow Q-R8 prompt applies instead
        var dialog = new RecordingDialog(response: true);
        var controller = NewController(new AppSettings { ConfirmBeforeDelete = true, AllowPermanentDeleteWithoutRecycleBin = true }, dialog);

        await controller.RecycleAsync(null, a);

        Assert.Single(dialog.Confirmations); // not two
        Assert.Contains(a, _bin.Deleted);
    }

    private sealed class FakeBin : IRecycleBin
    {
        public List<string> Recycled { get; } = [];
        public List<string> Deleted { get; } = [];
        public bool NoBin { get; set; }

        public bool CanRecycle(string path) => !NoBin;

        public void SendToRecycleBin(string path)
        {
            if (NoBin) throw new IOException("no bin");
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
            File.WriteAllBytes(originalPath, [1, 2, 3, 4]);
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
        public void SetStatusText(string status) { }
        public void ShowLateActionStatus(string status) { }
        public void OnCatalogChanged(string? removedPath) { }
        public Task PresentAsync(int index) => Task.CompletedTask;
        public void UpdateSessionPath(string currentPath) { }
        public void NotifyNavigationStateChanged() { }
    }
}

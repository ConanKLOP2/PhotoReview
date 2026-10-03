using System.IO;
using PhotoReview.App.Coordinators;
using PhotoReview.App.Tests.ViewModels;
using PhotoReview.Core;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.IO;
using PhotoReview.Core.Model;

namespace PhotoReview.App.Tests.Coordinators;

/// <summary>Shared temp folder, fakes and controller factory (composition: the fakes derive from an internal test helper, so a public base class is not possible) for the <see cref="FileActionController"/> mutation-gap tests.</summary>
internal sealed class FileActionControllerMutationGapHarness : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "PhotoReview_MutGapCtl_" + Guid.NewGuid().ToString("N"));
    public GenerationClock Clock { get; } = new();
    public ReviewCatalog Catalog { get; } = new();
    public RecordingSink Sink { get; } = new();
    public FakeBin Bin { get; } = new();
    public HookFileSystem Fs { get; } = new(new PhysicalFileSystem());

    public FileActionControllerMutationGapHarness() => Directory.CreateDirectory(Root);

    public void Dispose()
    {
        try { Directory.Delete(Root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public string Make(string name, int length = 4)
    {
        var path = Path.Combine(Root, name);
        File.WriteAllBytes(path, new byte[length]);
        return path;
    }

    public FileActionController NewController(AppSettings settings, IDialogService? dialog = null, IFolderPicker? picker = null,
        bool withFileSystem = true)
    {
        var journal = new OperationJournal(new AppPaths(Root), Fs, new SystemClock());
        var fileActions = new FileActionService(journal, Fs, new SystemClock(), Bin);
        var undo = new UndoService(journal, Fs, Bin, fileActions);
        return new FileActionController(
            Catalog, Clock, fileActions, undo, dialog, preloadController: null,
            ManagedNaturalComparer.Instance, () => settings, Sink, picker, withFileSystem ? Fs : null);
    }

    public static AppSettings Settings(params ReviewAction[] actions) => new() { Actions = [.. actions] };

    public static ReviewAction MoveAction(bool confirm = false) =>
        new() { Name = "MoveToSub", Operation = FileOperationType.Move, Destination = "Sorted", Confirm = confirm };

    public static ReviewAction RecycleAction(bool confirm = false) =>
        new() { Name = "DeleteIt", Operation = FileOperationType.Recycle, Confirm = confirm };

    public sealed class HookFileSystem(IFileSystem inner) : MainViewModelFileActionTests.DelegatingFileSystem(inner)
    {
        public Exception? ThrowOnMove { get; set; }
        public Action? OnMove { get; set; }
        public string? HideDirectory { get; set; }

        public override void Move(string source, string destination)
        {
            OnMove?.Invoke();
            if (ThrowOnMove is not null) throw ThrowOnMove;
            base.Move(source, destination);
        }

        public override bool DirectoryExists(string path) =>
            !string.Equals(path, HideDirectory, StringComparison.OrdinalIgnoreCase) && base.DirectoryExists(path);
    }

    public sealed class FixedPicker(string? result) : IFolderPicker
    {
        public Action? OnPick { get; set; }

        public string? PickFolder(string title, string? initialFolder)
        {
            OnPick?.Invoke();
            return result;
        }
    }

    public sealed class FakeBin : IRecycleBin
    {
        public List<string> Recycled { get; } = [];
        public List<string> Deleted { get; } = [];
        public bool NoBin { get; set; }
        public HashSet<string> NoBinFor { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string? FailRestoreFor { get; set; }

        public bool CanRecycle(string path) => !NoBin && !NoBinFor.Contains(path);
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

        public bool TryRestore(string originalPath, long expectedSize, DateTime expectedLastWriteUtc)
        {
            if (string.Equals(originalPath, FailRestoreFor, StringComparison.OrdinalIgnoreCase)) return false;
            File.WriteAllBytes(originalPath, new byte[expectedSize]);
            File.SetLastWriteTimeUtc(originalPath, expectedLastWriteUtc);
            return true;
        }
    }

    public sealed class RecordingDialog(bool response) : IDialogService
    {
        public List<(string Title, string Message)> Confirmations { get; } = [];
        public Action? OnConfirm { get; set; }

        public bool ShowConfirmation(string title, string message)
        {
            Confirmations.Add((title, message));
            OnConfirm?.Invoke();
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

    public sealed class RecordingSink : IFileActionSink
    {
        public List<string> Sessions { get; } = [];
        public List<string> Statuses { get; } = [];
        public Func<Task>? PresentResult { get; set; }

        public string? LastStatus => Statuses.Count > 0 ? Statuses[^1] : null;
        public void Reset() { Sessions.Clear(); Statuses.Clear(); }
        public void SetStatusText(string status) => Statuses.Add(status);
        public void ShowLateActionStatus(string status) { }
        public void OnCatalogChanged(string? removedPath) { }
        public Task PresentAsync(int index) => PresentResult?.Invoke() ?? Task.CompletedTask;
        public void UpdateSessionPath(string currentPath) => Sessions.Add(currentPath);
        public void NotifyNavigationStateChanged() { }
    }
}

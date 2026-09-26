using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PhotoReview.App.Coordinators;
using PhotoReview.App.Services;
using PhotoReview.App.ViewModels;
using PhotoReview.Core;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.FileActions;
using PhotoReview.Core.IO;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Model;
using PhotoReview.TestSupport;

namespace PhotoReview.App.Tests.ViewModels;

/// <summary>
/// "Move to… / Copy to…" (M / Y): the controller flow with a fake folder picker, run through MainViewModel and the real
/// FileActionService/UndoService/OperationJournal on temp folders (fake Recycle Bin; nothing outside the temp root is touched).
/// </summary>
[Trait("Category", "HotPath")]
[Collection("GlobalState")]
public sealed class MainViewModelMoveCopyToTests : IDisposable
{
    private readonly TempRoot _root = new("movecopyto");
    private readonly ReviewCatalog _catalog = new();
    private readonly GenerationClock _clock = new();
    private readonly ReviewMetrics _metrics = new();
    private readonly CompareViewModel _compare = new();
    private readonly ViewerState _viewer = new();
    private readonly PhysicalFileSystem _fileSystem = new();
    private readonly FakeFolderPicker _picker = new();
    private readonly SettingsStore _settingsStore;
    private readonly SessionStore _sessionStore;
    private readonly OperationJournal _journal;
    private readonly PreviewImageService _previewService;
    private readonly ThumbnailCache _thumbnailCache;
    private readonly AppPaths _appPaths;

    public MainViewModelMoveCopyToTests()
    {
        _appPaths = new AppPaths(_root.Dir("appdata"));
        _journal = new OperationJournal(_appPaths, _fileSystem, new SystemClock());
        _sessionStore = new SessionStore(_appPaths, _fileSystem);
        _settingsStore = new SettingsStore(_appPaths, _fileSystem, new NullLog());
        _settingsStore.Save(new AppSettings { LoadingMode = LoadingMode.Preview });
        _previewService = new PreviewImageService(_metrics, () => false, () => new DecodeBox(256, 0),
            capacityBytes: 16 * 1024 * 1024, currentBackend: () => DecoderBackend.Wpf, disableDiskCacheOverride: true);
        _thumbnailCache = new ThumbnailCache(diskDirectory: _root.Combine("thumbs"), maxRamBytes: 4 * 1024 * 1024, persistNewThumbnails: false);
    }

    public void Dispose()
    {
        _thumbnailCache.Dispose();
        _root.Dispose();
    }

    private AppSettings Settings => _settingsStore.Current;

    private (MainViewModel Vm, FileActionService FileActions, UndoService Undo) CreateViewModel(Func<string, string, Task>? moveOverride = null)
    {
        var recycleBin = new NeverRecycleBin();
        var fileActions = new FileActionService(_journal, _fileSystem, new SystemClock(), recycleBin, moveOverride);
        var undo = new UndoService(_journal, _fileSystem, recycleBin, fileActions);
        MainViewModel? vm = null;
        var preload = new NoPreload();
        var presenter = new ImagePresenter(_catalog, _clock, _previewService, _thumbnailCache, preload, _compare, new FileHashService(),
            _metrics, () => _settingsStore.Current, _sessionStore, new NullPresentationSink(), _fileSystem, getSession: () => vm?.Session);
        var coordinator = new FolderLoadCoordinator(_catalog, _clock, new NoExplorerOrder(), _fileSystem, _sessionStore, _settingsStore,
            new ForwardingFolderSink(() => vm!));
        vm = new MainViewModel(_catalog, _clock, coordinator, presenter, _viewer, _compare, _settingsStore, _sessionStore, _fileSystem,
            fileActions, undo, new NoDialogService(), new FileHashService(), _previewService, _thumbnailCache,
            new SessionWriter(_sessionStore, FileLog.Default), preloadController: preload, naturalComparer: ManagedNaturalComparer.Instance,
            folderPicker: _picker);
        return (vm, fileActions, undo);
    }

    /// <summary>photos/1.jpg, photos/2.jpg and an empty sibling folder "dest"; the view model has photos open on 1.jpg.</summary>
    private async Task<(MainViewModel Vm, FileActionService FileActions, UndoService Undo, string Img1, string Img2, string Dest)> OpenAlbumAsync(Func<string, string, Task>? moveOverride = null)
    {
        var img1 = _root.File(Path.Combine("photos", "1.jpg"), TestImages.OpaquePng);
        var img2 = _root.File(Path.Combine("photos", "2.jpg"), TestImages.OpaquePng);
        var dest = _root.Dir("dest");
        var (vm, fileActions, undo) = CreateViewModel(moveOverride);
        await vm.OpenFolderAsync(_root.Combine("photos"));
        Assert.Equal(img1, vm.Catalog.Current?.Path);
        return (vm, fileActions, undo, img1, img2, dest);
    }

    [Fact]
    public async Task MoveTo_PickedFolder_MovesAdvancesRemembersFolderAndUndoRestores()
    {
        var (vm, _, undo, img1, img2, dest) = await OpenAlbumAsync();
        _picker.Result = dest;

        await vm.MoveToFolderAsync();

        var moved = Path.Combine(dest, "1.jpg");
        Assert.True(File.Exists(moved));
        Assert.False(File.Exists(img1));
        Assert.Equal(img2, vm.Catalog.Current?.Path);
        Assert.Equal(1, vm.TotalFiles);
        // The picker started at the photo folder's parent (no last folder yet) and was titled for Move.
        var call = Assert.Single(_picker.Calls);
        Assert.Equal(Tr.DialogMoveToFolderTitle, call.Title);
        Assert.Equal(_root.Path, call.InitialFolder);
        Assert.Equal(Tr.StatusMovedToFolder("1.jpg", dest), vm.StatusText);
        // Remembered and persisted: a fresh store reads it back from config.json.
        Assert.Equal(dest, new SettingsStore(_appPaths, _fileSystem, new NullLog()).Load().LastMoveToFolder);
        Assert.Null(Settings.LastCopyToFolder);

        Assert.Equal(1, undo.MoveHistoryCount);
        await vm.UndoAsync();
        Assert.True(File.Exists(img1));
        Assert.False(File.Exists(moved));
        Assert.Equal(2, vm.TotalFiles);
    }

    [Fact]
    public async Task CopyTo_PickedFolder_CopiesKeepsSourceAndDoesNotRegisterUndo()
    {
        var (vm, _, undo, img1, _, dest) = await OpenAlbumAsync();
        _picker.Result = dest;

        await vm.CopyToFolderAsync();

        Assert.True(File.Exists(Path.Combine(dest, "1.jpg")));
        Assert.True(File.Exists(img1));
        Assert.Equal(img1, vm.Catalog.Current?.Path);
        Assert.Equal(2, vm.TotalFiles);
        Assert.Equal(Tr.DialogCopyToFolderTitle, Assert.Single(_picker.Calls).Title);
        Assert.Equal(Tr.StatusCopiedToFolder("1.jpg", dest), vm.StatusText);
        Assert.Equal(dest, Settings.LastCopyToFolder);
        Assert.Null(Settings.LastMoveToFolder);
        Assert.Equal(0, undo.MoveHistoryCount);
    }

    [Fact]
    public async Task MoveTo_Cancelled_IsNoOp()
    {
        var (vm, fileActions, _, img1, _, _) = await OpenAlbumAsync();
        _picker.Result = null;
        var statusBefore = vm.StatusText;

        await vm.MoveToFolderAsync();

        Assert.Single(_picker.Calls);
        Assert.True(File.Exists(img1));
        Assert.Equal(2, vm.TotalFiles);
        Assert.Equal(img1, vm.Catalog.Current?.Path);
        Assert.Equal(statusBefore, vm.StatusText);
        Assert.Null(Settings.LastMoveToFolder);
        Assert.False(vm.IsFileActionInProgress);
        Assert.False(fileActions.IsBusy);
    }

    [Fact]
    public async Task MoveTo_ReuseLastOn_SkipsPickerAndMovesToLastFolder()
    {
        var (vm, _, _, img1, _, dest) = await OpenAlbumAsync();
        Settings.MoveCopyReuseLastFolder = true;
        Settings.LastMoveToFolder = dest;

        await vm.MoveToFolderAsync();

        Assert.Empty(_picker.Calls);
        Assert.True(File.Exists(Path.Combine(dest, "1.jpg")));
        Assert.False(File.Exists(img1));
        Assert.Equal(Tr.StatusMovedToFolder("1.jpg", dest), vm.StatusText);
    }

    [Fact]
    public async Task MoveTo_ReuseLastOnButForced_OpensPickerAtLastFolder()
    {
        var (vm, _, _, img1, _, dest) = await OpenAlbumAsync();
        var other = _root.Dir("other");
        Settings.MoveCopyReuseLastFolder = true;
        Settings.LastMoveToFolder = dest;
        _picker.Result = other;

        await vm.MoveToFolderAsync(forcePicker: true);

        Assert.Equal(dest, Assert.Single(_picker.Calls).InitialFolder);
        Assert.True(File.Exists(Path.Combine(other, "1.jpg")));
        Assert.False(File.Exists(Path.Combine(dest, "1.jpg")));
        Assert.False(File.Exists(img1));
        Assert.Equal(other, Settings.LastMoveToFolder);
    }

    [Fact]
    public async Task MoveTo_ReuseLastOff_OpensPickerAtLastFolder()
    {
        var (vm, _, _, _, _, dest) = await OpenAlbumAsync();
        Settings.LastMoveToFolder = dest;
        _picker.Result = null;

        await vm.MoveToFolderAsync();

        Assert.Equal(dest, Assert.Single(_picker.Calls).InitialFolder);
    }

    [Fact]
    public async Task MoveTo_ReuseLastButFolderDeleted_OpensPickerAtPhotoParent()
    {
        var (vm, _, _, img1, _, _) = await OpenAlbumAsync();
        Settings.MoveCopyReuseLastFolder = true;
        Settings.LastMoveToFolder = _root.Combine("gone");
        _picker.Result = null;

        await vm.MoveToFolderAsync();

        Assert.Equal(_root.Path, Assert.Single(_picker.Calls).InitialFolder);
        Assert.True(File.Exists(img1));
    }

    public static TheoryData<string> StateChanges => new() { "folder", "image", "busy" };

    [Theory]
    [MemberData(nameof(StateChanges))]
    public async Task MoveTo_StateChangedWhileDialogOpen_AbortsWithStatus(string change)
    {
        var (vm, fileActions, undo, img1, img2, dest) = await OpenAlbumAsync();
        _picker.Result = dest;
        var heldBusy = false;
        _picker.WhileOpen = () =>
        {
            switch (change)
            {
                case "folder": _clock.NextFolder(); break; // a forwarded open started loading another folder
                case "image": _catalog.SetCurrent(_catalog.IndexOf(img2)); break;
                case "busy": heldBusy = fileActions.TryBegin(); break;
            }
        };

        try
        {
            await vm.MoveToFolderAsync();
        }
        finally
        {
            if (heldBusy) fileActions.End();
        }

        Assert.True(File.Exists(img1));
        Assert.True(File.Exists(img2));
        Assert.Empty(Directory.GetFiles(dest));
        Assert.Equal(2, vm.TotalFiles);
        Assert.Equal(0, undo.MoveHistoryCount);
        Assert.Equal(Tr.StatusMoveCopyToStateChanged(Tr.ActionMoveToFolderName), vm.StatusText);
        Assert.Null(Settings.LastMoveToFolder);
        Assert.False(vm.IsFileActionInProgress);
    }

    [Fact]
    public async Task MoveTo_PhotoFolderItself_IsRefused()
    {
        var (vm, _, _, img1, _, _) = await OpenAlbumAsync();
        _picker.Result = _root.Combine("photos") + Path.DirectorySeparatorChar;

        await vm.MoveToFolderAsync();

        Assert.True(File.Exists(img1));
        Assert.Equal(2, vm.TotalFiles);
        Assert.Equal(Tr.StatusMoveCopyToSameFolder(Tr.ActionMoveToFolderName), vm.StatusText);
    }

    [Fact]
    public async Task CopyTo_MissingOrRelativeFolder_IsRefused()
    {
        var (vm, _, _, img1, _, _) = await OpenAlbumAsync();
        var missing = _root.Combine("missing");

        _picker.Result = missing;
        await vm.CopyToFolderAsync();
        Assert.Equal(Tr.StatusMoveCopyToFolderMissing(Tr.ActionCopyToFolderName, missing), vm.StatusText);
        Assert.False(Directory.Exists(missing)); // the pipeline would create it: it must never be reached

        _picker.Result = "relative";
        await vm.CopyToFolderAsync();
        Assert.Equal(Tr.StatusMoveCopyToNotAbsolute(Tr.ActionCopyToFolderName), vm.StatusText);
        Assert.False(Directory.Exists(_root.Combine("photos", "relative")));
        Assert.Single(Directory.GetFiles(_root.Combine("photos"), "1.jpg"));
        Assert.True(File.Exists(img1));
    }

    [Fact]
    public async Task MoveTo_NameCollision_FollowsActionPolicy_RefusesAndKeepsBothFiles()
    {
        var (vm, _, undo, img1, _, dest) = await OpenAlbumAsync();
        var existing = Path.Combine(dest, "1.jpg");
        File.WriteAllBytes(existing, [1, 2, 3]);
        _picker.Result = dest;

        await vm.MoveToFolderAsync();

        Assert.True(File.Exists(img1));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(existing)); // never overwritten
        Assert.Equal(2, vm.TotalFiles); // INV-5: the source went back into the catalog
        Assert.Equal(img1, vm.Catalog.Paths[0]);
        Assert.Equal(0, undo.MoveHistoryCount);
        Assert.Equal(StatusFormatter.ActionFailed(Tr.ActionMoveToFolderName, Tr.CoreFileActionDestinationExists(existing)), vm.StatusText);
        Assert.Null(Settings.LastMoveToFolder); // only a successful operation is remembered
    }

    // F3: the file changed size while moving -> source gone, destination present, journal Failed. The catalog must not
    // get the missing source back, the status is the "moved but unverified" warning, and no Undo is registered.
    [Theory]
    [InlineData("vi")]
    [InlineData("en")]
    public async Task MoveTo_SizeChangedDuringMove_SourceStaysOutOfCatalog_WarnsAndJournalsFailed(string language)
    {
        using var _ = TestLocalization.Use(language == "vi" ? TestLocalization.Vietnamese : TestLocalization.English);
        var (vm, _, undo, img1, img2, dest) = await OpenAlbumAsync((source, destination) =>
        {
            File.Move(source, destination);
            File.AppendAllText(destination, "grew while moving");
            return Task.CompletedTask;
        });
        _picker.Result = dest;

        await vm.MoveToFolderAsync();

        var moved = Path.Combine(dest, "1.jpg");
        Assert.False(File.Exists(img1));
        Assert.True(File.Exists(moved));
        Assert.Equal(1, vm.TotalFiles);
        Assert.DoesNotContain(img1, vm.Catalog.Paths);
        Assert.Equal(img2, vm.Catalog.Current?.Path);
        var failed = Assert.Single(_journal.ReadFailedOperations());
        Assert.Equal(JournalErrors.VerifySizeChanged, failed.ErrorCode);
        Assert.Equal(img1, failed.Source);
        Assert.Equal(0, undo.MoveHistoryCount);
        Assert.Null(Settings.LastMoveToFolder); // not a success: the folder is not remembered
        Assert.Equal(Tr.StatusMoveUnverified("1.jpg"), vm.StatusText);
        Assert.Contains(language == "vi" ? "không xác minh được" : "could not be verified", vm.StatusText, StringComparison.Ordinal);
        Assert.False(vm.IsFileActionInProgress);
    }

    [Fact]
    public async Task MoveTo_SourceStillExistsAfterFailedMove_KeepsSourceInCatalog()
    {
        var (vm, _, _, img1, _, dest) = await OpenAlbumAsync((source, destination) =>
        {
            File.Copy(source, destination); // cross-volume style: copied, but the source could not be deleted
            return Task.CompletedTask;
        });
        _picker.Result = dest;

        await vm.MoveToFolderAsync();

        Assert.True(File.Exists(img1));
        Assert.Equal(2, vm.TotalFiles);
        Assert.Equal(img1, vm.Catalog.Paths[0]);
        Assert.Equal(StatusFormatter.ActionFailed(Tr.ActionMoveToFolderName, Tr.CoreFileActionMoveSourceNotRemoved), vm.StatusText);
    }

    // --- test doubles ---

    private sealed class FakeFolderPicker : IFolderPicker
    {
        public string? Result { get; set; }
        public Action? WhileOpen { get; set; }
        public List<(string Title, string? InitialFolder)> Calls { get; } = [];

        public string? PickFolder(string title, string? initialFolder)
        {
            Calls.Add((title, initialFolder));
            WhileOpen?.Invoke(); // stands in for the nested message loop of the modal dialog
            return Result;
        }
    }

    private sealed class NeverRecycleBin : IRecycleBin
    {
        public bool CanRecycle(string path) => true;
        public void SendToRecycleBin(string path) => throw new InvalidOperationException("Move/Copy-to tests never recycle.");
        public void DeletePermanently(string path) => throw new InvalidOperationException("Move/Copy-to tests never delete.");
        public bool TryRestore(string path, long expectedSize, DateTime expectedLastWriteUtc) => false;
    }

    private sealed class NoDialogService : IDialogService
    {
        public bool ShowConfirmation(string title, string message) => false;
        public void ShowMessage(string title, string message) { }
        public void ShowError(string title, string message) { }
        public string? PickFolder(string? initialFolder = null) => throw new InvalidOperationException("Move/Copy-to must use IFolderPicker.");
        public bool ShowBatchReview(IReadOnlyList<string> paths) => false;
        public void ShowRecovery() { }
        public void ShowDiagnostics() { }
        public bool ShowSettings() => false;
        public void ShowBenchmark(string? folder = null) { }
        public void ShowSkippedFiles(IReadOnlyList<SkippedEntry> entries) { }
    }

    private sealed class NoExplorerOrder : IExplorerOrderProvider
    {
        public Task<ExplorerViewSnapshot> TryGetSnapshotAsync(string folder, TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.FromResult(new ExplorerViewSnapshot(folder, [], [], ExplorerGroupState.None, ExplorerOrderStatus.NativeViewUnavailable, null, DateTime.UtcNow));

        public Task<ExplorerViewSnapshot> TryGetSnapshotProgressiveAsync(string folder, TimeSpan timeout, IProgress<ExplorerQueryProgress>? progress = null, int progressiveBatchSize = 16, CancellationToken cancellationToken = default) =>
            TryGetSnapshotAsync(folder, timeout, cancellationToken);

        public void Dispose() { }
    }

    private sealed class NoPreload : IPreloadController
    {
        public Task PreloadAroundAsync(int center) => Task.CompletedTask;
        public bool TryConsumePreloadedKey(ImageCacheKey key) => false;
        public void Cancel() { }
        public void RemovePreloadedKeysForPath(string normalizedPath) { }
        public void ClearPreloadedKeys() { }
    }

    private sealed class NullPresentationSink : IPresentationSink
    {
        public void SetCurrentImage(object? image) { }
        public void SetStatusText(string status) { }
        public void ApplyInitialViewMode() { }
        public void OnPresented(string path) { }
        public void TracePresented(long token, string kind, long assignedTimestamp) { }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using PhotoReview.App.Coordinators;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.FileActions;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Model;
using PhotoReview.Core.Session;
using PhotoReview.Core.Settings;
using PhotoReview.Imaging.Caching;

namespace PhotoReview.App.ViewModels;

/// <summary>
/// ViewModel chính quản lý trạng thái hiển thị, điều phối thao tác mở thư mục và điều hướng xem ảnh,
/// tuân thủ tuyệt đối quy tắc K-2 (hoàn toàn độc lập với WPF và System.Windows).
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IFolderLoadSink, IFileActionSink, ISiblingNavigatorSink, IDuplicateCleanupSink
{
    private readonly ReviewCatalog _catalog;
    private readonly GenerationClock _clock;
    private readonly FolderLoadCoordinator _folderCoordinator;
    private readonly ImagePresenter _presenter;
    private bool _isRawDecodeIndicatorVisible;
    private readonly ViewerState _viewerState;
    private readonly CompareViewModel _compare;
    private readonly SettingsStore _settingsStore;
    private readonly SessionStore _sessionStore;
    private readonly SessionWriter? _sessionWriter;
    private readonly FileActionService? _fileActionService;
    private readonly UndoService? _undoService;
    private readonly IDialogService? _dialogService;
    private readonly IUiScheduler _uiScheduler;
    private readonly IPreloadController? _preloadController;
    private readonly INaturalComparer _naturalComparer;
    private readonly IFileSystem _fileSystem;
    private readonly Action? _resetCachesAction;
    private readonly FileHashService? _hashService;
    private readonly PreviewImageService? _previewService;
    private readonly ThumbnailCache? _thumbnailCache;
    private readonly FileActionController _fileActionController;
    private readonly SiblingFolderNavigator _siblingNavigator;
    private readonly DuplicateCleanupController _duplicateController;

    private string _folderTitle = Tr.AppTitle;
    private string _folderText = string.Empty;
    // FolderText is rendered from this state (never parsed back): the folder and image count of the
    // loaded catalog, and whether Explorer's view order has been applied to it.
    private string? _folderTextFolder;
    private int _folderTextCount;
    private bool _isExplorerOrderApplied;
    private string _statusText = string.Empty;
    private SessionState? _currentSession;
    private bool _isClosed;

    public MainViewModel(
        ReviewCatalog catalog,
        GenerationClock clock,
        FolderLoadCoordinator folderCoordinator,
        ImagePresenter presenter,
        ViewerState viewerState,
        CompareViewModel compare,
        SettingsStore settingsStore,
        SessionStore sessionStore,
        IFileSystem fileSystem,
        FileActionService fileActionService,
        UndoService undoService,
        IDialogService dialogService,
        FileHashService hashService,
        PreviewImageService previewService,
        ThumbnailCache thumbnailCache,
        SessionWriter sessionWriter,
        IPreloadController? preloadController = null,
        INaturalComparer? naturalComparer = null,
        Action? resetCachesAction = null,
        ReviewMetrics? metrics = null,
        IUiScheduler? uiScheduler = null,
        IFolderPicker? folderPicker = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _folderCoordinator = folderCoordinator ?? throw new ArgumentNullException(nameof(folderCoordinator));
        _presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));
        _presenter.ZoomDetail.RawDecodeIndicatorChanged += visible =>
        {
            _isRawDecodeIndicatorVisible = visible;
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(StatusNeedsAttention));
        };
        _viewerState = viewerState ?? throw new ArgumentNullException(nameof(viewerState));
        _compare = compare ?? throw new ArgumentNullException(nameof(compare));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _sessionStore = sessionStore ?? throw new ArgumentNullException(nameof(sessionStore));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _fileActionService = fileActionService ?? throw new ArgumentNullException(nameof(fileActionService));
        _undoService = undoService ?? throw new ArgumentNullException(nameof(undoService));
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        _hashService = hashService ?? throw new ArgumentNullException(nameof(hashService));
        _previewService = previewService ?? throw new ArgumentNullException(nameof(previewService));
        _thumbnailCache = thumbnailCache ?? throw new ArgumentNullException(nameof(thumbnailCache));
        _sessionWriter = sessionWriter ?? throw new ArgumentNullException(nameof(sessionWriter));
        _uiScheduler = uiScheduler ?? ImmediateUiScheduler.Instance;
        _preloadController = preloadController;
        _naturalComparer = naturalComparer ?? ManagedNaturalComparer.Instance;
        _resetCachesAction = resetCachesAction;
        Metrics = metrics ?? new ReviewMetrics();
        // AR14 (Q-AR10 option a): the controllers below are built here on purpose, not injected. Each takes this
        // view-model as its sink (IFileActionSink / ISiblingNavigatorSink / IDuplicateCleanupSink) plus delegates
        // reading its state (() => Settings, () => _currentSession), so DI cannot create them before the VM exists:
        // the controller -> sink = VM cycle is by design (AR14, see docs/refactoring/HISTORY.md).
        _fileActionController = new FileActionController(
            _catalog, _clock, _fileActionService, _undoService, _dialogService, _preloadController,
            _naturalComparer, () => Settings, this,
            folderPicker: folderPicker, fileSystem: _fileSystem, rememberFolder: RememberMoveCopyFolder);
        _siblingNavigator = new SiblingFolderNavigator(
            _clock, _catalog, _fileSystem, this, () => _currentSession, () => Settings.RawSupportEnabled);
        InfoOverlay = new InfoOverlayViewModel(() => Settings, _siblingNavigator.FindSiblingImageFolders,
            hasImage: () => HasImages, getZoomPercent: () => _viewerState.DisplayZoomPercent);
        InfoOverlay.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(InfoOverlayViewModel.IsFileInfoVisible)) OnPropertyChanged(nameof(IsStatusPanelVisible));
        };
        _duplicateController = new DuplicateCleanupController(
            _clock, _catalog, _fileActionService, _hashService, _fileSystem, _dialogService, _uiScheduler,
            _preloadController, _thumbnailCache, _previewService, this);
        _viewerState.ScalingQuality = Settings.ScalingQuality;
        _viewerState.ZoomStep = Settings.KeyboardZoomStepPercent / 100.0; // Q-R41
        // feat(zoom): leaving Fit requests the current image's full-resolution decode; Fit reverts
        // to the preview.
        _viewerState.ZoomModeChanged += (_, _) => _presenter.SetViewerZoom(_viewerState.EffectiveZoom);
        // Q-R45: the zoom HUD text/visibility follows the zoom level and Fit/stretch changes (DisplayZoomPercent's
        // own dependencies already notify through ImageWidth/ImageHeight, see ViewerState).
        _viewerState.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ViewerState.ImageWidth) or nameof(ViewerState.ImageHeight)) InfoOverlay.Refresh();
        };
        _presenter.SetViewerZoom(_viewerState.EffectiveZoom);
    }

    /// <summary>
    /// feat/image-crossfade: raised synchronously by <see cref="NotifyCurrentImageChanged"/>, BEFORE
    /// <c>CurrentImage</c>'s own <see cref="INotifyPropertyChanged.PropertyChanged"/> (so <c>MainImage.Source</c>
    /// still holds the OUTGOING bitmap when a subscriber handles it). <c>IsFileChange</c> is true only for the
    /// first bitmap of a navigation to a different file -- never for a same-file thumbnail/preview/original
    /// upgrade, and never when there is nothing to fade from.
    /// </summary>
    public event EventHandler<ImageChangingEventArgs>? ImageChanging;

    /// <summary>
    /// The presenter replaced the displayed bitmap (thumbnail, preview or full-resolution decode):
    /// size the viewer from the source's original dimensions, then refresh bindings.
    /// </summary>
    public void NotifyCurrentImageChanged(bool isFileChange = false)
    {
        if (isFileChange) ImageChanging?.Invoke(this, new ImageChangingEventArgs(isFileChange));
        if (_presenter.IsSameSourceSwap) _viewerState.SwapSourceSize(_presenter.CurrentOriginalWidth, _presenter.CurrentOriginalHeight);
        else _viewerState.SetSourceSize(_presenter.CurrentOriginalWidth, _presenter.CurrentOriginalHeight, newImage: isFileChange);
        NotifyNavigationStateChanged();
    }

    public ReviewMetrics Metrics { get; }
    public ImagePresenter Presenter => _presenter;
    public IPreloadController? PreloadController => _preloadController;
    public PreviewImageService? PreviewService => _previewService;
    // _undoService is a required constructor parameter (no default) that is null-checked via
    // ArgumentNullException in the constructor, so it is never null once the object exists; the
    // field is annotated `UndoService?` only by convention shared with the other DI fields above,
    // not because null is a legitimate post-construction state. All call sites (MainWindow.xaml.cs)
    // dereference this property unconditionally, confirming non-null is the real contract.
    public UndoService UndoService => _undoService!;

    private AppSettings? _settingsOverride;
    public AppSettings Settings
    {
        get => _settingsOverride ?? _settingsStore.Current;
        set
        {
            _settingsOverride = value;
            // Visibility switches and the folder keys shown in the folder line follow the new settings.
            InfoOverlay.Refresh();
        }
    }

    /// <summary>On-image info overlays (file block, folder block with sibling folders).</summary>
    public InfoOverlayViewModel InfoOverlay { get; }

    /// <summary>
    /// The bottom-left panel is shown when the file info is on, or when there is a skipped-files warning: the warning is
    /// not "info" and must stay visible even with the overlays hidden.
    /// </summary>
    public bool IsStatusPanelVisible => InfoOverlay.IsFileInfoVisible || IsExifLineVisible || HasSkippedEntries;

    public string FolderTitle
    {
        get => _folderTitle;
        private set => SetProperty(ref _folderTitle, value);
    }

    public string FolderText
    {
        get => _folderText;
        private set => SetProperty(ref _folderText, value);
    }

    /// <summary>True once Explorer's view order has been applied to the current folder's catalog.</summary>
    public bool IsExplorerOrderApplied
    {
        get => _isExplorerOrderApplied;
        private set => SetProperty(ref _isExplorerOrderApplied, value);
    }

    public string StatusText
    {
        get
        {
            // The zoom-decode indicator only replaces the plain index/name line: an action/error status (event text or a
            // presenter error) must stay readable during a slow RAW decode.
            if (!string.IsNullOrEmpty(_statusText)) return _statusText;
            if (_isRawDecodeIndicatorVisible && !_presenter.StatusNeedsAttention) return StatusFormatter.DecodingRaw();
            return _presenter.StatusText;
        }
        set
        {
            SetProperty(ref _statusText, value);
        }
    }

    /// <summary>
    /// Q-R34: true while the status area shows something the user must read (an event/error message such as an action
    /// result or "open failed", a loading/error status from the presenter, or the skipped-files warning), so the info
    /// auto-hide keeps it visible. A plain "index / size / name" line is false.
    /// </summary>
    public bool StatusNeedsAttention => _isRawDecodeIndicatorVisible || !string.IsNullOrEmpty(_statusText)
        || _presenter.StatusNeedsAttention || HasSkippedEntries;

    private IReadOnlyList<SkippedEntry> _skippedEntries = [];

    /// <summary>IO05: files of the current folder that could not be read and are not in the catalog.</summary>
    public IReadOnlyList<SkippedEntry> SkippedEntries => _skippedEntries;

    public bool HasSkippedEntries => _skippedEntries.Count > 0;

    /// <summary>Persistent warning ("Skipped N unreadable files"); empty when nothing was skipped.</summary>
    public string SkippedWarningText => _skippedEntries.Count == 0 ? string.Empty : Tr.MainSkippedWarning(_skippedEntries.Count);

    private void SetSkippedEntries(IReadOnlyList<SkippedEntry> entries)
    {
        _skippedEntries = entries;
        OnPropertyChanged(nameof(SkippedEntries));
        OnPropertyChanged(nameof(HasSkippedEntries));
        OnPropertyChanged(nameof(SkippedWarningText));
        OnPropertyChanged(nameof(IsStatusPanelVisible));
    }

    public object? CurrentImage => _presenter.CurrentImage;
    public ViewerState Viewer => _viewerState;
    public CompareViewModel Compare => _compare;
    public ReviewCatalog Catalog => _catalog;
    public SessionState? Session => _currentSession;

    public int CurrentIndex => _catalog.CurrentIndex;
    public int TotalFiles => _catalog.Count;
    public bool HasImages => _catalog.Count > 0;
    public bool CanNavigateNext => _catalog.Count > 0 && _catalog.CurrentIndex < _catalog.Count - 1;
    public bool CanNavigatePrevious => _catalog.Count > 0 && _catalog.CurrentIndex > 0;

    public event Action? CatalogChanged;

    public Task FirstImageAsync() => FirstAsync();

    public Task LastImageAsync() => LastAsync();

    public bool CurrentHasComparePair =>
        _catalog.CurrentIndex >= 0 && _catalog.CurrentIndex < _catalog.Count && _presenter.HasComparePair(_catalog.PathAt(_catalog.CurrentIndex));

    public bool CurrentHasCapturePair => _catalog.Current?.CaptureGroup is not null;

    /// <summary>Localized badge naming the displayed member of the current JPG+RAW capture (empty without a pair).</summary>
    public string CapturePairBadge
    {
        get
        {
            if (_catalog.Current is not { CaptureGroup: { } group }) return string.Empty;
            var shown = _presenter.CurrentPresentedPath;
            return shown is not null && string.Equals(shown, group.RawPath, StringComparison.OrdinalIgnoreCase)
                ? Tr.MainCapturePairBadgeRaw
                : Tr.MainCapturePairBadgeJpeg;
        }
    }

    public async Task ToggleCaptureGroupMemberAsync()
    {
        await WaitForPendingExplorerOrderAsync();
        if (_compare.IsVisible || _catalog.Current is not { CaptureGroup: { } group } current) return;

        var currentPath = _presenter.CurrentPresentedPath ?? current.Path;
        var nextPath = string.Equals(currentPath, group.JpegPath, StringComparison.OrdinalIgnoreCase)
            ? group.RawPath
            : group.JpegPath;
        _clock.NextInteraction();
        _statusText = string.Empty;
        await _presenter.PresentAsync(_catalog.CurrentIndex, allowCompare: false, pathOverride: nextPath);
        // The badge names the displayed member; do not rely on the presenter's status hook to refresh it.
        OnPropertyChanged(nameof(CapturePairBadge));
    }

    public void ToggleCompare()
    {
        var enableCompare = !_compare.IsVisible;
        _compare.IsVisible = enableCompare;
        if (_catalog.CurrentIndex >= 0)
        {
            // The presented path can be stale (catalog changed since); PresentAsync rejects an override that is not a
            // member of the selected entry, so only pass it when it really belongs to the current index.
            var presented = _presenter.CurrentPresentedPath;
            var pathOverride = _catalog.Current?.CaptureGroup is not null && presented is not null
                && _catalog.IndexOf(presented) == _catalog.CurrentIndex ? presented : null;
            CompareToggleTask = ObservePresentAsync(_presenter.PresentAsync(_catalog.CurrentIndex, allowCompare: enableCompare,
                pathOverride: pathOverride, includeCaptureGroupInCompare: enableCompare));
        }
    }

    /// <summary>The latest fire-and-forget compare toggle presentation (tests await it).</summary>
    internal Task CompareToggleTask { get; private set; } = Task.CompletedTask;

    private static async Task ObservePresentAsync(Task present)
    {
        try { await present; }
        catch (Exception ex) { AppLog.Error("ToggleCompare presentation failed", ex); }
    }

    /// <summary>Latest folder load, including its Explorer-order apply/ignore; completes when the order is settled (tests await it instead of a wall-clock window).</summary>
    internal Task FolderLoadTask { get; private set; } = Task.CompletedTask;

    /// <summary>The re-present of the current image started by the last <see cref="ShowSettings"/> (test seam).</summary>
    internal Task SettingsRefreshTask { get; private set; } = Task.CompletedTask;

    /// <summary>AR16: the latest load's background readability probe (unreadable files removed and reported); tests await it.</summary>
    internal Task ReadabilityProbeTask => _folderCoordinator.ReadabilityProbe;

    /// <summary>
    /// Mở thư mục ảnh và nạp danh mục ảnh.
    /// </summary>
    public async Task OpenFolderAsync(string folder, string? initialPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        // A forwarded open, drop or undo that reaches a window already closed (CloseSession disposed the load coordinator)
        // is dropped instead of throwing out of an async-void handler.
        if (_isClosed) return;
        // Q-R18: every folder open (command line, drop, forwarded, sibling navigation) passes the instance ownership
        // first. The common case completes synchronously, so this adds no dispatcher yield.
        var ownership = FolderOwnership;
        if (ownership is not null)
        {
            var decision = await ownership.BeforeOpenAsync(folder, initialPath);
            if (_isClosed)
            {
                // The window closed while ownership was being negotiated: the load coordinator is disposed, so loading
                // would throw out of an async-void handler. Keep the AfterOpen contract for a granted open.
                if (decision == PhotoReview.Core.Instance.FolderOpenDecision.Proceed)
                    ownership.AfterOpen(folder, _shownFolder);
                return;
            }
            if (decision != PhotoReview.Core.Instance.FolderOpenDecision.Proceed)
            {
                var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
                StatusText = decision == PhotoReview.Core.Instance.FolderOpenDecision.ForwardedToOtherInstance
                    ? Tr.StatusFolderOpenedInOtherWindow(name)
                    : Tr.StatusFolderOpenInOtherWindowNoResponse(name);
                NotifyNavigationStateChanged();
                return;
            }
        }
        _statusText = string.Empty;
        // feat/image-crossfade: the new folder's first image must never fade in from whatever the previous
        // folder left on screen.
        _presenter.ResetFileIdentity();
        try
        {
            FolderLoadTask = _folderCoordinator.LoadAsync(folder, initialPath);
            await FolderLoadTask;
        }
        finally
        {
            ownership?.AfterOpen(folder, _shownFolder);
        }
        NotifyNavigationStateChanged();
    }

    /// <summary>
    /// Q-R18: single-instance lock/pipe ownership of the shown folder, set by the composition root (App). Null (tests,
    /// benchmark host) = no instance checks.
    /// </summary>
    public PhotoReview.Core.Instance.IFolderOwnership? FolderOwnership { get; set; }

    /// <summary>Q-R18: the folder whose catalog this window shows (set when a load reaches the catalog).</summary>
    private string? _shownFolder;

    private void OnFolderShown(string folder)
    {
        _shownFolder = folder;
        FolderOwnership?.OnFolderShown(folder);
    }

    /// <summary>
    /// Xử lý mở đường dẫn từ thao tác kéo thả (Drag & Drop) hoặc dòng lệnh.
    /// </summary>
    public async Task OpenPathAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        var input = DragDropInputService.Parse([path], Settings.RawSupportEnabled);
        if (!input.IsValid)
        {
            StatusText = input.Warning ?? Tr.StatusNoValidInput;
            return;
        }

        if (input.Warning is not null)
        {
            StatusText = input.Warning;
        }

        await OpenFolderAsync(input.FolderPath!, input.InitialImagePath);
    }

    /// <summary>
    /// Điều hướng tới ảnh kế tiếp. Tăng thế hệ tương tác người dùng.
    /// </summary>
    public async Task NextAsync()
    {
        await WaitForPendingExplorerOrderAsync();
        if (_catalog.Count == 0) return;
        _clock.NextInteraction();
        var nextIdx = Math.Min(_catalog.CurrentIndex + 1, _catalog.Count - 1);
        _statusText = string.Empty;
        // R05: no explicit NotifyNavigationStateChanged() here -- PresentAsync always drives at least one
        // (via NotifyCurrentImageChanged when the image is assigned, then again via NotifyPresentationChanged
        // once the final status text, e.g. with dimensions, is known) before it returns, for every index this
        // method can pass (0 <= nextIdx < _catalog.Count, already guaranteed above). An extra call here would
        // just re-raise the same, already-current state a second time; see
        // MainViewModelNavigationTests.NextAsync_RaisesNavigationStateChanged_ExactlyThreeTimes (pinned count).
        await _presenter.PresentAsync(nextIdx);
    }

    /// <summary>
    /// Điều hướng tới ảnh trước đó. Tăng thế hệ tương tác người dùng.
    /// </summary>
    public async Task PreviousAsync()
    {
        await WaitForPendingExplorerOrderAsync();
        if (_catalog.Count == 0) return;
        _clock.NextInteraction();
        var prevIdx = Math.Max(_catalog.CurrentIndex - 1, 0);
        _statusText = string.Empty;
        // R05: see the comment in NextAsync -- PresentAsync already notifies before returning.
        await _presenter.PresentAsync(prevIdx);
    }

    /// <summary>
    /// Điều hướng về ảnh đầu tiên trong danh mục. Tăng thế hệ tương tác người dùng.
    /// </summary>
    public async Task FirstAsync()
    {
        await WaitForPendingExplorerOrderAsync();
        if (_catalog.Count == 0) return;
        _clock.NextInteraction();
        _statusText = string.Empty;
        // R05: see the comment in NextAsync -- PresentAsync already notifies before returning.
        await _presenter.PresentAsync(0);
    }

    /// <summary>
    /// Điều hướng tới ảnh cuối cùng trong danh mục (đối xứng với <see cref="FirstAsync"/>). Tăng thế hệ tương tác người dùng.
    /// </summary>
    public async Task LastAsync()
    {
        await WaitForPendingExplorerOrderAsync();
        if (_catalog.Count == 0) return;
        _clock.NextInteraction();
        _statusText = string.Empty;
        // R05: see the comment in NextAsync -- PresentAsync already notifies before returning.
        await _presenter.PresentAsync(_catalog.Count - 1);
    }

    /// <summary>
    /// Bỏ qua ảnh hiện tại, ghi nhận vào danh sách Skipped của phiên và chuyển tiếp.
    /// </summary>
    public async Task SkipAsync()
    {
        await WaitForPendingExplorerOrderAsync();
        if (_catalog.CurrentIndex < 0 || _catalog.CurrentIndex >= _catalog.Count) return;
        _clock.NextInteraction();

        var currentPath = _catalog.PathAt(_catalog.CurrentIndex);
        if (_currentSession != null)
        {
            // R2-F-10: no duplicates -- skipping the same image again must not grow the persisted session file.
            if (!_currentSession.Skipped.Contains(currentPath, StringComparer.OrdinalIgnoreCase))
                _currentSession.Skipped.Add(currentPath);
            _currentSession.UpdatedUtc = DateTime.UtcNow;
            PersistSession(_currentSession);
        }

        var nextIdx = Math.Min(_catalog.CurrentIndex + 1, _catalog.Count - 1);
        _statusText = string.Empty;
        // R05: see the comment in NextAsync -- PresentAsync already notifies before returning.
        await _presenter.PresentAsync(nextIdx);
    }

    /// <summary>
    /// Điều hướng thư mục cùng cấp (sibling), bảo toàn kiểm tra thế hệ folder chống race condition.
    /// </summary>
    public Task NavigateSiblingFolderAsync(int direction) => _siblingNavigator.NavigateSiblingFolderAsync(direction);

    /// <summary>
    /// Thực hiện action từ danh sách Action Profiles theo chỉ số index.
    /// </summary>
    public Task RunActionAsync(int index) => _fileActionGate.RunQueuedAsync(async () =>
    {
        await WaitForPendingExplorerOrderAsync();
        await _fileActionController.RunActionAsync(index, _compare.SelectedPath, _catalog.Current?.Path);
    });

    /// <summary>
    /// Chuyển ảnh hiện tại vào thùng rác (Recycle Bin).
    /// </summary>
    public Task RecycleAsync() => _fileActionGate.RunQueuedAsync(async () =>
    {
        await WaitForPendingExplorerOrderAsync();
        await _fileActionController.RecycleAsync(_compare.SelectedPath, _catalog.Current?.Path);
    });

    /// <summary>
    /// OC14: single gate shared by file actions and undo. Move/Delete/Copy review actions queue
    /// behind each other (Q-T1, <see cref="FileActionGate.RunQueuedAsync"/>); Undo and duplicate
    /// cleanup stay exclusive and are no-ops while anything else holds the gate.
    /// </summary>
    private readonly FileActionGate _fileActionGate = new();

    /// <summary>True while a file action, a queued Move/Delete action, or undo is in flight (read-only projection of the gate).</summary>
    public bool IsFileActionInProgress => _fileActionGate.IsHeld;

    /// <summary>R7-7: completes once no file action or undo holds the gate (window close waits for it).</summary>
    public Task WhenFileActionIdleAsync() => _fileActionGate.WhenReleasedAsync();

    /// <summary>
    /// INV-9: a file opened directly is presented before Explorer's view order arrives. Commands that
    /// move away from it (navigation, file actions that advance) wait for that order to be applied or
    /// given up on first, so they step through the Explorer order and do not trip INV-7. Completes
    /// synchronously -- no extra dispatcher hop on the navigation hot path -- when nothing is pending.
    /// </summary>
    private async Task WaitForPendingExplorerOrderAsync()
    {
        var pending = _folderCoordinator.PendingOrder;
        if (!pending.IsCompleted) await pending;
    }

    /// <summary>
    /// Unified entry point for Undo: reverses the last file action (Move or Recycle).
    /// Replaces both legacy UndoAsync (Move-only) and UndoLastAsync to provide consistent semantics
    /// across all callers (keyboard Ctrl+Z and button click).
    /// </summary>
    public Task UndoAsync() => _fileActionGate.RunExclusiveAsync(UndoCoreAsync);

    private async Task UndoCoreAsync()
    {
        var currentFolder = _currentSession?.Folder;
        var result = await _fileActionController.UndoLastAsync(currentFolder);

        // Recycle undo, or a Move made in a previous folder (R7-2): open the restored file's folder at that file.
        if (FileActionController.RestoresOutsideFolder(result, currentFolder))
        {
            // Open at a member that really came back: Source (first manifest member) may be a permanently deleted one.
            var reloadPath = FileActionController.ReloadPathAfterUndo(result!);
            var folder = string.IsNullOrEmpty(reloadPath) ? null : Path.GetDirectoryName(reloadPath);
            if (!string.IsNullOrEmpty(folder))
            {
                await OpenFolderAsync(folder, reloadPath);
                // The reload wrote its own status: put back the note of a capture that was only partly restorable.
                if (!string.IsNullOrEmpty(result!.ErrorMessage)) StatusText = result.ErrorMessage;
            }
        }
    }

    public void ToggleFit() => _viewerState.ResetFit();
    public void ZoomIn() => _viewerState.ZoomIn();
    public void ZoomOut() => _viewerState.ZoomOut();

    /// <summary>Zoom to 100 % = one source pixel per device pixel (ADR 0008); no-op without an image.</summary>
    public void ZoomActualSize()
    {
        if (!HasImages) return;
        _viewerState.ZoomToActualSize();
    }

    /// <summary>
    /// Shows/hides all on-image info overlays and persists the choice (config.json, through the same SettingsStore.Save
    /// as the Settings window, on the UI thread: SettingsStore.Changed listeners are UI-affine, ADR 0005).
    /// </summary>
    public void ToggleInfoOverlay()
    {
        var settings = Settings;
        settings.ShowInfoOverlay = !settings.ShowInfoOverlay;
        InfoOverlay.Refresh();
        NotifyExifLineChanged();
        try
        {
            _settingsStore.Save(settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The overlay still toggles for this session; only persisting failed.
            AppLog.Error("Could not save the info overlay setting", ex);
        }
    }
    public void ToggleFullscreen() => _viewerState.ToggleFullscreen();
    public void ExitFullscreen() => _viewerState.ExitFullscreen();

    /// <summary>Test seam: launches the external editor; default starts the real process (Q-R48).</summary>
    internal Action<string, string> StartExternalEditor { get; set; } = static (exePath, filePath) =>
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exePath)
        {
            UseShellExecute = false,
            ArgumentList = { filePath },
        });
    };

    /// <summary>Q-R48: the "Open in External Editor" context-menu item is shown only when an editor is configured and a photo is open.</summary>
    public bool CanOpenInExternalEditor => !string.IsNullOrWhiteSpace(Settings.ExternalEditorPath) && HasImages;

    /// <summary>Q-R48: launches the configured external editor with the currently-viewed (or compare-selected) photo's path.</summary>
    public void OpenInExternalEditor()
    {
        var editorPath = Settings.ExternalEditorPath;
        if (string.IsNullOrWhiteSpace(editorPath)) return;
        var path = _compare.SelectedPath ?? _presenter.CurrentPresentedPath ?? _catalog.Current?.Path;
        if (string.IsNullOrEmpty(path)) return;
        if (!_fileSystem.FileExists(path))
        {
            _dialogService?.ShowError(Tr.DialogExternalEditorFailedTitle, Tr.DialogExternalEditorFileMissing(Path.GetFileName(path)));
            return;
        }
        try
        {
            StartExternalEditor(editorPath, path);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or System.IO.FileNotFoundException)
        {
            _dialogService?.ShowError(Tr.DialogExternalEditorFailedTitle, ex.Message);
        }
    }

    /// <summary>
    /// Mở hộp thoại chọn thư mục và tải thư mục được chọn.
    /// </summary>
    public async Task PickAndOpenFolderAsync()
    {
        if (_dialogService is null) return;
        var current = _currentSession?.Folder ?? (_catalog.Current != null ? Path.GetDirectoryName(_catalog.Current.Path) : null);
        var selected = _dialogService.PickFolder(current);
        if (!string.IsNullOrWhiteSpace(selected))
        {
            await OpenFolderAsync(selected);
        }
    }

    /// <summary>
    /// Tìm kiếm và xử lý các ảnh trùng lặp theo hash nội dung.
    /// </summary>
    public Task RemoveDuplicatesAsync(bool removeNumbered) =>
        // R2-F-20: same gate as Recycle/Move/Undo, so a second click during hashing/review and interleaved file actions are no-ops.
        _fileActionGate.RunExclusiveAsync(() => _duplicateController.RemoveDuplicatesAsync(removeNumbered));

    /// <summary>
    /// Q-R25: cancels a running duplicate-check hashing phase (Esc). Returns true while one is running so the key is consumed
    /// (no fullscreen exit / window close); the recycle batch that follows the review dialog is not cancellable here.
    /// </summary>
    public bool CancelDuplicateCheck() => _duplicateController.CancelDuplicateCheck();

    /// <summary>
    /// R7-7: true while a file action holds the gate, so closing the window must wait for it. A duplicate check that is still
    /// only hashing (read-only) is cancelled so the wait ends quickly; a Move/recycle batch is never interrupted.
    /// </summary>
    public bool DeferCloseForFileAction()
    {
        if (!IsFileActionInProgress) return false;
        CancelDuplicateCheck();
        return true;
    }

    /// <summary>
    /// Xóa toàn bộ bộ nhớ đệm preview và thumbnail sau khi người dùng xác nhận.
    /// </summary>
    public async Task ClearCacheAsync() =>
        await _duplicateController.ClearCacheAsync();

    /// <summary>
    /// Hiển thị cửa sổ khôi phục thao tác tệp tin.
    /// </summary>
    public void ShowRecovery() => _dialogService?.ShowRecovery();

    /// <summary>
    /// Hiển thị cửa sổ chẩn đoán hiệu năng.
    /// </summary>
    public void ShowDiagnostics() => _dialogService?.ShowDiagnostics();

    /// <summary>
    /// Hiển thị danh sách tệp bị bỏ qua ở lần nạp thư mục gần nhất (AR19).
    /// </summary>
    public void ShowSkippedFiles() => _dialogService?.ShowSkippedFiles(SkippedEntries);

    /// <summary>
    /// Hiển thị cửa sổ benchmark hiệu năng.
    /// </summary>
    public void ShowBenchmark()
    {
        var folder = _currentSession?.Folder ?? (_catalog.Current != null ? Path.GetDirectoryName(_catalog.Current.Path) : null);
        _dialogService?.ShowBenchmark(folder);
    }

    /// <summary>
    /// Hiển thị cửa sổ cài đặt cấu hình. Nếu cấu hình chế độ nạp thay đổi, hủy và nạp lại ảnh hiện tại.
    /// </summary>
    public void ShowSettings()
    {
        if (_dialogService is null) return;
        var previousMode = _settingsStore.Current.LoadingMode;
        var previousBackend = _settingsStore.Current.DecoderBackend;
        var previousRawSupport = _settingsStore.Current.RawSupportEnabled;
        var previousPairMode = _settingsStore.Current.RawPairMode;
        var previousRawFullDecode = _settingsStore.Current.RawFullDecode;
        var changed = _dialogService.ShowSettings();
        if (!changed) return;

        UpdateFolderTitle();
        InfoOverlay.Refresh();
        _viewerState.ScalingQuality = Settings.ScalingQuality;
        _viewerState.ZoomStep = Settings.KeyboardZoomStepPercent / 100.0; // Q-R41
        NotifyExifLineChanged(); // ShowExifInfo / ExifInfoFields may have changed
        var newMode = _settingsStore.Current.LoadingMode;
        var newBackend = _settingsStore.Current.DecoderBackend;
        var newRawSupport = _settingsStore.Current.RawSupportEnabled;
        // RAW support decides which files the catalog lists (and whether RAW paths can be decoded at all), and the pair mode
        // (read only while a folder loads) decides how JPG+RAW captures are grouped: reload the open folder at the current
        // file so no listed entry is left that the decoder now refuses and no stale grouping stays. The pair mode is
        // irrelevant while RAW support is off.
        var reloadFolder = (previousRawSupport != newRawSupport
                || (newRawSupport && previousPairMode != _settingsStore.Current.RawPairMode))
            && _currentSession?.Folder is { Length: > 0 };
        // The RAW full-decode setting is read when the zoom target is armed (ZoomDetailLoader.OnPreviewPresented), so the open
        // image must be re-presented (cheap: RAM-cached preview, no folder reload) for Never <-> OnZoom to apply to it.
        var rawFullDecodeChanged = previousRawFullDecode != _settingsStore.Current.RawFullDecode;
        if (reloadFolder || previousMode != newMode || previousBackend != newBackend || rawFullDecodeChanged)
        {
            _preloadController?.Cancel();
            if (previousBackend != newBackend)
            {
                _previewService?.ClearCache();
                // R2-F-16: the directory delete must not run on the UI thread (the RAM cache above is already cleared).
                var previewService = _previewService;
                if (previewService is not null) _ = Task.Run(previewService.ClearDisk);
                _preloadController?.ClearPreloadedKeys();
            }
            if (reloadFolder)
            {
                _previewService?.ClearCache();
                _preloadController?.ClearPreloadedKeys();
                // OpenFolderAsync presents the reloaded folder itself: no separate PresentAsync (would double-present).
                SettingsRefreshTask = ReloadFolderAfterSettingsAsync();
            }
            else if (_catalog.CurrentIndex >= 0 && _catalog.CurrentIndex < _catalog.Count)
            {
                // Drop an armed target / held full decode made under the old setting before the image is presented again.
                _presenter.ZoomDetail.Reset();
                SettingsRefreshTask = _presenter.PresentAsync(_catalog.CurrentIndex);
            }
        }
    }

    /// <summary>
    /// The settings-triggered reload of the open folder. A file action that is still running must finish first: a Move that
    /// completed after a reload would take the "moved after you left its folder" path although the folder is the same one.
    /// Completes synchronously (the load starts immediately) when no file action holds the gate. The task is always observed:
    /// a failure is logged instead of surfacing as an unobserved exception.
    /// </summary>
    private async Task ReloadFolderAfterSettingsAsync()
    {
        try
        {
            while (_fileActionGate.IsHeld) await _fileActionGate.WhenReleasedAsync();
            // Read after the wait: the action may have moved the current photo, or the user may have opened another folder.
            if (_isClosed || _currentSession?.Folder is not { Length: > 0 } folder) return;
            await OpenFolderAsync(folder, _catalog.Current?.Path);
        }
        catch (Exception ex)
        {
            AppLog.Error("Reloading the folder after a settings change failed", ex);
        }
    }

    private void PersistSession(SessionState session)
    {
        if (_sessionWriter is not null) _sessionWriter.Update(session);
        else _sessionStore.Save(session);
    }

    /// <summary>Writes any debounced session state now (window close, folder change).</summary>
    public void FlushSession() => _sessionWriter?.Flush();

    /// <summary>
    /// Window close / app exit (Q-R5, R7-3): writes pending session state but waits at most 2 s for a write already
    /// in flight on a slow disk, then skips it instead of hanging shutdown. Later updates are ignored.
    /// </summary>
    /// <summary>
    /// Window closed: cancel and dispose the folder-load coordinator (a scan/Explorer await must not keep
    /// running or touch this view model afterwards, APP-01), then write pending session state through the
    /// bounded shutdown path.
    /// </summary>
    public void CloseSession()
    {
        _isClosed = true;
        _folderCoordinator.Dispose();
        _sessionWriter?.Dispose();
    }

    public void UpdateTitle(string? folder = null) => UpdateFolderTitle(folder);

    private string? _instanceLabel;

    /// <summary>
    /// Marks every title this window renders with a "[label] " prefix, so a window created for
    /// automated tests or a manual/agent verification run is never mistaken for the user's real,
    /// everyday window. Null/empty (the default) leaves the title exactly as before this existed --
    /// two seams set it: <c>TestAppHost.CreateMainWindow</c> (fixed test label, unconditional) and
    /// <c>PHOTOREVIEW_DIAG_INSTANCE_LABEL</c> read once at production startup
    /// (<see cref="PhotoReview.Core.Diagnostics.DiagOptions.InstanceLabel"/>). Both funnel through this
    /// one property so the prefix is applied in exactly one place (<see cref="ApplyInstanceLabel"/>).
    /// </summary>
    public string? InstanceLabel
    {
        get => _instanceLabel;
        set
        {
            _instanceLabel = value;
            UpdateFolderTitle();
        }
    }

    /// <summary>Single seam that prepends <see cref="InstanceLabel"/> (see its doc) to a composed title; a no-op when unset.</summary>
    private string ApplyInstanceLabel(string title) =>
        string.IsNullOrEmpty(_instanceLabel) ? title : $"[{_instanceLabel}] {title}";

    private void UpdateFolderTitle(string? folder = null)
    {
        folder ??= _currentSession?.Folder ?? "";
        var settings = Settings;
        if (string.IsNullOrWhiteSpace(folder))
        {
            FolderTitle = ApplyInstanceLabel(Tr.AppTitle);
            return;
        }
        var content = BuildTitleBarContent(folder, settings.TitleBarFields);
        var title = settings.LoggingEnabled ? Tr.MainTitleWithFolderLogging(content) : Tr.MainTitleWithFolder(content);
        FolderTitle = ApplyInstanceLabel(title);
    }

    /// <summary>
    /// Assembles the title bar's content from data already in memory (AGENTS.md rule 1: no disk I/O just for the
    /// title) -- the catalog entry's Length/LastWriteUtc from the folder scan and the EXIF already read for the
    /// presented photo (<see cref="ImagePresenter.CurrentPhotoInfo"/>), same sources <see cref="ExifText"/> uses.
    /// Falls back to the folder name alone when the selected fields render empty (e.g. everything unchecked, or
    /// nothing presented yet), so the title is never left bare.
    /// </summary>
    private string BuildTitleBarContent(string folder, TitleBarFields fields)
    {
        var entry = _catalog.Current;
        var info = _presenter.CurrentPhotoInfo;
        var index = _catalog.CurrentIndex;
        var count = _catalog.Count;
        var content = TitleBarFormatter.Format(
            fields, folder,
            index >= 0 && count > 0 ? index : null, count > 0 ? count : null,
            info?.FileName, entry?.Length, info?.Width ?? 0, info?.Height ?? 0,
            entry?.LastWriteUtc, info?.Exif,
            System.Globalization.CultureInfo.CurrentCulture);
        return content.Length > 0 ? content : Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
    }

    /// <summary>Sets the folder/count/Explorer state and renders <see cref="FolderText"/> from it.</summary>
    private void SetFolderText(string folder, int count, bool explorerOrderApplied)
    {
        _folderTextFolder = folder;
        _folderTextCount = count;
        IsExplorerOrderApplied = explorerOrderApplied;
        FolderText = explorerOrderApplied
            ? Tr.MainFolderTextImageCountExplorer(count, folder)
            : Tr.MainFolderTextImageCount(count, folder);
    }

    /// <summary>
    /// Re-renders text built in code after a live language switch (ADR 0006): XAML follows by binding,
    /// but the title and folder line are rendered here from state.
    /// </summary>
    public void RefreshLocalizedText()
    {
        UpdateFolderTitle();
        if (_folderTextFolder is { } folder) SetFolderText(folder, _folderTextCount, IsExplorerOrderApplied);
        OnPropertyChanged(nameof(SkippedWarningText));
        InfoOverlay.Refresh();
        OnPropertyChanged(nameof(CapturePairBadge));
        // StatusText is event text (last action); it switches language with the next update.
        NotifyExifLineChanged();
    }

    public void NotifyPresentationChanged() => NotifyNavigationStateChanged();

    private void NotifyNavigationStateChanged()
    {
        OnPropertyChanged(nameof(CanNavigateNext));
        OnPropertyChanged(nameof(CanNavigatePrevious));
        OnPropertyChanged(nameof(HasImages));
        OnPropertyChanged(nameof(CurrentHasCapturePair));
        OnPropertyChanged(nameof(CapturePairBadge));
        OnPropertyChanged(nameof(CurrentIndex));
        OnPropertyChanged(nameof(TotalFiles));
        OnPropertyChanged(nameof(CurrentImage));
        OnPropertyChanged(nameof(StatusText));
        NotifyExifLineChanged();
        UpdateFolderTitle(); // TitleBarFields may include the current file/EXIF, which just changed
    }

    // --- IFolderLoadSink implementation ---

    void IFolderLoadSink.ResetCaches()
    {
        _resetCachesAction?.Invoke();
    }

    void IFolderLoadSink.OnCatalogReady(string folder, int count, SessionState session)
    {
        // The running preload loop holds a snapshot of the PREVIOUS catalog; stop it so the next
        // PreloadAroundAsync starts a fresh lifetime over the new entries (R2-F-01).
        _preloadController?.Cancel();
        _currentSession = session; // loaded once by FolderLoadCoordinator (APP-02)
        OnFolderShown(folder);
        if (_skippedEntries.Count > 0) SetSkippedEntries([]); // a new load; OnFilesSkipped follows if needed
        SetFolderText(folder, count, explorerOrderApplied: false);
        UpdateFolderTitle(folder);
        InfoOverlay.SetFolder(folder);
        _statusText = StatusFormatter.IndexOnly(0, count);
        CatalogChanged?.Invoke();
        NotifyNavigationStateChanged();
    }

    Task IFolderLoadSink.PresentAsync(int index, long presentationGeneration)
    {
        _statusText = string.Empty;
        return _presenter.PresentAsync(index);
    }

    void IFolderLoadSink.OnEmpty(string folder, SessionState session)
    {
        _preloadController?.Cancel();
        _currentSession = session; // loaded once by FolderLoadCoordinator (APP-02)
        OnFolderShown(folder);
        SetFolderText(folder, 0, explorerOrderApplied: false);
        UpdateFolderTitle(folder);
        InfoOverlay.SetFolder(folder);
        StatusText = StatusFormatter.NoSupportedImages();
        _presenter.ClearPresentation();
        CatalogChanged?.Invoke();
        NotifyNavigationStateChanged();
    }

    void IFolderLoadSink.OnEmptyWithSubfolders(string folder, SessionState session, int subfolderCount)
    {
        _preloadController?.Cancel();
        _currentSession = session; // loaded once by FolderLoadCoordinator (APP-02)
        OnFolderShown(folder);
        SetFolderText(folder, 0, explorerOrderApplied: false);
        UpdateFolderTitle(folder);
        InfoOverlay.SetFolder(folder);
        StatusText = StatusFormatter.NoSupportedImagesButSubfolders(subfolderCount);
        _presenter.ClearPresentation();
        CatalogChanged?.Invoke();
        NotifyNavigationStateChanged();
    }

    void IFolderLoadSink.OnOrderApplied(int count, int currentIndex, bool currentKept)
    {
        if (_currentSession?.Folder is { } f)
        {
            SetFolderText(f, count, explorerOrderApplied: true);
        }
        else if (_folderTextFolder is { } shown && !IsExplorerOrderApplied)
        {
            // No session folder: keep the folder and count already shown, just mark the order.
            SetFolderText(shown, _folderTextCount, explorerOrderApplied: true);
        }
        // The current image keeps its place but its neighbours are now the Explorer-order ones:
        // re-center preload on its new index (a file opened directly is presented before the
        // order is applied, so preload started around the fallback neighbours).
        // The loop still walks the pre-order snapshot: cancel it so the fresh lifetime sees the new order (R2-F-01).
        _preloadController?.Cancel();
        if (currentKept && currentIndex >= 0) _ = _preloadController?.PreloadAroundAsync(currentIndex);
        CatalogChanged?.Invoke();
        NotifyNavigationStateChanged();
    }

    void IFolderLoadSink.OnFilesSkipped(string folder, IReadOnlyList<SkippedEntry> skipped)
    {
        SetSkippedEntries(skipped.ToArray());
    }

    async Task IFolderLoadSink.OnUnreadableRemovedAsync(IReadOnlyList<string> removedPaths, bool currentRemoved)
    {
        // AR16: the running preload loop walks a snapshot that still holds the removed files: stop it,
        // and drop anything already cached/preloaded for them, before a fresh lifetime starts below.
        _preloadController?.Cancel();
        foreach (var path in removedPaths)
        {
            _presenter.EvictCachedPath(path);
        }

        if (_folderTextFolder is { } shown) SetFolderText(shown, _catalog.Count, IsExplorerOrderApplied);
        CatalogChanged?.Invoke();

        if (currentRemoved)
        {
            // Same as a Delete of the current image: show the next one (it saves the session), or the empty state.
            _compare.Clear();
            if (_catalog.CurrentIndex >= 0)
            {
                _statusText = string.Empty;
                await _presenter.PresentAsync(_catalog.CurrentIndex);
            }
            else
            {
                _presenter.ClearPresentation();
                StatusText = StatusFormatter.NoImagesRemaining();
            }
        }
        else if (_catalog.CurrentIndex >= 0)
        {
            // The current image stays on screen; its neighbours changed, so preload re-centers on its new index.
            _ = _preloadController?.PreloadAroundAsync(_catalog.CurrentIndex);
        }

        NotifyNavigationStateChanged();
    }

    void IFolderLoadSink.OnFailed(string folder, Exception exception)
    {
        StatusText = StatusFormatter.FolderOpenFailed(UserFacingError.Describe(exception));
        NotifyNavigationStateChanged();
    }

    // IFileActionSink implementation
    void IFileActionSink.SetStatusText(string status)
    {
        StatusText = status;
    }

    void IFileActionSink.ShowLateActionStatus(string status)
    {
        // Event text (_statusText) is non-empty while the current folder shows its own message (catalog just listed,
        // "no images", "open failed", an action result) and is cleared once an image is presented. Never overwrite it.
        if (string.IsNullOrEmpty(_statusText)) StatusText = status;
    }

    void IFileActionSink.OnCatalogChanged(string? removedPath)
    {
        CatalogChanged?.Invoke();
        // Only the removed file leaves the cache; the new Current is the next image the user is about
        // to see and is usually already preloaded (R2-F-02).
        if (removedPath is not null)
        {
            _presenter.EvictCachedPath(removedPath);
        }
        _compare.Clear();
    }

    void IFileActionSink.EvictCachedPaths(IReadOnlyList<string> paths)
    {
        foreach (var path in paths) _presenter.EvictCachedPath(path);
    }

    async Task IFileActionSink.PresentAsync(int index)
    {
        await _presenter.PresentAsync(index);
    }

    void IFileActionSink.UpdateSessionPath(string currentPath)
    {
        if (_currentSession is not null)
        {
            _currentSession.CurrentPath = currentPath;
            _currentSession.UpdatedUtc = DateTime.UtcNow;
            PersistSession(_currentSession);
        }
    }

    void IFileActionSink.NotifyNavigationStateChanged()
    {
        NotifyNavigationStateChanged();
    }

    // ISiblingNavigatorSink implementation
    void ISiblingNavigatorSink.SetStatusText(string status)
    {
        StatusText = status;
    }

    async Task ISiblingNavigatorSink.OpenFolderAsync(string folder, string? initialPath)
    {
        await OpenFolderAsync(folder, initialPath);
    }

    // IDuplicateCleanupSink implementation
    void IDuplicateCleanupSink.SetStatusText(string status)
    {
        StatusText = status;
    }

    async Task IDuplicateCleanupSink.OpenFolderAsync(string folder, string? initialPath)
    {
        await OpenFolderAsync(folder, initialPath);
    }

    // ---- "Move to… / Copy to…" ----

    /// <summary>
    /// "Move to…" (default M): moves the current photo into a folder chosen in the folder picker, or into the last one
    /// when reuse is on; <paramref name="forcePicker"/> (Shift+key) always asks. Same gate and pipeline as an action profile.
    /// </summary>
    public Task MoveToFolderAsync(bool forcePicker = false) => MoveOrCopyToFolderAsync(FileOperationType.Move, forcePicker);

    /// <summary>"Copy to…" (default Y): like <see cref="MoveToFolderAsync"/> but copies.</summary>
    public Task CopyToFolderAsync(bool forcePicker = false) => MoveOrCopyToFolderAsync(FileOperationType.Copy, forcePicker);

    /// <returns>False when an exclusive holder (Undo/duplicate cleanup) held the gate (nothing queued/ran); otherwise queues behind any other running/queued Move-Delete-family action (Q-T1).</returns>
    private Task<bool> MoveOrCopyToFolderAsync(FileOperationType operation, bool forcePicker) => _fileActionGate.RunQueuedAsync(async () =>
    {
        await WaitForPendingExplorerOrderAsync();
        await _fileActionController.MoveOrCopyToFolderAsync(operation, forcePicker, () => (_compare.SelectedPath, _catalog.Current?.Path));
    });

    /// <summary>Persists the folder a successful Move-to/Copy-to went to, so the picker starts there next time.</summary>
    private void RememberMoveCopyFolder(FileOperationType operation, string folder)
    {
        var settings = Settings;
        if (operation == FileOperationType.Move) settings.LastMoveToFolder = folder;
        else settings.LastCopyToFolder = folder;
        try
        {
            _settingsStore.Save(settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The file operation already succeeded; only the remembered folder is lost (kept in memory for this session).
            FileLog.Default.Warn("Could not save the last Move-to/Copy-to folder: " + ex.Message);
        }
    }

    // --- Photo information (EXIF) line under the status line ---

    /// <summary>
    /// The photo information line for the presented image (<see cref="ExifFormatter"/>, fields from
    /// <see cref="AppSettings.ExifInfoFields"/>); empty while loading, in compare mode or with nothing shown.
    /// Independent of <see cref="AppSettings.ShowExifInfo"/> -- see <see cref="IsExifLineVisible"/>.
    /// </summary>
    public string ExifText => _presenter.CurrentPhotoInfo is { } info
        ? ExifFormatter.Format(Settings.ExifInfoFields, info.FileName, info.Width, info.Height, info.Exif,
            _catalog.Current?.LastWriteUtc, // ModifiedDate: catalog entry from the folder scan, no extra disk read
            System.Globalization.CultureInfo.CurrentCulture, // display text: user's number/date format
            info.RawPreviewWidth, info.RawPreviewHeight)
        : string.Empty;

    /// <summary>
    /// <see cref="AppSettings.ShowInfoOverlay"/> (master switch, key I) and <see cref="AppSettings.ShowExifInfo"/>, and
    /// there is something to show.
    /// </summary>
    public bool IsExifLineVisible => Settings.ShowInfoOverlay && Settings.ShowExifInfo && ExifText.Length > 0;

    /// <summary>Screen-reader name of the line.</summary>
    public string ExifAutomationName => Tr.MainExifAutomationName(ExifText);

    private void NotifyExifLineChanged()
    {
        OnPropertyChanged(nameof(ExifText));
        OnPropertyChanged(nameof(IsExifLineVisible));
        OnPropertyChanged(nameof(ExifAutomationName));
        OnPropertyChanged(nameof(IsStatusPanelVisible));
    }
}

/// <summary>See <see cref="MainViewModel.ImageChanging"/>.</summary>
public sealed class ImageChangingEventArgs(bool isFileChange) : EventArgs
{
    public bool IsFileChange { get; } = isFileChange;
}

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
        IUiScheduler? uiScheduler = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _folderCoordinator = folderCoordinator ?? throw new ArgumentNullException(nameof(folderCoordinator));
        _presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));
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
        _fileActionController = new FileActionController(
            _catalog, _clock, _fileActionService, _undoService, _dialogService, _preloadController,
            _naturalComparer, () => Settings, this);
        _siblingNavigator = new SiblingFolderNavigator(
            _clock, _catalog, _fileSystem, this, () => _currentSession);
        _duplicateController = new DuplicateCleanupController(
            _clock, _catalog, _fileActionService, _hashService, _fileSystem, _dialogService, _uiScheduler,
            _preloadController, _thumbnailCache, _previewService, this);
        _viewerState.ScalingQuality = Settings.ScalingQuality;
        // feat(zoom): leaving Fit requests the current image's full-resolution decode; Fit reverts
        // to the preview.
        _viewerState.ZoomModeChanged += (_, _) => _presenter.SetViewerZoom(_viewerState.EffectiveZoom);
        _presenter.SetViewerZoom(_viewerState.EffectiveZoom);
    }

    /// <summary>
    /// The presenter replaced the displayed bitmap (thumbnail, preview or full-resolution decode):
    /// size the viewer from the source's original dimensions, then refresh bindings.
    /// </summary>
    public void NotifyCurrentImageChanged()
    {
        _viewerState.SetSourceSize(_presenter.CurrentOriginalWidth, _presenter.CurrentOriginalHeight);
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
        set => _settingsOverride = value;
    }

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
        get => string.IsNullOrEmpty(_statusText) ? _presenter.StatusText : _statusText;
        set
        {
            if (SetProperty(ref _statusText, value))
            {
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

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

    public bool CurrentHasComparePair =>
        _catalog.CurrentIndex >= 0 && _catalog.CurrentIndex < _catalog.Count && _presenter.HasComparePair(_catalog.Paths[_catalog.CurrentIndex]);

    public void ToggleCompare()
    {
        var enableCompare = !_compare.IsVisible;
        _compare.IsVisible = enableCompare;
        if (_catalog.CurrentIndex >= 0)
        {
            _ = _presenter.PresentAsync(_catalog.CurrentIndex, allowCompare: enableCompare);
        }
    }

    /// <summary>Latest folder load, including its Explorer-order apply/ignore; completes when the order is settled (tests await it instead of a wall-clock window).</summary>
    internal Task FolderLoadTask { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Mở thư mục ảnh và nạp danh mục ảnh.
    /// </summary>
    public async Task OpenFolderAsync(string folder, string? initialPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        _statusText = string.Empty;
        FolderLoadTask = _folderCoordinator.LoadAsync(folder, initialPath);
        await FolderLoadTask;
        NotifyNavigationStateChanged();
    }

    /// <summary>
    /// Xử lý mở đường dẫn từ thao tác kéo thả (Drag & Drop) hoặc dòng lệnh.
    /// </summary>
    public async Task OpenPathAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        var input = DragDropInputService.Parse([path]);
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
        await _presenter.PresentAsync(nextIdx);
        NotifyNavigationStateChanged();
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
        await _presenter.PresentAsync(prevIdx);
        NotifyNavigationStateChanged();
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
        await _presenter.PresentAsync(0);
        NotifyNavigationStateChanged();
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
        await _presenter.PresentAsync(nextIdx);
        NotifyNavigationStateChanged();
    }

    /// <summary>
    /// Chuyển tới thư mục anh em kế tiếp có chứa ảnh.
    /// </summary>
    public Task NextFolderAsync() => _siblingNavigator.NavigateSiblingFolderAsync(1);

    /// <summary>
    /// Chuyển tới thư mục anh em phía trước có chứa ảnh.
    /// </summary>
    public Task PreviousFolderAsync() => _siblingNavigator.NavigateSiblingFolderAsync(-1);

    /// <summary>
    /// Điều hướng thư mục cùng cấp (sibling), bảo toàn kiểm tra thế hệ folder chống race condition.
    /// </summary>
    public Task NavigateSiblingFolderAsync(int direction) => _siblingNavigator.NavigateSiblingFolderAsync(direction);

    /// <summary>
    /// Thực hiện action từ danh sách Action Profiles theo chỉ số index.
    /// </summary>
    public Task RunActionAsync(int index) => _fileActionGate.RunExclusiveAsync(async () =>
    {
        await WaitForPendingExplorerOrderAsync();
        await _fileActionController.RunActionAsync(index, _compare.SelectedPath, _catalog.Current?.Path);
    });

    /// <summary>
    /// Chuyển ảnh hiện tại vào thùng rác (Recycle Bin).
    /// </summary>
    public Task RecycleAsync() => _fileActionGate.RunExclusiveAsync(async () =>
    {
        await WaitForPendingExplorerOrderAsync();
        await _fileActionController.RecycleAsync(_compare.SelectedPath, _catalog.Current?.Path);
    });

    /// <summary>OC14: single gate shared by file actions and undo.</summary>
    private readonly FileActionGate _fileActionGate = new();

    /// <summary>True while a file action or undo is in flight (read-only projection of the gate).</summary>
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
            var folder = Path.GetDirectoryName(result!.Source);
            if (!string.IsNullOrEmpty(folder))
            {
                await OpenFolderAsync(folder, result.Source);
            }
        }
    }

    public void ToggleFit() => _viewerState.ResetFit();
    public void ZoomIn() => _viewerState.ZoomIn();
    public void ZoomOut() => _viewerState.ZoomOut();
    public void ToggleFullscreen() => _viewerState.ToggleFullscreen();
    public void ExitFullscreen() => _viewerState.ExitFullscreen();

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
        var changed = _dialogService.ShowSettings();
        if (changed)
        {
            UpdateFolderTitle();
            _viewerState.ScalingQuality = Settings.ScalingQuality;
            var newMode = _settingsStore.Current.LoadingMode;
            var newBackend = _settingsStore.Current.DecoderBackend;
            if (previousMode != newMode || previousBackend != newBackend)
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
                if (_catalog.CurrentIndex >= 0 && _catalog.CurrentIndex < _catalog.Count)
                {
                    _ = _presenter.PresentAsync(_catalog.CurrentIndex);
                }
            }
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
    public void CloseSession() => _sessionWriter?.Dispose();

    public void UpdateTitle(string? folder = null) => UpdateFolderTitle(folder);

    private void UpdateFolderTitle(string? folder = null)
    {
        folder ??= _currentSession?.Folder ?? "";
        var settings = Settings;
        FolderTitle = string.IsNullOrWhiteSpace(folder)
            ? Tr.AppTitle
            : settings.LoggingEnabled ? Tr.MainTitleWithFolderLogging(folder) : Tr.MainTitleWithFolder(folder);
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
        // StatusText is event text (last action); it switches language with the next update.
    }

    public void NotifyPresentationChanged() => NotifyNavigationStateChanged();

    private void NotifyNavigationStateChanged()
    {
        OnPropertyChanged(nameof(CanNavigateNext));
        OnPropertyChanged(nameof(CanNavigatePrevious));
        OnPropertyChanged(nameof(HasImages));
        OnPropertyChanged(nameof(CurrentIndex));
        OnPropertyChanged(nameof(TotalFiles));
        OnPropertyChanged(nameof(CurrentImage));
        OnPropertyChanged(nameof(StatusText));
    }

    // --- IFolderLoadSink implementation ---

    void IFolderLoadSink.ResetCaches()
    {
        _resetCachesAction?.Invoke();
    }

    void IFolderLoadSink.OnCatalogReady(string folder, int count)
    {
        // The running preload loop holds a snapshot of the PREVIOUS catalog; stop it so the next
        // PreloadAroundAsync starts a fresh lifetime over the new entries (R2-F-01).
        _preloadController?.Cancel();
        _sessionWriter?.Flush();
        _currentSession = _sessionStore.Load(folder);
        if (_skippedEntries.Count > 0) SetSkippedEntries([]); // a new load; OnFilesSkipped follows if needed
        SetFolderText(folder, count, explorerOrderApplied: false);
        UpdateFolderTitle(folder);
        _statusText = StatusFormatter.IndexOnly(0, count);
        CatalogChanged?.Invoke();
        NotifyNavigationStateChanged();
    }

    Task IFolderLoadSink.PresentAsync(int index, long presentationGeneration)
    {
        _statusText = string.Empty;
        return _presenter.PresentAsync(index);
    }

    void IFolderLoadSink.OnEmpty(string folder)
    {
        _preloadController?.Cancel();
        _sessionWriter?.Flush();
        _currentSession = _sessionStore.Load(folder);
        SetFolderText(folder, 0, explorerOrderApplied: false);
        UpdateFolderTitle(folder);
        StatusText = StatusFormatter.NoSupportedImages();
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

    void IFolderLoadSink.OnFailed(string folder, Exception exception)
    {
        StatusText = StatusFormatter.FolderOpenFailed(exception.Message);
        NotifyNavigationStateChanged();
    }

    // IFileActionSink implementation
    void IFileActionSink.SetStatusText(string status)
    {
        StatusText = status;
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

    void ISiblingNavigatorSink.NotifyNavigationStateChanged()
    {
        NotifyNavigationStateChanged();
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

    void IDuplicateCleanupSink.NotifyNavigationStateChanged()
    {
        NotifyNavigationStateChanged();
    }
}

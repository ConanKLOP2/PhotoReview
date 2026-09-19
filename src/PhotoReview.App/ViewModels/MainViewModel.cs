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
using PhotoReview.Core.Model;
using PhotoReview.Core.Session;
using PhotoReview.Core.Settings;
using PhotoReview.Imaging.Caching;

namespace PhotoReview.App.ViewModels;

/// <summary>
/// ViewModel chính quản lý trạng thái hiển thị, điều phối thao tác mở thư mục và điều hướng xem ảnh,
/// tuân thủ tuyệt đối quy tắc K-2 (hoàn toàn độc lập với WPF và System.Windows).
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IFolderLoadSink
{
    private readonly ReviewCatalog _catalog;
    private readonly GenerationClock _clock;
    private readonly FolderLoadCoordinator _folderCoordinator;
    private readonly ImagePresenter _presenter;
    private readonly ViewerState _viewerState;
    private readonly CompareViewModel _compare;
    private readonly SettingsStore _settingsStore;
    private readonly SessionStore _sessionStore;
    private readonly FileActionService? _fileActionService;
    private readonly UndoService? _undoService;
    private readonly IDialogService? _dialogService;
    private readonly IPreloadController? _preloadController;
    private readonly INaturalComparer _naturalComparer;
    private readonly IFileSystem _fileSystem;
    private readonly Action? _resetCachesAction;
    private readonly FileHashService? _hashService;
    private readonly PreviewImageService? _previewService;
    private readonly ThumbnailCache? _thumbnailCache;

    private string _folderTitle = "Photo Review";
    private string _folderText = string.Empty;
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
        IFileSystem? fileSystem = null,
        FileActionService? fileActionService = null,
        UndoService? undoService = null,
        IDialogService? dialogService = null,
        IPreloadController? preloadController = null,
        INaturalComparer? naturalComparer = null,
        Action? resetCachesAction = null,
        FileHashService? hashService = null,
        PreviewImageService? previewService = null,
        ThumbnailCache? thumbnailCache = null,
        ReviewMetrics? metrics = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _folderCoordinator = folderCoordinator ?? throw new ArgumentNullException(nameof(folderCoordinator));
        _presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));
        _viewerState = viewerState ?? throw new ArgumentNullException(nameof(viewerState));
        _compare = compare ?? throw new ArgumentNullException(nameof(compare));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _sessionStore = sessionStore ?? throw new ArgumentNullException(nameof(sessionStore));
        _fileActionService = fileActionService;
        _undoService = undoService;
        _dialogService = dialogService;
        _preloadController = preloadController;
        _naturalComparer = naturalComparer ?? ManagedNaturalComparer.Instance;
        _fileSystem = fileSystem ?? new PhotoReview.Core.IO.PhysicalFileSystem();
        _resetCachesAction = resetCachesAction;
        _hashService = hashService;
        _previewService = previewService;
        _thumbnailCache = thumbnailCache;
        Metrics = metrics ?? new ReviewMetrics();
        _viewerState.ScalingQuality = Settings.ScalingQuality;
    }

    public ReviewMetrics Metrics { get; }
    public ImagePresenter Presenter => _presenter;
    public IPreloadController? PreloadController => _preloadController;
    public PreviewImageService? PreviewService => _previewService;
    public UndoService UndoService => _undoService;

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

    public void ToggleCompare()
    {
        _compare.IsVisible = !_compare.IsVisible;
        if (_catalog.CurrentIndex >= 0)
        {
            _ = _presenter.PresentAsync(_catalog.CurrentIndex);
        }
    }

    /// <summary>
    /// Mở thư mục ảnh và nạp danh mục ảnh.
    /// </summary>
    public async Task OpenFolderAsync(string folder, string? initialPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        _statusText = string.Empty;
        await _folderCoordinator.LoadAsync(folder, initialPath).ConfigureAwait(false);
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
            StatusText = input.Warning ?? "Không có input hợp lệ.";
            return;
        }

        if (input.Warning is not null)
        {
            StatusText = input.Warning;
        }

        await OpenFolderAsync(input.FolderPath!, input.InitialImagePath).ConfigureAwait(false);
    }

    /// <summary>
    /// Điều hướng tới ảnh kế tiếp. Tăng thế hệ tương tác người dùng.
    /// </summary>
    public async Task NextAsync()
    {
        if (_catalog.Count == 0) return;
        _clock.NextInteraction();
        var nextIdx = Math.Min(_catalog.CurrentIndex + 1, _catalog.Count - 1);
        _statusText = string.Empty;
        await _presenter.PresentAsync(nextIdx).ConfigureAwait(false);
        NotifyNavigationStateChanged();
    }

    /// <summary>
    /// Điều hướng tới ảnh trước đó. Tăng thế hệ tương tác người dùng.
    /// </summary>
    public async Task PreviousAsync()
    {
        if (_catalog.Count == 0) return;
        _clock.NextInteraction();
        var prevIdx = Math.Max(_catalog.CurrentIndex - 1, 0);
        _statusText = string.Empty;
        await _presenter.PresentAsync(prevIdx).ConfigureAwait(false);
        NotifyNavigationStateChanged();
    }

    /// <summary>
    /// Điều hướng về ảnh đầu tiên trong danh mục. Tăng thế hệ tương tác người dùng.
    /// </summary>
    public async Task FirstAsync()
    {
        if (_catalog.Count == 0) return;
        _clock.NextInteraction();
        _statusText = string.Empty;
        await _presenter.PresentAsync(0).ConfigureAwait(false);
        NotifyNavigationStateChanged();
    }

    /// <summary>
    /// Bỏ qua ảnh hiện tại, ghi nhận vào danh sách Skipped của phiên và chuyển tiếp.
    /// </summary>
    public async Task SkipAsync()
    {
        if (_catalog.CurrentIndex < 0 || _catalog.CurrentIndex >= _catalog.Count) return;
        _clock.NextInteraction();

        var currentPath = _catalog.Paths[_catalog.CurrentIndex];
        if (_currentSession != null)
        {
            _currentSession.Skipped.Add(currentPath);
            _currentSession.UpdatedUtc = DateTime.UtcNow;
            _sessionStore.Save(_currentSession);
        }

        var nextIdx = Math.Min(_catalog.CurrentIndex + 1, _catalog.Count - 1);
        _statusText = string.Empty;
        await _presenter.PresentAsync(nextIdx).ConfigureAwait(false);
        NotifyNavigationStateChanged();
    }

    /// <summary>
    /// Chuyển tới thư mục anh em kế tiếp có chứa ảnh.
    /// </summary>
    public Task NextFolderAsync() => NavigateSiblingFolderAsync(1);

    /// <summary>
    /// Chuyển tới thư mục anh em phía trước có chứa ảnh.
    /// </summary>
    public Task PreviousFolderAsync() => NavigateSiblingFolderAsync(-1);

    /// <summary>
    /// Điều hướng thư mục cùng cấp (sibling), bảo toàn kiểm tra thế hệ folder chống race condition.
    /// </summary>
    public async Task NavigateSiblingFolderAsync(int direction)
    {
        var folder = _currentSession?.Folder ?? (_catalog.Current != null ? Path.GetDirectoryName(_catalog.Current.Path) : null);
        if (string.IsNullOrWhiteSpace(folder)) return;

        var currentFolder = Path.GetFullPath(folder);
        var folderGeneration = _clock.CurrentFolder;

        var targetFolder = await Task.Run(() => FindNextImageFolder(currentFolder, direction)).ConfigureAwait(false);

        // Kiểm tra generation để tránh race condition khi người dùng đã chuyển folder khác giữa chừng
        if (!_clock.IsFolderCurrent(folderGeneration)) return;

        if (targetFolder is null)
        {
            StatusText = StatusFormatter.SiblingFolderBoundary(direction);
            return;
        }

        await OpenFolderAsync(targetFolder).ConfigureAwait(false);
    }

    /// <summary>
    /// Thực hiện action từ danh sách Action Profiles theo chỉ số index.
    /// </summary>
    public async Task RunActionAsync(int index)
    {
        if (_catalog.Count == 0) return;
        var actions = Settings.Actions;
        if (index < 0 || index >= actions.Count) return;

        var action = actions[index];
        if (!Enum.IsDefined(action.Operation))
        {
            StatusText = $"Không thực hiện được {action.Name}: Operation không hợp lệ.";
            return;
        }

        if (action.Confirm && _dialogService is not null)
        {
            var ok = _dialogService.ShowConfirmation("Xác nhận action", $"Thực hiện action '{action.Name}' trên ảnh hiện tại?");
            if (!ok) return;
        }

        if (action.Operation == FileOperationType.Recycle)
        {
            await ExecuteFileActionCoreAsync(action.Name, FileOperationType.Recycle, null).ConfigureAwait(false);
            return;
        }

        var source = (_compare.IsVisible ? _compare.SelectedPath : null) ?? _catalog.Current?.Path;
        if (string.IsNullOrEmpty(source)) return;

        if (string.IsNullOrWhiteSpace(action.Destination))
        {
            StatusText = $"Không thực hiện được {action.Name}: Action chưa có thư mục đích.";
            return;
        }

        await ExecuteFileActionCoreAsync(action.Name, action.Operation, action.Destination).ConfigureAwait(false);
    }

    /// <summary>
    /// Chuyển ảnh hiện tại vào thùng rác (Recycle Bin).
    /// </summary>
    public async Task RecycleAsync()
    {
        await ExecuteFileActionCoreAsync("Recycle", FileOperationType.Recycle, null).ConfigureAwait(false);
    }

    private async Task ExecuteFileActionCoreAsync(string actionName, FileOperationType operation, string? destination)
    {
        if (_catalog.Count == 0) return;
        if (_fileActionService is null) return;

        // INV-4: Gate bận
        if (_fileActionService.IsBusy) return;

        var source = _compare.SelectedPath ?? _catalog.Current?.Path;
        if (string.IsNullOrEmpty(source)) return;

        var sourceIndex = _catalog.IndexOf(source);
        _clock.StopForAction();
        _preloadController?.Cancel();
        var folderGen = _clock.CurrentFolder;

        var isRemove = operation is FileOperationType.Move or FileOperationType.Recycle;
        int nextIndex = -1;

        if (isRemove)
        {
            nextIndex = _catalog.Remove(source);
            CatalogChanged?.Invoke();
            _presenter.EvictCachedPath(source);
            _compare.Clear();

            // INV-3: Trình diễn ảnh tiếp theo TRƯỚC KHI thao tác file hoàn thành, không await
            if (nextIndex >= 0)
            {
                _ = _presenter.PresentAsync(nextIndex);
            }
            else
            {
                StatusText = "Đã xử lý hết ảnh trong folder.";
            }
        }

        try
        {
            var request = new FileActionRequest(source, operation, destination);
            var result = await _fileActionService.ExecuteAsync(request).ConfigureAwait(false);

            // Stale Folder Guard: Nếu người dùng đã đổi thư mục trong khi I/O đang chạy, bỏ qua
            if (!_clock.IsFolderCurrent(folderGen))
            {
                return;
            }

            if (result.Succeeded)
            {
                _undoService?.Register(result);

                if (_currentSession is not null)
                {
                    _currentSession.CurrentPath = _catalog.Current?.Path;
                    _currentSession.UpdatedUtc = DateTime.UtcNow;
                    _sessionStore.Save(_currentSession);
                }

                if (_catalog.Count == 0)
                {
                    StatusText = isRemove ? "Đã xử lý hết ảnh trong folder." : $"Đã thực hiện: {actionName}";
                }
                else if (operation == FileOperationType.Copy)
                {
                    StatusText = $"Đã copy sang {Path.GetFileName(result.DestinationPath)}";
                }
            }
            else
            {
                // INV-5: Thất bại thì khôi phục lại ảnh nguồn vào danh mục
                if (isRemove && sourceIndex >= 0)
                {
                    _catalog.Restore(source, sourceIndex);
                }

                StatusText = $"Không thực hiện được {actionName}: {result.Error}";
            }
        }
        finally
        {
            NotifyNavigationStateChanged();
        }
    }

    /// <summary>
    /// Hoàn tác thao tác di chuyển gần nhất (Ctrl+Z).
    /// </summary>
    public async Task UndoAsync()
    {
        if (_undoService is null) return;

        var folderGen = _clock.CurrentFolder;
        var result = await _undoService.UndoMoveAsync().ConfigureAwait(false);

        if (!result.Succeeded)
        {
            StatusText = result.ErrorMessage ?? "Không thể Undo.";
            return;
        }

        if (!_clock.IsFolderCurrent(folderGen)) return;

        if (!string.IsNullOrEmpty(result.Source))
        {
            _catalog.InsertSorted(result.Source, (a, b) => _naturalComparer.Compare(Path.GetFileName(a), Path.GetFileName(b)));
            CatalogChanged?.Invoke();
            var idx = _catalog.IndexOf(result.Source);
            if (idx >= 0)
            {
                await _presenter.PresentAsync(idx).ConfigureAwait(false);
            }

            if (_currentSession is not null)
            {
                _currentSession.CurrentPath = result.Source;
                _currentSession.UpdatedUtc = DateTime.UtcNow;
                _sessionStore.Save(_currentSession);
            }
        }

        NotifyNavigationStateChanged();
    }

    /// <summary>
    /// Hoàn tác thao tác gần nhất (Move hoặc Recycle).
    /// </summary>
    public async Task UndoLastAsync()
    {
        if (_undoService is null) return;

        var folderGen = _clock.CurrentFolder;
        var result = await _undoService.UndoLastAsync().ConfigureAwait(false);

        if (!result.Succeeded)
        {
            StatusText = result.ErrorMessage ?? "Không có thao tác nào để hoàn tác.";
            return;
        }

        if (!_clock.IsFolderCurrent(folderGen)) return;

        if (result.Operation == FileOperationType.Move && !string.IsNullOrEmpty(result.Source))
        {
            _catalog.InsertSorted(result.Source, (a, b) => _naturalComparer.Compare(Path.GetFileName(a), Path.GetFileName(b)));
            CatalogChanged?.Invoke();
            var idx = _catalog.IndexOf(result.Source);
            if (idx >= 0)
            {
                await _presenter.PresentAsync(idx).ConfigureAwait(false);
            }

            if (_currentSession is not null)
            {
                _currentSession.CurrentPath = result.Source;
                _currentSession.UpdatedUtc = DateTime.UtcNow;
                _sessionStore.Save(_currentSession);
            }
        }
        else if (result.Operation == FileOperationType.Recycle && !string.IsNullOrEmpty(result.Source))
        {
            if (_currentSession is not null)
            {
                _currentSession.CurrentPath = result.Source;
                _currentSession.UpdatedUtc = DateTime.UtcNow;
                _sessionStore.Save(_currentSession);
            }

            var folder = Path.GetDirectoryName(result.Source);
            if (!string.IsNullOrEmpty(folder))
            {
                await OpenFolderAsync(folder, result.Source).ConfigureAwait(false);
            }
        }

        NotifyNavigationStateChanged();
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
            await OpenFolderAsync(selected).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Tìm kiếm và xử lý các ảnh trùng lặp theo hash nội dung.
    /// </summary>
    public async Task RemoveDuplicatesAsync(bool removeNumbered)
    {
        if (_fileActionService is null || _fileActionService.IsBusy) return;
        if (_catalog.Count == 0) return;

        var preHashGeneration = _clock.CurrentFolder;
        var candidates = _catalog.Paths.ToArray();
        if (candidates.Length == 0) return;

        List<string> remove;
        try
        {
            remove = await DuplicateFinder.FindAsync(
                candidates,
                removeNumbered,
                (path, ct) => _hashService?.GetAsync(path, ct) ?? Task.FromResult(string.Empty),
                System.Threading.CancellationToken.None,
                _fileSystem).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            StatusText = $"Lỗi kiểm tra trùng lặp: {ex.Message}";
            return;
        }

        // Stale Folder Guard: Nếu folder đã bị đổi trong khi hash, hủy bỏ thao tác
        if (!_clock.IsFolderCurrent(preHashGeneration))
        {
            StatusText = StatusFormatter.DuplicateCheckCanceledFolderChanged();
            return;
        }

        if (remove.Count == 0)
        {
            StatusText = StatusFormatter.NoDuplicatesFound();
            return;
        }

        if (_dialogService is not null)
        {
            var confirmed = _dialogService.ShowBatchReview(remove);
            if (!confirmed)
            {
                StatusText = StatusFormatter.BatchCanceled();
                return;
            }
        }

        _clock.StopForAction();
        _preloadController?.Cancel();
        var actionFolderGeneration = _clock.CurrentFolder;

        var failures = new List<string>();
        var succeeded = 0;

        foreach (var path in remove)
        {
            var request = new FileActionRequest(path, FileOperationType.Recycle);
            var result = await _fileActionService.ExecuteAsync(request).ConfigureAwait(false);
            if (result.Succeeded)
            {
                succeeded++;
            }
            else
            {
                failures.Add($"{Path.GetFileName(path)}: {result.Error}");
            }
        }

        if (!_clock.IsFolderCurrent(actionFolderGeneration))
        {
            return;
        }

        StatusText = StatusFormatter.BatchDone(succeeded, failures.Count);
        if (failures.Count > 0 && _dialogService is not null)
        {
            _dialogService.ShowError("Báo cáo lỗi batch", string.Join(Environment.NewLine, failures));
        }

        if (succeeded > 0 && remove.Count > 0)
        {
            var folder = Path.GetDirectoryName(remove[0]);
            if (!string.IsNullOrEmpty(folder))
            {
                await OpenFolderAsync(folder).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Xóa toàn bộ bộ nhớ đệm preview và thumbnail sau khi người dùng xác nhận.
    /// </summary>
    public async Task ClearCacheAsync()
    {
        if (_dialogService is not null)
        {
            var confirmed = _dialogService.ShowConfirmation("Xác nhận xóa cache", "Xóa toàn bộ cache preview? Ảnh nguồn không bị thay đổi.");
            if (!confirmed) return;
        }

        _preloadController?.Cancel();
        _thumbnailCache?.ClearDisk();
        _thumbnailCache?.ClearMemory();
        _previewService?.ClearCache();
        _previewService?.ClearDisk();
        _preloadController?.ClearPreloadedKeys();
        StatusText = StatusFormatter.CacheCleared();
        await Task.CompletedTask;
    }

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
        var changed = _dialogService.ShowSettings();
        if (changed)
        {
            UpdateFolderTitle();
            _viewerState.ScalingQuality = Settings.ScalingQuality;
            var newMode = _settingsStore.Current.LoadingMode;
            if (previousMode != newMode)
            {
                _preloadController?.Cancel();
                if (_catalog.CurrentIndex >= 0 && _catalog.CurrentIndex < _catalog.Count)
                {
                    _ = _presenter.PresentAsync(_catalog.CurrentIndex);
                }
            }
        }
    }

    public void UpdateTitle(string? folder = null) => UpdateFolderTitle(folder);

    private void UpdateFolderTitle(string? folder = null)
    {
        folder ??= _currentSession?.Folder ?? "";
        var settings = Settings;
        FolderTitle = string.IsNullOrWhiteSpace(folder)
            ? "Photo Review"
            : $"Photo Review — {folder}{(settings.LoggingEnabled ? " · LOG" : "")}";
    }

    private string? FindNextImageFolder(string currentFolder, int direction)
    {
        var folders = SiblingFolderService.GetSorted(currentFolder);
        var index = folders.ToList().FindIndex(path => string.Equals(Path.GetFullPath(path), Path.GetFullPath(currentFolder), StringComparison.OrdinalIgnoreCase));
        if (index < 0) return null;

        for (var i = index + direction; i >= 0 && i < folders.Count; i += direction)
        {
            try
            {
                var candidates = _fileSystem.EnumerateFiles(folders[i], "*")
                    .Where(ImageFileTypes.IsSupported);
                if (candidates.Any()) return folders[i];
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return null;
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
        _currentSession = _sessionStore.Load(folder);
        FolderText = $"{folder}  ({count} ảnh)";
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
        _currentSession = _sessionStore.Load(folder);
        FolderText = $"{folder}  (0 ảnh)";
        UpdateFolderTitle(folder);
        StatusText = StatusFormatter.NoSupportedImages();
        _compare.Clear();
        CatalogChanged?.Invoke();
        NotifyNavigationStateChanged();
    }

    void IFolderLoadSink.OnOrderApplied(int count, int currentIndex)
    {
        if (_currentSession?.Folder is { } f)
        {
            FolderText = $"{f}  ({count} ảnh) · Explorer";
        }
        else if (!string.IsNullOrEmpty(FolderText) && !FolderText.Contains("· Explorer"))
        {
            FolderText = $"{FolderText} · Explorer";
        }
        CatalogChanged?.Invoke();
        NotifyNavigationStateChanged();
    }

    void IFolderLoadSink.OnFailed(string folder, Exception exception)
    {
        StatusText = StatusFormatter.FolderOpenFailed(exception.Message);
        NotifyNavigationStateChanged();
    }
}
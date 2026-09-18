using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using PhotoReview.App.Coordinators;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Model;
using PhotoReview.Core.Session;
using PhotoReview.Core.Settings;

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
    private readonly IFileSystem _fileSystem;
    private readonly Action? _resetCachesAction;

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
        Action? resetCachesAction = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _folderCoordinator = folderCoordinator ?? throw new ArgumentNullException(nameof(folderCoordinator));
        _presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));
        _viewerState = viewerState ?? throw new ArgumentNullException(nameof(viewerState));
        _compare = compare ?? throw new ArgumentNullException(nameof(compare));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _sessionStore = sessionStore ?? throw new ArgumentNullException(nameof(sessionStore));
        _fileSystem = fileSystem ?? new PhotoReview.Core.IO.PhysicalFileSystem();
        _resetCachesAction = resetCachesAction;
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

    public void ToggleFit() => _viewerState.ResetFit();
    public void ZoomIn() => _viewerState.ZoomIn();
    public void ZoomOut() => _viewerState.ZoomOut();
    public void ToggleFullscreen() => _viewerState.ToggleFullscreen();
    public void ExitFullscreen() => _viewerState.ExitFullscreen();

    public void UpdateTitle(string? folder = null) => UpdateFolderTitle(folder);

    private void UpdateFolderTitle(string? folder = null)
    {
        folder ??= _currentSession?.Folder ?? "";
        var settings = _settingsStore.Load();
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
        NotifyNavigationStateChanged();
    }

    void IFolderLoadSink.OnOrderApplied(int count, int currentIndex)
    {
        NotifyNavigationStateChanged();
    }

    void IFolderLoadSink.OnFailed(string folder, Exception exception)
    {
        StatusText = StatusFormatter.FolderOpenFailed(exception.Message);
        NotifyNavigationStateChanged();
    }
}
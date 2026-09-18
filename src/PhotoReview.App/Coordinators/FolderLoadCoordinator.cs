using System.IO;
using System.Threading.Tasks;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Model;
using PhotoReview.Core.Session;
using PhotoReview.Core.Settings;

namespace PhotoReview.App.Coordinators;

/// <summary>
/// Điều phối quá trình quét, nạp danh mục và áp dụng thứ tự hiển thị Explorer tự nhiên cho một thư mục ảnh.
/// Đóng gói toàn bộ luồng bất đồng bộ của LoadFolderAsync độc lập với UI.
/// </summary>
public sealed class FolderLoadCoordinator : IDisposable
{
    private readonly ReviewCatalog _catalog;
    private readonly GenerationClock _clock;
    private readonly IExplorerOrderProvider _explorerOrder;
    private readonly IFileSystem _fileSystem;
    private readonly SessionStore _sessionStore;
    private readonly SettingsStore _settingsStore;
    private readonly IFolderLoadSink _sink;

    private CancellationTokenSource? _loadCts;
    private bool _disposed;

    public FolderLoadCoordinator(
        ReviewCatalog catalog,
        GenerationClock clock,
        IExplorerOrderProvider explorerOrder,
        IFileSystem fileSystem,
        SessionStore sessionStore,
        SettingsStore settingsStore,
        IFolderLoadSink sink)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _explorerOrder = explorerOrder ?? throw new ArgumentNullException(nameof(explorerOrder));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _sessionStore = sessionStore ?? throw new ArgumentNullException(nameof(sessionStore));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
    }

    /// <summary>
    /// Thực thi tải thư mục ảnh không đồng bộ.
    /// </summary>
    /// <param name="folder">Đường dẫn thư mục ảnh cần mở.</param>
    /// <param name="initialPath">Đường dẫn tệp tin cụ thể được chọn mở trực tiếp (nếu có).</param>
    public async Task LoadAsync(string folder, string? initialPath = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = new CancellationTokenSource();
        var loadToken = _loadCts.Token;

        var loadGeneration = _clock.NextFolder();

        try
        {
            folder = Path.GetFullPath(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (!_fileSystem.DirectoryExists(folder))
            {
                throw new DirectoryNotFoundException($"Không tìm thấy folder: {folder}");
            }

            var files = await Task.Run(() =>
            {
                return _fileSystem.EnumerateFiles(folder, "*")
                    .Where(ImageFileTypes.IsSupported)
                    .ToList();
            }, loadToken).ConfigureAwait(false);

            var sortMode = _settingsStore.Current.ImageSortMode;
            var scannedFiles = files.ToArray();

            var totalBytesTask = Task.Run(() =>
            {
                return scannedFiles.Sum(path =>
                {
                    try
                    {
                        return _fileSystem.GetFileStat(path)?.Length ?? 0L;
                    }
                    catch
                    {
                        return 0L;
                    }
                });
            }, loadToken);

            var explorerTask = _explorerOrder.TryGetSnapshotProgressiveAsync(
                folder,
                TimeSpan.FromSeconds(2),
                loadToken,
                null,
                16);

            files = await Task.Run(() => ImageSortService.Sort(files, sortMode), loadToken).ConfigureAwait(false);

            if (initialPath is not null)
            {
                var requested = Path.GetFullPath(initialPath);
                var requestedIndex = files.FindIndex(path => string.Equals(path, requested, StringComparison.OrdinalIgnoreCase));
                if (requestedIndex > 0)
                {
                    var selected = files[requestedIndex];
                    files.RemoveAt(requestedIndex);
                    files.Insert(0, selected);
                }
            }

            if (loadToken.IsCancellationRequested || !_clock.IsFolderCurrent(loadGeneration))
            {
                return;
            }

            _sink.ResetCaches();
            var session = _sessionStore.Load(folder);
            _catalog.Reset(files);
            _sink.OnCatalogReady(folder, _catalog.Count);

            var interactionGeneration = _clock.CurrentInteraction;
            var resumePath = initialPath ?? session.CurrentPath;

            ExplorerViewSnapshot? explorerSnapshot = null;
            if (initialPath is not null)
            {
                explorerSnapshot = await explorerTask.ConfigureAwait(false);
                if (loadToken.IsCancellationRequested || !_clock.IsFolderCurrent(loadGeneration))
                {
                    return;
                }
            }

            _ = await totalBytesTask.ConfigureAwait(false);
            if (loadToken.IsCancellationRequested || !_clock.IsFolderCurrent(loadGeneration))
            {
                return;
            }

            if (_catalog.Count > 0)
            {
                var resumeIndex = resumePath is null ? 0 : _catalog.IndexOf(resumePath);
                var targetIndex = resumeIndex >= 0 ? resumeIndex : 0;
                _catalog.SetCurrent(targetIndex);
                var presentationGen = _clock.CurrentNavigation;
                await _sink.PresentAsync(targetIndex, presentationGen).ConfigureAwait(false);
            }
            else
            {
                _clock.NextNavigation();
                _sink.OnEmpty(folder);
            }

            var presentationGeneration = _clock.CurrentNavigation;
            explorerSnapshot ??= await explorerTask.ConfigureAwait(false);

            if (loadToken.IsCancellationRequested || !_clock.IsFolderCurrent(loadGeneration))
            {
                return;
            }

            if (!_clock.IsInteractionCurrent(interactionGeneration))
            {
                return;
            }

            if (ExplorerSnapshotValidator.TryValidate(explorerSnapshot, scannedFiles, out var explorerOrder, out _))
            {
                if (_catalog.ReplaceOrder(explorerOrder))
                {
                    _sink.OnOrderApplied(explorerOrder.Count, _catalog.CurrentIndex);

                    var mayReplaceInitialFallback = initialPath is null && _clock.CurrentNavigation == presentationGeneration;
                    if (mayReplaceInitialFallback && _catalog.Count > 0)
                    {
                        await _sink.PresentAsync(_catalog.CurrentIndex, _clock.CurrentNavigation).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (loadToken.IsCancellationRequested)
        {
            // Bỏ qua khi loadToken bị hủy
        }
        catch (Exception ex) when (_clock.IsFolderCurrent(loadGeneration))
        {
            _sink.OnFailed(folder, ex);
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _loadCts?.Cancel();
            _loadCts?.Dispose();
        }
    }
}

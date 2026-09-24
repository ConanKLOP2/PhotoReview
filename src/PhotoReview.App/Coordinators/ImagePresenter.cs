using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PhotoReview.App.ViewModels;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.Model;
using PhotoReview.Core.Session;
using PhotoReview.Core.Settings;
using PhotoReview.Imaging.Caching;

namespace PhotoReview.App.Coordinators;

/// <summary>
/// Coordinator điều phối toàn bộ quá trình trình diễn ảnh (ShowImageAsync và RemoveMissingCatalogItemAsync),
/// nạp thumbnail placeholder, xử lý ảnh biến mất, kích hoạt preload, compare integration, lưu session;
/// bảo toàn bất biến INV-1 (chống ghi đè khung hình khi chuyển ảnh nhanh).
/// </summary>
public sealed class ImagePresenter
{
    private readonly ReviewCatalog _catalog;
    private readonly GenerationClock _clock;
    private readonly PreviewImageService _previewService;
    private readonly ThumbnailCache _thumbnailCache;
    private readonly IPreloadController _preloadController;
    private readonly CompareViewModel _compareViewModel;
    private readonly FileHashService _hashService;
    private readonly ReviewMetrics _metrics;
    private readonly Func<AppSettings> _getSettings;
    private readonly SessionStore? _sessionStore;
    private readonly SessionWriter? _sessionWriter;
    private readonly IPresentationSink _sink;
    private readonly IFileSystem? _fileSystem;
    private readonly Func<SessionState?>? _getSession;
    private readonly Action<string>? _onPresentedHook;

    // Perf: ComparePairService.BuildIndex is O(n log n) over the whole catalog; rebuilding it on
    // every single navigation would cost as much as the old per-call ComparePairService.Find did.
    // Cache it against ReviewCatalog.StructuralVersion so it's rebuilt only when membership/order
    // actually changes (folder load, file action, reorder), not on every present.
    // PresentAsync bodies for overlapping navigations run concurrently on threadpool threads
    // (async-void key handlers don't serialize each other -- a held-down arrow key can start a
    // second PresentAsync before the first's post-thumbnail-await continuation runs), so this
    // check-then-rebuild pair needs its own lock: without it, two threads could each pass the
    // version check, race to build the same generation's index twice, and interleave writes to
    // these two fields.
    private readonly object _compareIndexGate = new();
    private int _compareIndexVersion = -1;
    private Dictionary<string, (string Left, string Right)>? _compareIndex;

    private readonly IUiScheduler? _uiScheduler;

    public ImagePresenter(
        ReviewCatalog catalog,
        GenerationClock clock,
        PreviewImageService previewService,
        ThumbnailCache thumbnailCache,
        IPreloadController preloadController,
        CompareViewModel compareViewModel,
        FileHashService hashService,
        ReviewMetrics metrics,
        Func<AppSettings> getSettings,
        SessionStore? sessionStore,
        IPresentationSink sink,
        IFileSystem? fileSystem = null,
        Func<SessionState?>? getSession = null,
        Action<string>? onPresentedHook = null,
        SessionWriter? sessionWriter = null,
        IUiScheduler? uiScheduler = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _previewService = previewService ?? throw new ArgumentNullException(nameof(previewService));
        _thumbnailCache = thumbnailCache ?? throw new ArgumentNullException(nameof(thumbnailCache));
        _preloadController = preloadController ?? throw new ArgumentNullException(nameof(preloadController));
        _compareViewModel = compareViewModel ?? throw new ArgumentNullException(nameof(compareViewModel));
        _hashService = hashService ?? throw new ArgumentNullException(nameof(hashService));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _getSettings = getSettings ?? throw new ArgumentNullException(nameof(getSettings));
        _sessionStore = sessionStore;
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _fileSystem = fileSystem;
        _getSession = getSession;
        _onPresentedHook = onPresentedHook;
        _uiScheduler = uiScheduler;
        _sessionWriter = sessionWriter;
    }

    public ReviewCatalog Catalog => _catalog;
    public GenerationClock Clock => _clock;
    public CompareViewModel Compare => _compareViewModel;
    public object? CurrentImage { get; private set; }
    public string StatusText { get; private set; } = string.Empty;
    public bool IsCompareVisible => _compareViewModel.IsVisible;

    /// <summary>Clears the displayed frame when the catalog has no images.</summary>
    public void ClearPresentation()
    {
        UpdateCurrentImage(null);
        _compareViewModel.Clear();
    }

    /// <summary>
    /// Gỡ bỏ ảnh khỏi cache RAM/decode khi file bị di chuyển hoặc xóa.
    /// </summary>
    public void EvictCachedPath(string path)
    {
        _previewService.EvictCachedPath(path, normalized => _preloadController.RemovePreloadedKeysForPath(normalized));
    }

    /// <summary>
    /// Điều phối hiển thị ảnh tại vị trí index chỉ định trong danh mục.
    /// </summary>
    public async Task PresentAsync(int index, bool allowCompare = true)
    {
        if (index < 0 || index >= _catalog.Count) return;

        var perf = PhotoReviewPerf.Log.IsEnabled();
        var presentStopwatch = Stopwatch.StartNew();

        // 1. Tăng Navigation generation
        var token = _clock.NextNavigation();
        _catalog.SetCurrent(index);
        var path = _catalog.PathAt(index);

        var perfPathId = perf ? PhotoReviewPerf.PathId(path) : "";
        if (perf)
        {
            PhotoReviewPerf.NavContext = token;
            PhotoReviewPerf.Log.ShowStart(token, index, _getSettings().LoadingMode.ToString());
        }

        _compareViewModel.Select(null);

        if (AppLog.Enabled)
            AppLog.Info($"ShowImage start index={index} count={_catalog.Count} token={token} path={path}");

        // 2. Stat; file mất thì xóa khỏi catalog và chuyển tiếp
        long perfStat = perf ? Stopwatch.GetTimestamp() : 0;
        if (!TryGetFileInfo(path, out var initialInfo))
        {
            if (perf) PhotoReviewPerf.Log.Stat(token, PhotoReviewPerf.Ms(perfStat));
            await RemoveMissingCatalogItemAsync(path, index, token);
            return;
        }

        var initialEntry = _catalog.Find(path);
        var initialSize = initialEntry?.Length ?? initialInfo.Length;
        var currentKey = initialEntry?.Length is not null && initialEntry.LastWriteUtc is not null
            ? _previewService.GetCurrentCacheKey(initialEntry)
            : _previewService.GetCurrentCacheKey(initialInfo);
        if (perf) PhotoReviewPerf.Log.Stat(token, PhotoReviewPerf.Ms(perfStat));

        // 3. Tạo key, RAM hit (ghi nhận preload hit)
        var ramReady = _previewService.TryGetCachedPreview(currentKey, out var readyImage);
        var hasInflight = !ramReady && _previewService.HasInflightPreview(currentKey);
        if (perf)
        {
            PhotoReviewPerf.Log.Lookup(token, perfPathId, ramReady ? "ramHit" : hasInflight ? "inflight" : "miss");
        }

        if (ramReady)
        {
            if (_preloadController.TryConsumePreloadedKey(currentKey))
                _metrics.RecordPreloadHit();
        }

        var settings = _getSettings();
        var initialStatus = ramReady
            ? StatusFormatter.Ready(index, _catalog.Count, initialSize, Path.GetFileName(path))
            : StatusFormatter.Loading(index, _catalog.Count, initialSize);

        UpdateStatus(initialStatus);

        if (AppLog.Enabled)
            AppLog.Info($"ShowImage cache-state token={token} path={path} ramReady={ramReady} cacheBytes={_previewService.CacheBytes}");

        try
        {
            // Perf: start the preview decode immediately (instead of after the thumbnail below) so
            // the two run concurrently. GetPreviewAsync's in-flight dedup means calling it here just
            // starts (or joins) the same decode that step 5 used to start only after the thumbnail
            // finished, which serialized two independent pieces of I/O + decode work.
            var previewTask = ramReady ? null : _previewService.GetPreviewAsync(path, currentKey);

            // 4. Nếu mode Preview, chưa có trong RAM và chưa in-flight: chạy song song thumbnail và
            // preview, hiển thị bất kỳ cái nào xong trước. Nếu preview thắng, bỏ qua thumbnail hoàn
            // toàn (không chờ, không hiển thị) -- nó đã lỗi thời trước khi kịp lên màn hình.
            if (settings.LoadingMode == LoadingMode.Preview && !ramReady && !hasInflight)
            {
                long perfThumb = perf ? Stopwatch.GetTimestamp() : 0;
                if (perf) PhotoReviewPerf.Log.ThumbStart(token, perfPathId);

                var thumbnailTask = _thumbnailCache.GetAsync(path);
                // Cast to the non-generic Task overload: thumbnailTask (IDecodedImage?) and
                // previewTask (IDecodedImage) have different nullability of the same reference
                // type, and Task.WhenAny<T> can't unify those without a nullability warning.
                var firstDone = await Task.WhenAny((Task)thumbnailTask, previewTask!);

                if (ReferenceEquals(firstDone, thumbnailTask))
                {
                    // The thumbnail task itself never throws (ThumbnailCache/EmbeddedThumbnailReader
                    // treat every read/decode failure as "no thumbnail"), so no try/catch is needed
                    // here; a genuine fault would still be handled the same way as before by
                    // PresentAsync's own catch clauses below.
                    var thumbnail = await thumbnailTask;

                    if (perf) PhotoReviewPerf.Log.ThumbEnd(token, perfPathId, "unknown", PhotoReviewPerf.Ms(perfThumb));

                    // INV-1: kiểm tra token sau await
                    if (!_clock.IsNavigationCurrent(token)) return;

                    if (thumbnail is not null)
                    {
                        UpdateCurrentImage(thumbnail.PlatformImage);
                        if (perf) _sink.TracePresented(token, "thumbnail", Stopwatch.GetTimestamp());

                        if (AppLog.Enabled) AppLog.Info($"ShowImage thumbnail-presented token={token} path={path}");

                        _sink.ApplyInitialViewMode();
                        UpdateStatus(StatusFormatter.LoadingFullRes(index, _catalog.Count, initialSize));
                    }
                    // else: source has no embedded thumbnail (or isn't a JPEG) -- nothing to show
                    // yet; fall through to step 5, which is already awaiting the same preview task.
                }
                else
                {
                    // The preview finished first: let thumbnailTask keep running in the background
                    // (it just warms ThumbnailCache's RAM/disk cache for a later visit) and go
                    // straight to presenting the preview below. Observe any fault so a background
                    // ThumbnailCache failure never surfaces as an unobserved task exception.
                    _ = thumbnailTask.ContinueWith(
                        t => _ = t.Exception,
                        CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                }
            }

            // 5. Decode rồi present (đo UiAssign), kích preload
            var image = ramReady ? readyImage : await previewTask!;
            if (ramReady) _metrics.RecordCacheHit();

            // INV-1: kiểm tra token sau await
            if (!_clock.IsNavigationCurrent(token)) return;

            long perfAssign = perf ? Stopwatch.GetTimestamp() : 0;
            var uiAssign = Stopwatch.StartNew();

            UpdateCurrentImage(image.PlatformImage);

            long perfAssigned = perf ? Stopwatch.GetTimestamp() : 0;
            _metrics.RecordUiAssign(uiAssign.ElapsedMilliseconds);
            if (perf) PhotoReviewPerf.Log.Assign(token, (perfAssigned - perfAssign) * 1000.0 / Stopwatch.Frequency, image.PixelWidth, image.PixelHeight);

            if (AppLog.Enabled) AppLog.Info($"ShowImage preview-presented token={token} path={path} mode={settings.LoadingMode}");

            _sink.OnPresented(path);
            _onPresentedHook?.Invoke(path);

            long perfKick = perf ? Stopwatch.GetTimestamp() : 0;
            if (perf) PhotoReviewPerf.Log.PostStart(token, "preloadKick");
            _ = _preloadController.PreloadAroundAsync(index);
            if (perf) PhotoReviewPerf.Log.PostEnd(token, "preloadKick", PhotoReviewPerf.Ms(perfKick));

            // 6. Compare (qua CompareViewModel) hoặc lấy dimension (Original thì lấy từ ảnh)
            long perfCompare = perf ? Stopwatch.GetTimestamp() : 0;
            var pair = GetComparePair(path);

            if (perf)
            {
                if (pair is null) _sink.TracePresented(token, "final", perfAssigned);
                else PhotoReviewPerf.Log.PostStart(token, "compare");
            }

            if (pair is not null && allowCompare)
            {
                UpdateCurrentImage(null);
                var loaded = await _compareViewModel.LoadAsync(
                    pair.Value,
                    token,
                    t => _clock.IsNavigationCurrent(t),
                    async p =>
                    {
                        var prev = await _previewService.GetPreviewAsync(p);
                        return prev.PlatformImage;
                    },
                    p => _hashService.GetAsync(p),
                    compareSizeEnabled: settings.CompareSizeEnabled,
                    compareHashEnabled: settings.CompareHashEnabled,
                    currentIndex: index,
                    totalFiles: _catalog.Count,
                    initialSelectedPath: path);

                if (!loaded || !_clock.IsNavigationCurrent(token)) return;

                if (perf) _sink.TracePresented(token, "compare", Stopwatch.GetTimestamp());
                UpdateStatus(_compareViewModel.StatusText);
                if (perf) PhotoReviewPerf.Log.PostEnd(token, "compare", PhotoReviewPerf.Ms(perfCompare));
            }
            else
            {
                _compareViewModel.Clear();
                _sink.ApplyInitialViewMode();

                // Let the frame containing the new image render before the status/session bookkeeping
                // below. Original dimensions are usually known already (seeded by the decode), so the
                // await further down completes synchronously; without this yield the stat + status +
                // session work ran before the first frame (+~20 ms to first visual on folder open).
                if (_uiScheduler is not null)
                {
                    await _uiScheduler.YieldAsync();
                    if (!_clock.IsNavigationCurrent(token)) return;
                }

                long perfDims = perf && settings.LoadingMode != LoadingMode.Original ? Stopwatch.GetTimestamp() : 0;
                if (perfDims != 0) PhotoReviewPerf.Log.PostStart(token, "dims");

                var original = settings.LoadingMode == LoadingMode.Original
                    ? (Width: image.PixelWidth, Height: image.PixelHeight)
                    : await _previewService.GetOriginalDimensionsAsync(path);

                if (perfDims != 0) PhotoReviewPerf.Log.PostEnd(token, "dims", PhotoReviewPerf.Ms(perfDims));

                if (!_clock.IsNavigationCurrent(token)) return;

                if (!TryGetFileInfo(path, out var currentInfo)) return;
                if (initialEntry is not null && (initialEntry.Length != currentInfo.Length || initialEntry.LastWriteUtc != currentInfo.LastWriteTimeUtc))
                {
                    _catalog.UpdateMetadata(path, currentInfo.Length, currentInfo.LastWriteTimeUtc);
                }

                UpdateStatus(StatusFormatter.WithDimensions(index, _catalog.Count, currentInfo.Length, original.Width, original.Height, Path.GetFileName(path)));
            }

            // 7. Cập nhật status, lưu session, ghi metric Presented
            if (!_clock.IsNavigationCurrent(token)) return;

            var session = _getSession?.Invoke();
            if (session is not null && _sessionStore is not null)
            {
                session.CurrentPath = path;
                session.UpdatedUtc = DateTime.UtcNow;

                long perfSession = perf ? Stopwatch.GetTimestamp() : 0;
                if (perf) PhotoReviewPerf.Log.PostStart(token, "session");
                if (_sessionWriter is not null) _sessionWriter.Update(session); // debounced; flushed on folder change and shutdown
                else _sessionStore.Save(session);
                if (perf) PhotoReviewPerf.Log.PostEnd(token, "session", PhotoReviewPerf.Ms(perfSession));
            }

            presentStopwatch.Stop();
            _metrics.RecordPresented(presentStopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex) when (_clock.IsNavigationCurrent(token) && (ex is FileNotFoundException || ex is DirectoryNotFoundException))
        {
            if (AppLog.Enabled) AppLog.Info($"ShowImage stale-file token={token} path={path}");
            await RemoveMissingCatalogItemAsync(path, index, token);
        }
        catch (Exception ex) when (_clock.IsNavigationCurrent(token))
        {
            AppLog.Error($"ShowImage failed token={token} index={index} path={path}", ex);
            UpdateStatus(StatusFormatter.ImageError(Path.GetFileName(path), ex.Message));
        }
        catch (Exception ex)
        {
            AppLog.Error($"ShowImage failed (stale token={token}, current={_clock.CurrentNavigation}) index={index} path={path}", ex);
        }
    }

    /// <summary>
    /// Xóa tệp không tồn tại khỏi danh mục và tự động chuyển đến ảnh kế tiếp.
    /// </summary>
    public async Task RemoveMissingCatalogItemAsync(string path, int index, long token)
    {
        if (!_clock.IsNavigationCurrent(token)) return;

        var nextIndex = _catalog.Remove(path);
        if (_catalog.Count == 0)
        {
            UpdateCurrentImage(null);
            _compareViewModel.Clear();
            UpdateStatus(StatusFormatter.NoImagesRemaining());
            return;
        }

        if (nextIndex >= 0)
        {
            await PresentAsync(nextIndex);
        }
    }

    private (string Left, string Right)? GetComparePair(string path)
    {
        lock (_compareIndexGate)
        {
            if (_compareIndexVersion != _catalog.StructuralVersion)
            {
                _compareIndex = ComparePairService.BuildIndex(_catalog.Paths);
                _compareIndexVersion = _catalog.StructuralVersion;
            }
            return _compareIndex!.TryGetValue(path, out var pair) ? pair : null;
        }
    }

    private void UpdateCurrentImage(object? image)
    {
        CurrentImage = image;
        _sink.SetCurrentImage(image);
    }

    private void UpdateStatus(string status)
    {
        StatusText = status;
        _sink.SetStatusText(status);
    }

    private bool TryGetFileInfo(string path, out FileInfo info)
    {
        try
        {
            info = new FileInfo(path);
            // When an IFileSystem is available, its (counted, mockable) existence check is the
            // source of truth and FileInfo.Exists below would just be a second, redundant stat.
            if (_fileSystem != null)
            {
                if (!_fileSystem.FileExists(path)) { info = null!; return false; }
                return true;
            }
            if (!info.Exists) { info = null!; return false; }
            return true;
        }
        catch (FileNotFoundException) { info = null!; return false; }
        catch (DirectoryNotFoundException) { info = null!; return false; }
        catch { info = null!; return false; }
    }
}

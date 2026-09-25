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
using PhotoReview.Core.Localization;

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
    // perf(preload): cancels the current navigation's viewer decode when the next navigation starts.
    // A superseded source is cancelled, then disposed; none ever uses a timer or WaitHandle.
    private CancellationTokenSource? _viewerDecodeCts;
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
        _zoomDetail = new ZoomDetailLoader(_previewService, _clock, UpdateCurrentImage);
    }

    private readonly ZoomDetailLoader _zoomDetail;

    public ReviewCatalog Catalog => _catalog;
    public GenerationClock Clock => _clock;
    public CompareViewModel Compare => _compareViewModel;
    public object? CurrentImage { get; private set; }

    /// <summary>
    /// Full-resolution (post-orientation) size of the source behind <see cref="CurrentImage"/>, whatever
    /// bitmap (thumbnail, preview, full decode) is displayed; 0 when unknown or nothing is shown.
    /// </summary>
    public int CurrentOriginalWidth { get; private set; }

    /// <summary>See <see cref="CurrentOriginalWidth"/>.</summary>
    public int CurrentOriginalHeight { get; private set; }

    public string StatusText { get; private set; } = string.Empty;

    /// <summary>
    /// True when the latest <see cref="PresentAsync"/> found its preview already in the RAM cache, i.e. it showed the
    /// image straight away instead of the loading status. A language-independent state for probes and tests.
    /// </summary>
    public bool LastPresentStartedFromRam { get; private set; }

    public bool IsCompareVisible => _compareViewModel.IsVisible;

    /// <summary>feat(zoom): on-demand full-resolution decode of the current image while zoomed.</summary>
    public ZoomDetailLoader ZoomDetail => _zoomDetail;

    /// <summary>
    /// feat(zoom): the viewer's zoom changed -- <paramref name="zoom"/> is original-relative, or null
    /// for Fit. Outside Fit the current image's original is decoded on demand and swapped in.
    /// </summary>
    public void SetViewerZoom(double? zoom) => _zoomDetail.SetZoom(zoom);

    /// <summary>Clears the displayed frame when the catalog has no images.</summary>
    public void ClearPresentation()
    {
        CurrentPhotoInfo = null;
        _zoomDetail.Reset();
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
        LastPresentStartedFromRam = false;

        var perf = PhotoReviewPerf.Log.IsEnabled();
        var presentStopwatch = Stopwatch.StartNew();

        // 1. Tăng Navigation generation
        var token = _clock.NextNavigation();
        _catalog.SetCurrent(index);
        var path = _catalog.PathAt(index);

        // perf(preload): this navigation supersedes the previous one -- drop its viewer decode if it
        // has not started yet (a started one finishes and stays cached), and let preload re-center and
        // track direction/key rate now rather than only after this image is presented.
        var viewerDecodeCts = new CancellationTokenSource();
        var supersededCts = Interlocked.Exchange(ref _viewerDecodeCts, viewerDecodeCts);
        if (supersededCts is not null)
        {
            supersededCts.Cancel();
            supersededCts.Dispose();
        }
        // feat(zoom): drop the previous image's zoom-detail decode and release its original (~96 MB
        // for 24 MP) now; this navigation's preview is shown at the same original-relative zoom and
        // its own original is fetched once that preview is up.
        _zoomDetail.Reset();
        _preloadController.NotifyNavigation(index);

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
        var initialOutcome = TryGetFileStat(path, out var initialStat, out var initialStatError);
        if (initialOutcome != StatOutcome.Found)
        {
            if (perf) PhotoReviewPerf.Log.Stat(token, PhotoReviewPerf.Ms(perfStat));
            if (initialOutcome == StatOutcome.Missing)
            {
                await RemoveMissingCatalogItemAsync(path, index, token);
            }
            else
            {
                // An unreadable file (share hiccup, access denied) is not a missing one: keep it in the catalog.
                AppLog.Error($"ShowImage stat failed token={token} index={index} path={path}", initialStatError!);
                CurrentPhotoInfo = null;
                // The status names this file, so the previous photo must not stay visible under it.
                UpdateCurrentImage(null);
                UpdateStatus(StatusFormatter.ImageError(Path.GetFileName(path), UserFacingError.Describe(initialStatError!)));
            }

            return;
        }

        // R7-1: the key must describe the file as it is now, not as the folder scan saw it. A photo edited in
        // another app since the scan kept the old Length/mtime in the catalog, so the viewer served the old
        // preview from RAM, or decoded and then failed MatchesCurrentSource forever; preload (which reads the
        // catalog) cached under keys the viewer never asked for. Refresh the entry from the stat just taken
        // (no extra I/O) and drop the old version's RAM entries (stale disk entries are keyed by length+mtime
        // and are never served; the disk LRU prunes them).
        var initialEntry = _catalog.Find(path);
        if (initialEntry is not null && !initialEntry.Matches(initialStat))
        {
            if (initialEntry.Length is not null && initialEntry.LastWriteUtc is not null)
                EvictCachedPath(path);
            _catalog.UpdateMetadata(path, initialStat.Length, initialStat.LastWriteUtc);
            initialEntry = _catalog.Find(path);
        }
        var initialSize = initialStat.Length;
        var currentKey = initialEntry is not null
            ? _previewService.GetCurrentCacheKey(initialEntry)
            : _previewService.GetCurrentCacheKey(path);
        if (perf) PhotoReviewPerf.Log.Stat(token, PhotoReviewPerf.Ms(perfStat));

        // 3. Tạo key, RAM hit (ghi nhận preload hit)
        var ramReady = _previewService.TryGetCachedPreview(currentKey, out var readyImage);
        LastPresentStartedFromRam = ramReady;
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

        // Photo information line: never show the previous image's EXIF; a RAM hit already has this image's.
        CurrentPhotoInfo = ramReady ? PhotoInfo.From(path, readyImage) : null;

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
            // perf(preload): the viewer's decode gets its own priority lane and is dropped (before it
            // starts) when a newer navigation supersedes this one; see GetViewerPreviewAsync.
            var previewTask = ramReady ? null : _previewService.GetViewerPreviewAsync(path, currentKey,
                viewerDecodeCts.Token, _preloadController.GetViewerDecodeDelay());
            // R2-F-29: a superseded navigation returns without awaiting previewTask; observe a later fault here so it
            // is not reported context-free by TaskScheduler.UnobservedTaskException at GC time. Awaiting it below
            // still throws as before (a continuation does not consume the exception).
            _ = previewTask?.ContinueWith(
                t => _ = t.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

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
                        // Size the placeholder like the source (if any earlier decode already told us
                        // its dimensions) so a non-Fit zoom doesn't jump when the preview replaces it.
                        var (thumbWidth, thumbHeight) = _previewService.TryGetKnownOriginalDimensions(currentKey, out var known)
                            ? known
                            : (thumbnail.OriginalWidth, thumbnail.OriginalHeight);
                        UpdateCurrentImage(thumbnail.PlatformImage, thumbWidth, thumbHeight);
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

            CurrentPhotoInfo = PhotoInfo.From(path, image);
            UpdateCurrentImage(image.PlatformImage, image.OriginalWidth, image.OriginalHeight);

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
                CurrentPhotoInfo = null; // two images: the compare status describes them
                UpdateCurrentImage(null);
                bool loaded;
                try
                {
                    loaded = await _compareViewModel.LoadAsync(
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
                }
                catch (Exception ex) when (ex is not OperationCanceledException && _clock.IsNavigationCurrent(token))
                {
                    // The compare partner vanished or cannot be decoded. That must not take the current image (which is
                    // fine) out of the catalog via the stale-file path below: show it alone and name the partner.
                    var partner = string.Equals(pair.Value.Left, path, StringComparison.OrdinalIgnoreCase) ? pair.Value.Right : pair.Value.Left;
                    AppLog.Error($"ShowImage compare partner failed token={token} path={path} partner={partner}", ex);
                    _compareViewModel.Clear();
                    CurrentPhotoInfo = PhotoInfo.From(path, image);
                    UpdateCurrentImage(image.PlatformImage, image.OriginalWidth, image.OriginalHeight);
                    _sink.ApplyInitialViewMode();
                    UpdateStatus(StatusFormatter.ImageError(Path.GetFileName(partner), UserFacingError.Describe(ex)));
                    return;
                }

                if (!loaded || !_clock.IsNavigationCurrent(token)) return;

                if (perf) _sink.TracePresented(token, "compare", Stopwatch.GetTimestamp());
                UpdateStatus(_compareViewModel.StatusText);
                if (perf) PhotoReviewPerf.Log.PostEnd(token, "compare", PhotoReviewPerf.Ms(perfCompare));
            }
            else
            {
                _compareViewModel.Clear();
                _sink.ApplyInitialViewMode();
                // feat(zoom): if the viewer is (still) zoomed after the initial view mode, start this
                // image's full-resolution decode; the preview stays up until it is ready.
                _zoomDetail.OnPreviewPresented(token, path, currentKey, image);

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

                if (TryGetFileStat(path, out var currentInfo, out _) != StatOutcome.Found) return;
                if (initialEntry is not null && (initialEntry.Length != currentInfo.Length || initialEntry.LastWriteUtc != currentInfo.LastWriteUtc))
                {
                    _catalog.UpdateMetadata(path, currentInfo.Length, currentInfo.LastWriteUtc);
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
        catch (OperationCanceledException) when (!_clock.IsNavigationCurrent(token))
        {
            // perf(preload): a newer navigation cancelled this one's not-yet-started viewer decode.
            if (AppLog.Enabled) AppLog.Info($"ShowImage superseded token={token} path={path}");
        }
        catch (Exception ex) when (_clock.IsNavigationCurrent(token) && (ex is FileNotFoundException || ex is DirectoryNotFoundException))
        {
            if (AppLog.Enabled) AppLog.Info($"ShowImage stale-file token={token} path={path}");
            await RemoveMissingCatalogItemAsync(path, index, token);
        }
        catch (Exception ex) when (_clock.IsNavigationCurrent(token))
        {
            AppLog.Error($"ShowImage failed token={token} index={index} path={path}", ex);
            CurrentPhotoInfo = null;
            UpdateStatus(StatusFormatter.ImageError(Path.GetFileName(path), UserFacingError.Describe(ex)));
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
        // R2-F-07: iterative. Each missing file used to call PresentAsync, which found the next one missing and
        // recursed; every call completes synchronously, so N consecutive missing files (an ejected card or a dropped
        // share) meant N nested async frames on the UI thread. Skip the run of missing files here and present once.
        while (true)
        {
            if (!_clock.IsNavigationCurrent(token)) return;

            var nextIndex = _catalog.Remove(path);
            if (_catalog.Count == 0)
            {
                CurrentPhotoInfo = null;
                _zoomDetail.Reset();
                UpdateCurrentImage(null);
                _compareViewModel.Clear();
                UpdateStatus(StatusFormatter.NoImagesRemaining());
                return;
            }

            if (nextIndex < 0 || nextIndex >= _catalog.Count) return;

            var nextPath = _catalog.PathAt(nextIndex);
            // Only a file that is really gone is skipped; an unreadable one is presented so its error is reported.
            if (TryGetFileStat(nextPath, out _, out _) != StatOutcome.Missing)
            {
                await PresentAsync(nextIndex);
                return;
            }
            path = nextPath;
        }
    }

    /// <summary>True when <paramref name="path"/> belongs to a numbered compare pair (lets the compare key open compare while it is closed).</summary>
    public bool HasComparePair(string path) => GetComparePair(path) is not null;

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

    private void UpdateCurrentImage(object? image, int originalWidth = 0, int originalHeight = 0)
    {
        // Dimensions first: the sink's image-changed callback reads them to size the element
        // (feat(zoom): original-relative zoom), in the same UI pass as the new Source.
        CurrentOriginalWidth = image is null ? 0 : originalWidth;
        CurrentOriginalHeight = image is null ? 0 : originalHeight;
        CurrentImage = image;
        _sink.SetCurrentImage(image);
    }

    private void UpdateStatus(string status)
    {
        StatusText = status;
        _sink.SetStatusText(status);
    }

    private enum StatOutcome { Found, Missing, Error }

    /// <summary>
    /// One stat: existence plus the Length/LastWriteUtc the cache key is built from. Only "not there" is
    /// <see cref="StatOutcome.Missing"/> (the caller drops the item from the catalog); any other failure is
    /// <see cref="StatOutcome.Error"/> with the exception, so a flaky share never silently loses a photo.
    /// </summary>
    private StatOutcome TryGetFileStat(string path, out FileStat stat, out Exception? error)
    {
        stat = null!;
        error = null;
        try
        {
            // When an IFileSystem is available, its (counted, mockable) stat is the source of truth.
            if (_fileSystem != null)
            {
                if (_fileSystem.GetFileStat(path) is not { } fsStat) return StatOutcome.Missing;
                stat = fsStat;
                return StatOutcome.Found;
            }
            var info = new FileInfo(path);
            if (!info.Exists) return StatOutcome.Missing;
            stat = new FileStat(info.Length, info.LastWriteTimeUtc);
            return StatOutcome.Found;
        }
        // A path that can never name a file (blank, illegal characters, too long, unsupported) is as good as missing.
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            return StatOutcome.Missing;
        }
        catch (Exception ex)
        {
            error = ex;
            return StatOutcome.Error;
        }
    }

    /// <summary>
    /// What the photo information line describes: the presented image's file name, original size and the EXIF its
    /// decoder (or preview disk-cache entry) carried -- null while loading, in compare mode or with nothing shown.
    /// Set before the sink notification of the same step, so bindings refreshed by it already see the new value.
    /// </summary>
    public PhotoInfo? CurrentPhotoInfo { get; private set; }
}

/// <summary>Input of the photo information line (see <see cref="ImagePresenter.CurrentPhotoInfo"/>).</summary>
public sealed record PhotoInfo(string FileName, int Width, int Height, PhotoReview.Imaging.Metadata.ExifSummary? Exif)
{
    public static PhotoInfo From(string path, PhotoReview.Imaging.Decoding.IDecodedImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return new PhotoInfo(Path.GetFileName(path), image.OriginalWidth, image.OriginalHeight, image.Exif);
    }
}

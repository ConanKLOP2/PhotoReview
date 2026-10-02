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
using PhotoReview.Core.IO;
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

    // feat/image-crossfade: the file path currently anchoring the displayed bitmap (whatever stage:
    // thumbnail, preview or original). Used to tell "this PresentAsync moved to a different file" (fire the
    // transition once) apart from "this PresentAsync is upgrading the SAME file's bitmap" (thumbnail -> preview
    // -> original within one navigation must stay an instant, seamless replacement). Null before the first image.
    private string? _presentedFilePath;
    private string? _currentNavigationPath;

    private readonly IUiScheduler? _uiScheduler;

    // Waits before the single retry of a compare decode the LibRaw gate refused (DecoderBusyException). Injectable so tests
    // never depend on wall-clock time.
    private readonly Func<TimeSpan, CancellationToken, Task> _busyRetryDelay;
    private static readonly TimeSpan BusyRetryDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// PR-B: lets <c>MainWindow</c> hook <c>WpfPresentationSink.ApplyInitialViewModeOverride</c> to
    /// <c>PointerInputController.ApplyInitialViewAsync</c> after the pointer controller (which owns the surface) is
    /// constructed -- the sink itself is built earlier, in <c>MainViewModelCompositionRoot</c>, before the window exists.
    /// </summary>
    public IPresentationSink Sink => _sink;

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
        IUiScheduler? uiScheduler = null,
        Func<TimeSpan, CancellationToken, Task>? busyRetryDelay = null)
    {
        _busyRetryDelay = busyRetryDelay ?? Task.Delay;
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
        // feat/image-crossfade: ZoomDetailLoader upgrades the SAME image's bitmap (zoom-triggered full-resolution
        // decode) -- never a file change, so no path is passed (UpdateCurrentImage's isFileChange stays false).
        _zoomDetail = new ZoomDetailLoader(_previewService, _clock,
            ShowZoomDetailImage, _uiScheduler);
    }

    private readonly ZoomDetailLoader _zoomDetail;
    private PhotoInfo? _infoWhileOriginalShown; // the preview's PhotoInfo, kept while the full decode replaces it

    /// <summary>
    /// ZoomDetailLoader swapped the displayed bitmap. Q-RAW-03: while the full-resolution original is shown the
    /// info line must not say "RAW preview"; swapping back to the preview restores it.
    /// </summary>
    private void ShowZoomDetailImage(object image, int w, int h)
    {
        IsSameSourceSwap = true; // read synchronously by the sink's image-changed callback (ViewerState.SwapSourceSize)
        try { ShowZoomDetailImageCore(image, w, h); }
        finally { IsSameSourceSwap = false; }
    }

    private void ShowZoomDetailImageCore(object image, int w, int h)
    {
        // Decided from the shown bitmap itself, not from "the held original is shown": a held original that is still the
        // embedded JPEG (a full decode that fell back to it) is still a RAW preview and keeps its info line.
        var showingOriginal = _zoomDetail.HeldOriginal is { } held && ReferenceEquals(held.PlatformImage, image)
            && held is not PhotoReview.Imaging.Decoding.IRawPreviewInfo { EmbeddedPreviewWidth: > 0 };
        if (showingOriginal)
        {
            // Always reassigned (null for a non-RAW photo) so a value left by an earlier photo can never be restored.
            _infoWhileOriginalShown = CurrentPhotoInfo is { RawPreviewWidth: > 0 } current ? current : null;
            if (_infoWhileOriginalShown is not null)
                CurrentPhotoInfo = _infoWhileOriginalShown with { RawPreviewWidth = 0, RawPreviewHeight = 0 };
        }
        else if (_infoWhileOriginalShown is { } preview)
        {
            if (CurrentPhotoInfo is not null) CurrentPhotoInfo = preview;
            _infoWhileOriginalShown = null;
        }
        UpdateCurrentImage(image, w, h);
    }

    public ReviewCatalog Catalog => _catalog;
    public GenerationClock Clock => _clock;
    public CompareViewModel Compare => _compareViewModel;
    public object? CurrentImage { get; private set; }
    public string? CurrentPresentedPath => _currentNavigationPath;

    /// <summary>
    /// Full-resolution (post-orientation) size of the source behind <see cref="CurrentImage"/>, whatever
    /// bitmap (thumbnail, preview, full decode) is displayed; 0 when unknown or nothing is shown. One size per displayed
    /// bitmap: a full decode reports its own pixel size (ADR 0008 amendment).
    /// </summary>
    public int CurrentOriginalWidth { get; private set; }

    /// <summary>
    /// True only while the sink is told about a zoom-detail swap (preview <-> full decode of the SAME image): the new
    /// <see cref="CurrentOriginalWidth"/>/<see cref="CurrentOriginalHeight"/> are the new bitmap's own size, which may
    /// differ slightly from the previous one (RAW). The viewer then keeps its view anchored instead of treating it as a new image.
    /// </summary>
    public bool IsSameSourceSwap { get; private set; }

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
        _currentNavigationPath = null;
        CurrentPhotoInfo = null;
        _infoWhileOriginalShown = null;
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
    public Task PresentAsync(int index, bool allowCompare = true, string? pathOverride = null, bool includeCaptureGroupInCompare = false) =>
        PresentCoreAsync(index, allowCompare, pathOverride, includeCaptureGroupInCompare, staleNotFoundRetries: 0, originalPreviousNavigationPath: null);

    /// <summary>Re-presents after a stale FileNotFound are bounded: the file system is re-checked each time and a file that exists is kept.</summary>
    private const int MaxStaleNotFoundRetries = 3;

    /// <param name="staleNotFoundRetries">How many re-presents already followed a FileNotFound the file system contradicts (see the catch in this method); at most <see cref="MaxStaleNotFoundRetries"/>.</param>
    /// <param name="originalPreviousNavigationPath">Only for that retry: the path that was really on screen before the FIRST attempt,
    /// so a later generic failure restores the member that is shown instead of the retried (failed) member's own path.</param>
    private async Task PresentCoreAsync(int index, bool allowCompare, string? pathOverride, bool includeCaptureGroupInCompare, int staleNotFoundRetries,
        string? originalPreviousNavigationPath)
    {
        if (index < 0 || index >= _catalog.Count) return;
        if (pathOverride is not null && _catalog.IndexOf(pathOverride) != index)
        {
            throw new ArgumentException("The presentation path must belong to the selected catalog entry.", nameof(pathOverride));
        }
        LastPresentStartedFromRam = false;

        var perf = PhotoReviewPerf.Log.IsEnabled();
        var presentStopwatch = Stopwatch.StartNew();

        // 1. Tăng Navigation generation
        var token = _clock.NextNavigation();
        _catalog.SetCurrent(index);
        var path = pathOverride ?? _catalog.PathAt(index);
        var previousNavigationPath = staleNotFoundRetries > 0 ? originalPreviousNavigationPath : _currentNavigationPath;
        _currentNavigationPath = path;

        // perf(preload): this navigation supersedes the previous one -- drop its viewer decode if it
        // has not started yet (a started one finishes and stays cached), and let preload re-center and
        // track direction/key rate now rather than only after this image is presented.
        var viewerDecodeCts = new CancellationTokenSource();
        var viewerDecodeToken = viewerDecodeCts.Token; // read now: a later navigation disposes the source
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
        // Q-R29 option C: the stat below is awaited off the UI thread, so this navigation is "in progress" from here on:
        // the photo information line must not keep describing the previous image meanwhile (it is set again below).
        CurrentPhotoInfo = null;
        _infoWhileOriginalShown = null; // the previous photo's preview info must never be restored onto this one

        if (AppLog.Enabled)
            AppLog.Info($"ShowImage start index={index} count={_catalog.Count} token={token} path={path}");

        // 2. Stat; file mất thì xóa khỏi catalog và chuyển tiếp
        // Q-R29 option C: the stat runs on the navigation stat worker, never on the UI thread -- on a NAS/wifi share
        // one metadata call is 2-30 ms (or a stalled SMB request), all of it UI-thread time when taken here directly.
        // Nothing below looks at a cache before this stat returns, so a changed file can never be served stale from
        // RAM; a navigation superseded while its stat was pending stops here without touching the catalog or screen.
        long perfStat = perf ? Stopwatch.GetTimestamp() : 0;
        StatResult initial;
        try
        {
            initial = await StatOffUiThreadAsync(path, viewerDecodeCts.Token);
        }
        catch (OperationCanceledException) when (!_clock.IsNavigationCurrent(token))
        {
            if (AppLog.Enabled) AppLog.Info($"ShowImage superseded during stat token={token} path={path}");
            return;
        }
        if (!_clock.IsNavigationCurrent(token)) return;
        var initialOutcome = initial.Outcome;
        if (initialOutcome != StatOutcome.Found)
        {
            if (perf) PhotoReviewPerf.Log.Stat(token, PhotoReviewPerf.Ms(perfStat));
            if (initialOutcome == StatOutcome.Missing)
            {
                await RemoveMissingCatalogItemAsync(path, index, token);
            }
            else
            {
                var initialStatError = initial.Error;
                // An unreadable file (share hiccup, access denied) is not a missing one: keep it in the catalog.
                AppLog.Error($"ShowImage stat failed token={token} index={index} path={path}", initialStatError!);
                CurrentPhotoInfo = null;
                // The status names this file, so the previous photo must not stay visible under it.
                UpdateCurrentImage(null);
                // Same for a visible comparison pair: it is not this file, and the catalog stays untouched.
                _compareViewModel.Clear();
                UpdateStatus(StatusFormatter.ImageError(Path.GetFileName(path), UserFacingError.Describe(initialStatError!)), needsAttention: true);
            }

            return;
        }

        // R7-1: the key must describe the file as it is now, not as the folder scan saw it. A photo edited in
        // another app since the scan kept the old Length/mtime in the catalog, so the viewer served the old
        // preview from RAM, or decoded and then failed MatchesCurrentSource forever; preload (which reads the
        // catalog) cached under keys the viewer never asked for. Refresh the entry from the stat just taken
        // (no extra I/O) and drop the old version's RAM entries (stale disk entries are keyed by length+mtime
        // and are never served; the disk LRU prunes them).
        var initialStat = initial.Stat!;
        var initialEntry = _catalog.Find(path);
        if (pathOverride is null && initialEntry is not null && !initialEntry.Matches(initialStat))
        {
            if (initialEntry.Length is not null && initialEntry.LastWriteUtc is not null)
                EvictCachedPath(path);
            _catalog.UpdateMetadata(path, initialStat.Length, initialStat.LastWriteUtc);
            initialEntry = _catalog.Find(path);
        }
        var initialSize = initialStat.Length;
        // Q-R29 option C: both keys come from the stat just taken (no second stat on the UI thread): an entry that
        // left the catalog meanwhile gets a key built from that stat, not from a fresh FileInfo.
        var currentKey = _previewService.GetCurrentCacheKey(pathOverride is not null
            ? new CatalogEntry(path).WithMetadata(initialStat.Length, initialStat.LastWriteUtc)
            : initialEntry ?? new CatalogEntry(path).WithMetadata(initialStat.Length, initialStat.LastWriteUtc));
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

        UpdateStatus(initialStatus, needsAttention: !ramReady);

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

                // Q-R29 option C: the thumbnail key reuses this navigation's stat (no second, UI-thread stat in BuildKey).
                var thumbnailTask = _thumbnailCache.GetAsync(path, initialStat);
                // Cast to the non-generic Task overload: thumbnailTask (IDecodedImage?) and
                // previewTask (IDecodedImage) have different nullability of the same reference
                // type, and Task.WhenAny<T> can't unify those without a nullability warning.
                var firstDone = await Task.WhenAny((Task)thumbnailTask, previewTask!);

                if (ReferenceEquals(firstDone, thumbnailTask))
                {
                    // The thumbnail is only a placeholder for the preview that is already decoding: whatever the
                    // thumbnail read/decode does wrong (a damaged cache file, an unexpected WIC/COM failure) must not fail
                    // an image whose preview is fine. Treat a fault as "no thumbnail" and carry on to step 5.
                    IDecodedImage? thumbnail;
                    try
                    {
                        thumbnail = await thumbnailTask;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        AppLog.Error($"ShowImage thumbnail failed token={token} path={path}", ex);
                        thumbnail = null;
                    }

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
                        UpdateCurrentImage(thumbnail.PlatformImage, thumbWidth, thumbHeight, path);
                        if (perf) _sink.TracePresented(token, "thumbnail", Stopwatch.GetTimestamp());

                        if (AppLog.Enabled) AppLog.Info($"ShowImage thumbnail-presented token={token} path={path}");

                        _sink.ApplyInitialViewMode();
                        UpdateStatus(StatusFormatter.LoadingFullRes(index, _catalog.Count, initialSize), needsAttention: true);
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
            // AR16: the background readability probe may have removed other files while this image decoded.
            // It keeps the current entry by path (as ReplaceOrder does), so re-read its index for preload/status.
            // Both sides are -1 when the entry itself left the catalog meanwhile: that is not "still current".
            var currentIndexNow = _catalog.CurrentIndex;
            if (currentIndexNow >= 0 && currentIndexNow == _catalog.IndexOf(path))
            {
                index = currentIndexNow;
            }

            long perfAssign = perf ? Stopwatch.GetTimestamp() : 0;
            var uiAssign = Stopwatch.StartNew();

            CurrentPhotoInfo = PhotoInfo.From(path, image);
            UpdateCurrentImage(image.PlatformImage, image.OriginalWidth, image.OriginalHeight, path);

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
            // While compare is open every presentation (Next/Previous/undo, not only the toggle) compares the capture pair,
            // otherwise navigation drops the pair yet leaves compare visible.
            var pair = GetComparePair(path, includeCaptureGroupInCompare || _compareViewModel.IsVisible);

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
                        p => LoadComparePreviewAsync(p, viewerDecodeToken),
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
                    UpdateCurrentImage(image.PlatformImage, image.OriginalWidth, image.OriginalHeight, path);
                    _sink.ApplyInitialViewMode();
                    // feat(zoom): match the normal (non-compare) branch below — arm the full-resolution
                    // decode target so zooming into this image after a failed compare works immediately.
                    _zoomDetail.OnPreviewPresented(token, path, currentKey, image);
                    UpdateStatus(StatusFormatter.ImageError(Path.GetFileName(partner), UserFacingError.Describe(ex)), needsAttention: true);
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

                (int Width, int Height)? original = settings.LoadingMode == LoadingMode.Original
                    ? (image.PixelWidth, image.PixelHeight)
                    : await TryGetOriginalDimensionsAsync(path, currentKey);

                if (perfDims != 0) PhotoReviewPerf.Log.PostEnd(token, "dims", PhotoReviewPerf.Ms(perfDims));

                if (!_clock.IsNavigationCurrent(token)) return;

                // Q-R29 option C: the post-present refresh stat runs off the UI thread too.
                var current = await StatOffUiThreadAsync(path, CancellationToken.None);
                if (!_clock.IsNavigationCurrent(token)) return;
                if (current.Outcome != StatOutcome.Found)
                {
                    // RV-A04: the file was decoded and is on screen, but the refresh stat failed (vanished or a share hiccup).
                    // Do not leave the status on "Loading" and skip the session save: report it ready with the size known
                    // before the stat. The photo stays in the catalog; the next present's initial stat drops a missing file
                    // or reports the error, so this one is not yanked from under the user.
                    UpdateStatus(original is { } failedSize
                        ? StatusFormatter.WithDimensions(index, _catalog.Count, initialSize, failedSize.Width, failedSize.Height, Path.GetFileName(path))
                        : StatusFormatter.Ready(index, _catalog.Count, initialSize, Path.GetFileName(path)));
                }
                else
                {
                    var currentInfo = current.Stat!;
                    // A capture-group member shown via pathOverride is a different file from the entry's representative:
                    // its stat must not overwrite the representative's Length/LastWriteUtc (that evicted the cache each toggle).
                    if (pathOverride is null && initialEntry is not null && (initialEntry.Length != currentInfo.Length || initialEntry.LastWriteUtc != currentInfo.LastWriteUtc))
                    {
                        _catalog.UpdateMetadata(path, currentInfo.Length, currentInfo.LastWriteUtc);
                    }

                    // Unknown dimensions (the lookup failed for a file that is shown fine): the status line just omits them.
                    UpdateStatus(original is { } size
                        ? StatusFormatter.WithDimensions(index, _catalog.Count, currentInfo.Length, size.Width, size.Height, Path.GetFileName(path))
                        : StatusFormatter.Ready(index, _catalog.Count, currentInfo.Length, Path.GetFileName(path)));
                }
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
            // A decode that is still in flight is shared by every request for the same key (path + length + mtime), so this
            // FileNotFound may belong to an EARLIER state of the path: the file was moved away while that decode opened it and
            // has come back since (Undo of the Move keeps length and mtime). Trust the file system, not the joined failure: a
            // file that is on disk again is presented once more (the failed shared decode is gone by now, so this starts a
            // fresh one) instead of being dropped from the catalog.
            {
                var recheck = await StatOffUiThreadAsync(path, CancellationToken.None);
                if (!_clock.IsNavigationCurrent(token)) return;
                if (recheck.Outcome == StatOutcome.Found && staleNotFoundRetries >= MaxStaleNotFoundRetries)
                {
                    // Under load the retry can join yet another failure from before the file came back. A file that is on disk is
                    // never dropped from the catalog: report it like an unreadable file and let the user present it again.
                    AppLog.Error($"ShowImage stale-file failures persisted while the file exists token={token} path={path}", ex);
                    CurrentPhotoInfo = null;
                    UpdateCurrentImage(null);
                    _compareViewModel.Clear();
                    UpdateStatus(StatusFormatter.ImageError(Path.GetFileName(path), UserFacingError.Describe(ex)), needsAttention: true);
                    return;
                }
                if (recheck.Outcome == StatOutcome.Found)
                {
                    if (AppLog.Enabled) AppLog.Info($"ShowImage stale-file failure ignored (file exists again) token={token} path={path}");
                    var retryIndex = _catalog.IndexOf(path);
                    if (retryIndex >= 0) await PresentCoreAsync(retryIndex, allowCompare, pathOverride, includeCaptureGroupInCompare, staleNotFoundRetries: staleNotFoundRetries + 1,
                        originalPreviousNavigationPath: previousNavigationPath);
                    return;
                }
            }

            if (AppLog.Enabled) AppLog.Info($"ShowImage stale-file token={token} path={path}");
            await RemoveMissingCatalogItemAsync(path, index, token);
        }
        catch (Exception ex) when (_clock.IsNavigationCurrent(token))
        {
            AppLog.Error($"ShowImage failed token={token} index={index} path={path}", ex);
            // A failed member switch (e.g. the RAW of a JPG+RAW capture) leaves the previous member on screen: keep the
            // presented path (badge, file actions) on what is really shown. A different entry keeps the new path.
            bool sameEntryAsShown = previousNavigationPath is not null
                && (string.Equals(previousNavigationPath, path, StringComparison.OrdinalIgnoreCase)
                    || _catalog.IndexOf(previousNavigationPath) == index);
            if (previousNavigationPath is not null && !string.Equals(previousNavigationPath, path, StringComparison.OrdinalIgnoreCase)
                && sameEntryAsShown)
            {
                _currentNavigationPath = previousNavigationPath;
            }
            CurrentPhotoInfo = null;
            if (!sameEntryAsShown)
            {
                // A different entry failed: the previous photo must not stay visible under this file's error status,
                // otherwise Delete/Move would act on a file the user never saw (same as the stat-error branch above).
                UpdateCurrentImage(null);
                _compareViewModel.Clear();
            }
            UpdateStatus(StatusFormatter.ImageError(Path.GetFileName(path), UserFacingError.Describe(ex)), needsAttention: true);
        }
        catch (Exception ex)
        {
            AppLog.Error($"ShowImage failed (stale token={token}, current={_clock.CurrentNavigation}) index={index} path={path}", ex);
        }
    }

    /// <summary>
    /// Removes a missing file from the catalog. A missing member of a JPEG+RAW capture group must not hide its
    /// surviving partner: the entry degrades to the other member as a standalone (group-less) entry at the same
    /// position and is selected. Returns the new current index (as <see cref="ReviewCatalog.Remove"/> does).
    /// </summary>
    private int RemoveOrDegrade(string path)
    {
        var index = _catalog.IndexOf(path);
        var group = index >= 0 ? _catalog.Find(path)?.CaptureGroup : null;
        var survivor = group?.ImagePaths.FirstOrDefault(p => !string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        if (survivor is null) return _catalog.Remove(path);

        _catalog.Remove(path);
        _catalog.Restore(survivor, index);
        _catalog.SetCurrent(index);
        return index;
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

            var nextIndex = RemoveOrDegrade(path);
            if (_catalog.Count == 0)
            {
                CurrentPhotoInfo = null;
                _infoWhileOriginalShown = null;
                _zoomDetail.Reset();
                UpdateCurrentImage(null);
                _compareViewModel.Clear();
                UpdateStatus(StatusFormatter.NoImagesRemaining(), needsAttention: true);
                return;
            }

            if (nextIndex < 0 || nextIndex >= _catalog.Count) return;

            var nextPath = _catalog.PathAt(nextIndex);
            // Only a file that is really gone is skipped; an unreadable one is presented so its error is reported.
            // Q-R29 option C: off the UI thread; the navigation token is re-checked before the catalog is touched.
            var next = await StatOffUiThreadAsync(nextPath, CancellationToken.None);
            if (!_clock.IsNavigationCurrent(token)) return;
            if (next.Outcome != StatOutcome.Missing)
            {
                await PresentAsync(nextIndex);
                return;
            }
            path = nextPath;
        }
    }

    /// <summary>True when <paramref name="path"/> belongs to a numbered compare pair (lets the compare key open compare while it is closed).</summary>
    public bool HasComparePair(string path) => GetComparePair(path) is not null;

    private (string Left, string Right)? GetComparePair(string path, bool includeCaptureGroup = true)
    {
        if (includeCaptureGroup && _catalog.Find(path)?.CaptureGroup is { } group)
        {
            return (group.JpegPath, group.RawPath);
        }

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

    /// <summary>
    /// feat/image-crossfade: called once when a new folder starts loading, so the folder's first image is never
    /// treated as a "file changed" transition (no outgoing image conceptually) even though a previous folder's
    /// bitmap may still be on screen at that moment.
    /// </summary>
    public void ResetFileIdentity() => _presentedFilePath = null;

    private void UpdateCurrentImage(object? image, int originalWidth = 0, int originalHeight = 0, string? path = null)
    {
        // Dimensions first: the sink's image-changed callback reads them to size the element
        // (feat(zoom): original-relative zoom), in the same UI pass as the new Source.
        CurrentOriginalWidth = image is null ? 0 : originalWidth;
        CurrentOriginalHeight = image is null ? 0 : originalHeight;
        CurrentImage = image;

        // feat/image-crossfade: true only for the FIRST bitmap of a navigation to a different file (whichever
        // stage wins the race: thumbnail or preview). A same-file upgrade within the same navigation (thumbnail
        // replaced by preview) sees _presentedFilePath already equal to path (set by the earlier call below) and
        // reports false, so it stays an instant, seamless replacement as today.
        var isFileChange = ImageTransitionDecision.ShouldTransition(_presentedFilePath, image is null ? null : path, _compareViewModel.IsVisible);
        _presentedFilePath = image is null ? null : path;
        _sink.SetCurrentImage(image, isFileChange);
    }

    /// <summary>
    /// Q-R34: true while the current status is a loading/error/no-images message that the info auto-hide must never
    /// fade out (a normal "index / size / name" line is false).
    /// </summary>
    public bool StatusNeedsAttention { get; private set; }

    private void UpdateStatus(string status, bool needsAttention = false)
    {
        StatusText = status;
        StatusNeedsAttention = needsAttention;
        _sink.SetStatusText(status);
    }

    /// <summary>
    /// Q-R29 option C: where the navigation path's stats run (<see cref="NavigationStatWorker.Shared"/>). A test seam:
    /// a test that blocks a stat gets its own worker so it never stalls presenters of other tests.
    /// </summary>
    internal NavigationStatWorker StatWorker { get; init; } = NavigationStatWorker.Shared;

    private enum StatOutcome { Found, Missing, Error }

    /// <summary>Result of one <see cref="StatNow"/>: <see cref="Stat"/> is set for Found, <see cref="Error"/> for Error.</summary>
    private readonly record struct StatResult(StatOutcome Outcome, FileStat? Stat, Exception? Error);

    /// <summary>
    /// The original dimensions for the status line, or null when the lookup fails (e.g. a RAW whose header sizes are all
    /// unknown): the image is already on screen, so a failed lookup must not turn into an error status for a viewable file.
    /// </summary>
    private async Task<(int Width, int Height)?> TryGetOriginalDimensionsAsync(string path, ImageCacheKey currentKey)
    {
        try
        {
            return await _previewService.GetOriginalDimensionsAsync(path, currentKey);
        }
        // The expected header-read failures only (I/O, unsupported/corrupt/undecodable file, busy decoder); a bug such as a
        // NullReferenceException must surface instead of being turned into "dimensions unknown".
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or FormatException
            or InvalidOperationException or InvalidDataException or System.Runtime.InteropServices.ExternalException
            or PhotoReview.Imaging.Decoding.DecoderBusyException)
        {
            if (AppLog.Enabled) AppLog.Info($"ShowImage original dimensions unknown path={path}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// A compare member's preview at viewer priority (the pair is on screen, so it must not fail-fast like a preload does). A
    /// <see cref="PhotoReview.Imaging.Decoding.DecoderBusyException"/> (possibly inherited from a preload decode this request
    /// joined) is retried once after a short, cancellable pause; a second one propagates.
    /// </summary>
    private async Task<object?> LoadComparePreviewAsync(string path, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var preview = await _previewService.GetViewerPreviewAsync(path, _previewService.GetCurrentCacheKey(path), cancellationToken);
                return preview.PlatformImage;
            }
            catch (PhotoReview.Imaging.Decoding.DecoderBusyException) when (attempt == 0)
            {
                await _busyRetryDelay(BusyRetryDelay, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Q-R29 option C: <see cref="StatNow"/> on <see cref="StatWorker"/>, never on the calling (UI)
    /// thread. Cancelled without touching the disk when <paramref name="cancellationToken"/> fires before the stat
    /// starts (the navigation was superseded while it waited). The caller resumes on its own context and must
    /// re-check its navigation token before using the result.
    /// </summary>
    private Task<StatResult> StatOffUiThreadAsync(string path, CancellationToken cancellationToken) =>
        StatWorker.RunAsync(() => StatNow(path), cancellationToken);

    /// <summary>
    /// One stat: existence plus the Length/LastWriteUtc the cache key is built from. Only "not there" is
    /// <see cref="StatOutcome.Missing"/> (the caller drops the item from the catalog); any other failure is
    /// <see cref="StatOutcome.Error"/> with the exception, so a flaky share never silently loses a photo.
    /// Blocking file-system I/O: called only through <see cref="StatOffUiThreadAsync"/>. Never throws.
    /// </summary>
    private StatResult StatNow(string path)
    {
        try
        {
            // When an IFileSystem is available, its (counted, mockable) stat is the source of truth.
            if (_fileSystem != null)
            {
                return _fileSystem.GetFileStat(path) is { } fsStat
                    ? new StatResult(StatOutcome.Found, fsStat, null)
                    : new StatResult(StatOutcome.Missing, null, null);
            }
            var info = new FileInfo(path);
            if (!info.Exists) return new StatResult(StatOutcome.Missing, null, null);
            return new StatResult(StatOutcome.Found, new FileStat(info.Length, info.LastWriteTimeUtc), null);
        }
        // A path that can never name a file (blank, illegal characters, too long, unsupported) is as good as missing.
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new StatResult(StatOutcome.Missing, null, null);
        }
        catch (Exception ex)
        {
            return new StatResult(StatOutcome.Error, null, ex);
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
/// <param name="RawPreviewWidth">Q-RAW-03: pixel width of the embedded RAW JPEG preview while THAT is the displayed image; 0 = none
/// (non-RAW, a cache-restored image that cannot tell, or the full decode is shown).</param>
/// <param name="RawPreviewHeight">See <paramref name="RawPreviewWidth"/>.</param>
public sealed record PhotoInfo(string FileName, int Width, int Height, PhotoReview.Imaging.Metadata.ExifSummary? Exif,
    int RawPreviewWidth = 0, int RawPreviewHeight = 0)
{
    public static PhotoInfo From(string path, PhotoReview.Imaging.Decoding.IDecodedImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var preview = image as PhotoReview.Imaging.Decoding.IRawPreviewInfo;
        return new PhotoInfo(Path.GetFileName(path), image.OriginalWidth, image.OriginalHeight, image.Exif,
            preview?.EmbeddedPreviewWidth ?? 0, preview?.EmbeddedPreviewHeight ?? 0);
    }
}

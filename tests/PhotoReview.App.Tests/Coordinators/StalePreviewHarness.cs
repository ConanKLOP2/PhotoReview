using System.Collections.Concurrent;
using System.IO;
using PhotoReview.App.Coordinators;
using PhotoReview.App.ViewModels;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.IO;
using PhotoReview.Core.Model;
using PhotoReview.Core.Settings;
using PhotoReview.Imaging;
using PhotoReview.Imaging.Caching;
using PhotoReview.Imaging.Decoding;
using Xunit;

namespace PhotoReview.App.Tests.Coordinators;

/// <summary>Which stage of a file's presentation a bitmap came from.</summary>
internal enum ShownStage
{
    Thumbnail,
    Preview,
    Original,
}

/// <summary>
/// The platform image a fake decode hands to the presenter: it tells the test which FILE and which STAGE a bitmap on screen came
/// from, so "the image on screen belongs to the current navigation" is a plain comparison.
/// </summary>
internal sealed class ShownImage(string path, ShownStage stage, int serial)
{
    public string Path { get; } = path;

    public ShownStage Stage { get; } = stage;

    public int Serial { get; } = serial;

    public override string ToString() => $"{System.IO.Path.GetFileName(Path)}#{Stage}{Serial}";
}

/// <summary>Decoded image of one file/stage; the size is a function of the file index so a swapped bitmap changes the size.</summary>
internal sealed class StaleFakeImage : IDecodedImage
{
    public StaleFakeImage(ShownImage shown, int width, int height, bool downscaled, int originalWidth, int originalHeight)
    {
        PlatformImage = shown;
        PixelWidth = width;
        PixelHeight = height;
        Downscaled = downscaled;
        OriginalWidth = originalWidth;
        OriginalHeight = originalHeight;
    }

    public int PixelWidth { get; }

    public int PixelHeight { get; }

    public bool Downscaled { get; }

    public int Orientation => 1;

    public long EstimatedBytes => 1;

    public object PlatformImage { get; }

    public int OriginalWidth { get; }

    public int OriginalHeight { get; }

    public ShownImage Shown => (ShownImage)PlatformImage;
}

internal enum UiEventKind
{
    Image,
    Status,
    InitialViewMode,
    Presented,
}

/// <summary>One call the presenter made on the sink, in the order the (single) UI thread saw it.</summary>
/// <param name="NavToken">The generation clock's current navigation at the moment of the call.</param>
internal sealed record UiEvent(int Seq, UiEventKind Kind, long NavToken, ShownImage? Image, bool IsFileChange, string? Text);

/// <summary>A navigation the test asked for.</summary>
internal sealed record StaleNav(int Ordinal, int Index, string Path, long Token, Task Task);

/// <summary>
/// A single thread with a message queue and a <see cref="SynchronizationContext"/>: the stand-in for the UI thread. Every
/// continuation the presenter awaits resumes here, one at a time, exactly like on the real UI thread, so "checked the token,
/// then touched the screen" is atomic and the invariants below are exact rather than statistical.
/// </summary>
internal sealed class FakeUiThread : IDisposable
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
    private readonly Thread _thread;

    public FakeUiThread()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "stale-preview-ui" };
        _thread.Start();
    }

    public bool IsUiThread => Thread.CurrentThread == _thread;

    private void Run()
    {
        SynchronizationContext.SetSynchronizationContext(new Context(this));
        foreach (var (callback, state) in _queue.GetConsumingEnumerable())
        {
            callback(state);
        }
    }

    public Task<T> InvokeAsync<T>(Func<T> func)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add((_ =>
        {
            try
            {
                done.SetResult(func());
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        }, null));
        return done.Task;
    }

    public void Dispose()
    {
        // Not joined: on a loaded machine the thread takes a while to be scheduled again, and nothing waits for it. It is a
        // background thread that ends as soon as it drains what is left in its queue.
        _queue.CompleteAdding();
    }

    private sealed class Context(FakeUiThread owner) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
        {
            try
            {
                owner._queue.Add((d, state));
            }
            catch (InvalidOperationException)
            {
                // The harness was disposed: a continuation that arrives after that is dropped, like a closed window.
            }
        }

        public override void Send(SendOrPostCallback d, object? state)
        {
            if (!owner.IsUiThread) throw new NotSupportedException("Send from another thread is not used by the presenter.");
            d(state);
        }

        public override SynchronizationContext CreateCopy() => this;
    }
}

/// <summary>
/// WP "stale preview": the deterministic bed for "an OLD preview overwrites the NEW one". Everything that can finish late is a
/// gate the test opens: the preview decode of each file, its embedded thumbnail, its full-resolution original, and a decode
/// failure. The presenter runs on <see cref="FakeUiThread"/> against the real <see cref="PreviewImageService"/> /
/// <see cref="ThumbnailCache"/> / <see cref="GenerationClock"/>; only the decoder and the thumbnail reader are fakes. No
/// delay, no sleep: a step returns when the presenter has really reacted (its navigation task ended, or the thumbnail was shown).
/// </summary>
internal sealed class StalePreviewHarness : IDisposable
{
    public static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    private readonly string _dir;
    private readonly bool _ownsDir;
    private readonly object _gate = new();
    private readonly List<UiEvent> _events = [];
    private readonly List<StaleNav> _navs = [];
    private readonly Dictionary<string, FileControl> _controls = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentBag<string> _decodeTimeouts = [];
    private readonly bool _thumbnails;
    private int _serial;

    /// <param name="sharedDir">A folder that already holds the files (several scenarios of one test share it: creating and deleting
    /// a folder per scenario would dominate the run); null = a private folder, deleted on dispose.</param>
    public StalePreviewHarness(IReadOnlyList<string> fileNames, bool thumbnails = false, bool compareSize = false, string? sharedDir = null)
    {
        _thumbnails = thumbnails;
        _ownsDir = sharedDir is null;
        _dir = sharedDir ?? NewFolder();
        Directory.CreateDirectory(_dir);
        var paths = new string[fileNames.Count];
        for (var i = 0; i < fileNames.Count; i++)
        {
            paths[i] = Path.Combine(_dir, fileNames[i]);
            if (!File.Exists(paths[i])) File.WriteAllBytes(paths[i], new byte[64 + (i * 7)]); // distinct lengths: distinct cache keys and status text
            _controls[paths[i]] = new FileControl();
        }

        Paths = paths;
        Ui = new FakeUiThread();
        Catalog = new ReviewCatalog();
        Catalog.Reset(paths);
        Clock = new GenerationClock();
        Metrics = new ReviewMetrics();
        Compare = new CompareViewModel();
        Sink = new RecordingSink(this);
        Decoder = new GatedDecoder(this);
        Service = new PreviewImageService(
            Metrics,
            () => false,
            (Func<DecodeBox>)(() => new DecodeBox(1920, 0)),
            WpfBitmapSourceCodec.Instance,
            capacityBytes: 64 * 1024 * 1024,
            decoder: Decoder,
            currentBackend: () => DecoderBackend.Wpf,
            disableDiskCacheOverride: true);
        Thumbnails = new ThumbnailCache(WpfBitmapSourceCodec.Instance,
            diskDirectory: Path.Combine(_dir, "thumbs-" + Guid.NewGuid().ToString("N")),
            maxRamBytes: 16 * 1024 * 1024,
            embeddedThumbnailReader: ReadThumbnailAsync);
        Settings = new AppSettings { LoadingMode = LoadingMode.Preview, CompareSizeEnabled = compareSize, CompareHashEnabled = false };
        Preload = new StalePreloadController();
        StatGate = new StatGateFileSystem(new PhysicalFileSystem());
        Presenter = new ImagePresenter(
            Catalog, Clock, Service, Thumbnails, Preload, Compare, new FileHashService(), Metrics, () => Settings,
            sessionStore: null, Sink, fileSystem: StatGate, getSession: () => null)
        {
            StatWorker = new NavigationStatWorker(),
        };
    }

    /// <summary>The file system the presenter stats through: the SECOND stat of a held file (the refresh after the image was presented) can be paused.</summary>
    public StatGateFileSystem StatGate { get; }

    public IReadOnlyList<string> Paths { get; }

    public FakeUiThread Ui { get; }

    public ReviewCatalog Catalog { get; }

    public GenerationClock Clock { get; }

    public ReviewMetrics Metrics { get; }

    public CompareViewModel Compare { get; }

    public AppSettings Settings { get; }

    public PreviewImageService Service { get; }

    public ThumbnailCache Thumbnails { get; }

    public ImagePresenter Presenter { get; }

    public StalePreloadController Preload { get; }

    private RecordingSink Sink { get; }

    private GatedDecoder Decoder { get; }

    /// <summary>Paths whose decode gate was still closed when the decode gave up waiting (a step the test forgot).</summary>
    public IReadOnlyCollection<string> DecodeTimeouts => _decodeTimeouts;

    public IReadOnlyList<UiEvent> Events
    {
        get
        {
            lock (_gate) return [.. _events];
        }
    }

    public IReadOnlyList<StaleNav> Navs
    {
        get
        {
            lock (_gate) return [.. _navs];
        }
    }

    public static (int Width, int Height) OriginalSize(int index) => (400 + (index * 10), 300 + index);

    public long FileLength(int index) => new FileInfo(Paths[index]).Length;

    public void Dispose()
    {
        foreach (var control in _controls.Values) control.OpenEverything();
        Ui.Dispose();
        Thumbnails.Dispose();
        if (_ownsDir) DeleteFolder(_dir);
    }

    /// <summary>A new private folder path for files shared by the scenarios of one test (delete it with <see cref="DeleteFolder"/>).</summary>
    public static string NewFolder() => Path.Combine(Path.GetTempPath(), "PhotoReview_StalePreview_" + Guid.NewGuid().ToString("N"));

    public static void DeleteFolder(string dir)
    {
        try
        {
            Directory.Delete(dir, true);
        }
        catch (IOException)
        {
        }
    }

    // ---- steps ---------------------------------------------------------------------------------

    /// <summary>Starts a navigation on the UI thread and returns its ordinal. The presenter's task is NOT awaited.</summary>
    public Task<int> NavigateAsync(int index, bool allowCompare = true) => Ui.InvokeAsync(() =>
    {
        var task = Presenter.PresentAsync(index, allowCompare);
        var token = Clock.CurrentNavigation;
        lock (_gate)
        {
            var nav = new StaleNav(_navs.Count, index, Paths[index], token, task);
            _navs.Add(nav);
            return nav.Ordinal;
        }
    });

    /// <summary>Runs an action on the UI thread (e.g. a zoom change) and waits for it.</summary>
    public Task OnUiAsync(Action action) => Ui.InvokeAsync(() =>
    {
        action();
        return 0;
    });

    /// <summary>
    /// Lets the file's preview decode finish and waits until every navigation to that file reacted (presented the image, or
    /// noticed that it had been superseded). Only valid while no navigation to the file waits for something else (compare).
    /// </summary>
    public async Task ReleasePreviewAsync(int index)
    {
        var control = _controls[Paths[index]];
        control.Decode.TrySetResult();
        // Both viewer slots are held by superseded decodes that are still inside the decoder (that is the designed bound, see
        // PreviewImageService.ViewerDecodeSlots): this file's decode cannot even start yet, so it reacts only when one of them
        // is released. Its gate is already open, so it then runs straight through.
        if (!control.PreviewStarted.Task.IsCompleted && BlockedOtherDecodes(control) >= PreviewImageService.ViewerDecodeSlots)
        {
            return;
        }

        await WhenAllBounded(NavsOf(index).Select(n => n.Task)).ConfigureAwait(false);
    }

    /// <summary>The preview decode of <paramref name="index"/> throws <paramref name="error"/> instead of returning (then opens).</summary>
    public void FailPreview(int index, Exception error) => _controls[Paths[index]].PreviewFailure = error;

    /// <summary>Opens the gate without waiting for a reaction (the caller waits for its own observable).</summary>
    public void OpenPreview(int index) => _controls[Paths[index]].Decode.TrySetResult();

    /// <summary>Completes when the decoder has been entered for the file's preview (the decode is really in flight).</summary>
    public Task PreviewStartedAsync(int index) => _controls[Paths[index]].PreviewStarted.Task.WaitAsync(Bound);

    /// <summary>How many preview decodes of the file the decoder has been asked for.</summary>
    public int PreviewDecodeCalls(int index) => Volatile.Read(ref _controls[Paths[index]].PreviewCalls);

    /// <summary>
    /// Delivers the file's embedded thumbnail and waits for the reaction: a navigation that is still current shows it, a
    /// superseded one just ends. A thumbnail released twice is a no-op.
    /// </summary>
    public async Task ReleaseThumbnailAsync(int index)
    {
        var control = _controls[Paths[index]];
        if (!control.Thumbnail.TrySetResult(new StaleFakeImage(NewShown(Paths[index], ShownStage.Thumbnail), 40, 30, true,
                OriginalSize(index).Width, OriginalSize(index).Height)))
        {
            return;
        }

        foreach (var nav in NavsOf(index))
        {
            if (nav.Task.IsCompleted) continue;
            if (nav.Token != Clock.CurrentNavigation)
            {
                await nav.Task.WaitAsync(Bound).ConfigureAwait(false); // superseded: ends on the first thing it wakes up for
            }
            else
            {
                await Task.WhenAny(control.ThumbnailShown.Task, nav.Task).WaitAsync(Bound).ConfigureAwait(false);
                await Ui.InvokeAsync(() => 0).WaitAsync(Bound).ConfigureAwait(false); // the shown-thumbnail step ran to its end
            }
        }
    }

    /// <summary>Completes when the embedded-thumbnail reader has been asked for the file.</summary>
    public Task ThumbnailRequestedAsync(int index) => _controls[Paths[index]].ThumbnailRequested.Task.WaitAsync(Bound);

    /// <summary>
    /// The file's preview carries no source size (as a decoder that cannot tell), so the presenter has to read the header
    /// afterwards; that read (<see cref="ReadInfoStartedAsync"/>) waits for <see cref="OpenReadInfo"/>.
    /// </summary>
    public void HoldHeaderRead(int index) => _controls[Paths[index]].HoldReadInfo = true;

    public Task ReadInfoStartedAsync(int index) => _controls[Paths[index]].ReadInfoStarted.Task.WaitAsync(Bound);

    public void OpenReadInfo(int index) => _controls[Paths[index]].ReadInfo.TrySetResult();

    /// <summary>Opens the full-resolution decode of the file (a zoom-detail original).</summary>
    public void OpenOriginal(int index) => _controls[Paths[index]].Original.TrySetResult();

    public Task OriginalStartedAsync(int index) => _controls[Paths[index]].OriginalStarted.Task.WaitAsync(Bound);

    /// <summary>Opens everything still closed and waits until every navigation task ended.</summary>
    public async Task SettleAsync()
    {
        foreach (var index in Enumerable.Range(0, Paths.Count))
        {
            var control = _controls[Paths[index]];
            control.Decode.TrySetResult();
            control.Thumbnail.TrySetResult(new StaleFakeImage(NewShown(Paths[index], ShownStage.Thumbnail), 40, 30, true,
                OriginalSize(index).Width, OriginalSize(index).Height));
        }

        await WhenAllBounded(Navs.Select(n => n.Task)).ConfigureAwait(false);
        await Ui.InvokeAsync(() => 0).WaitAsync(Bound).ConfigureAwait(false);
    }

    private IReadOnlyList<StaleNav> NavsOf(int index)
    {
        lock (_gate) return [.. _navs.Where(n => n.Index == index)];
    }

    /// <summary>Decodes of OTHER files that are inside the decoder and still wait for their (closed) gate: each holds a viewer slot for good.</summary>
    private int BlockedOtherDecodes(FileControl self) =>
        _controls.Values.Count(c => !ReferenceEquals(c, self) && c.PreviewStarted.Task.IsCompleted
            && !c.Decode.Task.IsCompleted && Volatile.Read(ref c.PreviewReturned) == 0);

    private static Task WhenAllBounded(IEnumerable<Task> tasks) => Task.WhenAll(tasks).WaitAsync(Bound);

    // ---- what the user sees --------------------------------------------------------------------

    /// <summary>The navigation whose token was the clock's current one when event <paramref name="e"/> happened.</summary>
    public StaleNav NavOf(UiEvent e)
    {
        lock (_gate)
        {
            // The newest navigation with that token (a token is never reused; ordinals follow the order of calls).
            return _navs.Last(n => n.Token == e.NavToken);
        }
    }

    /// <summary>The status strings the presenter can emit for file <paramref name="index"/>, whatever the stage.</summary>
    public IReadOnlyList<string> StatusTextsOf(int index)
    {
        var (w, h) = OriginalSize(index);
        var name = Path.GetFileName(Paths[index]);
        var size = FileLength(index);
        var count = Paths.Count;
        return
        [
            StatusFormatter.Loading(index, count, size),
            StatusFormatter.LoadingFullRes(index, count, size),
            StatusFormatter.Ready(index, count, size, name),
            StatusFormatter.WithDimensions(index, count, size, w, h, name),
        ];
    }

    /// <summary>
    /// INV-1 as a property of the whole event log: every call on the sink (image, status, initial view mode, presented) belongs
    /// to the navigation that was current when it happened, and the navigation each call belongs to never goes backwards.
    /// Returns the sequence of navigation ordinals for the caller's own messages.
    /// </summary>
    public void AssertNoStaleOverwrite(string scenario)
    {
        var events = Events;
        var lastOrdinal = -1;
        var lastImageNav = -1;
        foreach (var e in events)
        {
            var nav = NavOf(e);
            var where = $"{scenario}: event #{e.Seq} {e.Kind} (nav {nav.Ordinal} -> file {nav.Index}) after nav {lastOrdinal}";
            Assert.True(nav.Ordinal >= lastOrdinal, "an event of an OLDER navigation came after a newer one: " + where);
            lastOrdinal = nav.Ordinal;
            switch (e.Kind)
            {
                case UiEventKind.Image when e.Image is { } shown:
                    Assert.True(string.Equals(shown.Path, nav.Path, StringComparison.OrdinalIgnoreCase),
                        $"bitmap {shown} of another file is on screen while navigating to {Path.GetFileName(nav.Path)}: " + where);
                    lastImageNav = nav.Ordinal;
                    break;
                case UiEventKind.Status:
                    // The plain texts of the file, or a compare line (which names both files of the pair).
                    Assert.True(StatusTextsOf(nav.Index).Contains(e.Text!)
                        || e.Text!.Contains(Path.GetFileName(nav.Path), StringComparison.Ordinal),
                        $"status '{e.Text}' does not describe file {Path.GetFileName(nav.Path)}: " + where);
                    break;
                case UiEventKind.Presented:
                    Assert.True(string.Equals(e.Text, nav.Path, StringComparison.OrdinalIgnoreCase),
                        $"OnPresented({Path.GetFileName(e.Text)}) of another file: " + where);
                    break;
                case UiEventKind.InitialViewMode:
                    Assert.True(lastImageNav == nav.Ordinal,
                        "ApplyInitialViewMode while the bitmap on screen is not this navigation's (it would reset the zoom of another image): " + where);
                    break;
            }
        }

        Assert.Empty(DecodeTimeouts);
    }

    /// <summary>Asserts the settled state: the last requested file is what is shown, and every piece of state says so.</summary>
    public async Task AssertSettledOnAsync(int index, ShownStage stage = ShownStage.Preview)
    {
        var state = await Ui.InvokeAsync(() => (
            Image: Presenter.CurrentImage as ShownImage,
            Width: Presenter.CurrentOriginalWidth,
            Height: Presenter.CurrentOriginalHeight,
            Info: Presenter.CurrentPhotoInfo?.FileName,
            Presented: Presenter.CurrentPresentedPath,
            Status: Presenter.StatusText,
            CatalogIndex: Catalog.CurrentIndex)).WaitAsync(Bound).ConfigureAwait(false);
        var (w, h) = OriginalSize(index);
        Assert.True(state.Image is not null, "nothing is shown; events:\n" + Dump());
        Assert.Equal(Paths[index], state.Image!.Path, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(stage, state.Image.Stage);
        Assert.Equal((w, h), (state.Width, state.Height));
        Assert.Equal(Path.GetFileName(Paths[index]), state.Info);
        Assert.Equal(Paths[index], state.Presented, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(index, state.CatalogIndex);
        Assert.Contains(state.Status, StatusTextsOf(index));
        var lastImage = Events.LastOrDefault(e => e is { Kind: UiEventKind.Image, Image: not null });
        Assert.NotNull(lastImage);
        Assert.Same(state.Image, lastImage!.Image);
    }

    /// <summary>The event log and navigations as text (for assertion messages).</summary>
    public string Dump() => string.Join('\n',
        Navs.Select(n => $"nav {n.Ordinal}: file {n.Index} token {n.Token} {n.Task.Status}")
            .Concat(Events.Select(e => $"  #{e.Seq} token {e.NavToken} {e.Kind} {e.Image} {e.Text}")));

    // ---- internals -----------------------------------------------------------------------------

    private ShownImage NewShown(string path, ShownStage stage) => new(path, stage, Interlocked.Increment(ref _serial));

    private int IndexOf(string path) => Paths.ToList().FindIndex(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));

    private void Record(UiEventKind kind, ShownImage? image = null, bool isFileChange = false, string? text = null)
    {
        lock (_gate) _events.Add(new UiEvent(_events.Count, kind, Clock.CurrentNavigation, image, isFileChange, text));
    }

    private async Task<IDecodedImage?> ReadThumbnailAsync(string path, CancellationToken cancellationToken)
    {
        var control = _controls[path];
        control.ThumbnailRequested.TrySetResult();
        if (!_thumbnails) return null;
        return await control.Thumbnail.Task.ConfigureAwait(false);
    }

    private sealed class FileControl
    {
        public TaskCompletionSource Decode { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource PreviewStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Original { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReadInfo { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReadInfoStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool HoldReadInfo { get; set; }

        public TaskCompletionSource OriginalStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<IDecodedImage?> Thumbnail { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ThumbnailRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ThumbnailShown { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Exception? PreviewFailure { get; set; }

        public int PreviewCalls;

        public int PreviewReturned;

        public void OpenEverything()
        {
            Decode.TrySetResult();
            Original.TrySetResult();
            ReadInfo.TrySetResult();
            Thumbnail.TrySetResult(null);
        }
    }

    private sealed class GatedDecoder(StalePreviewHarness owner) : IImageDecoder
    {
        public IDecodedImage Decode(DecodeRequest request)
        {
            var control = owner._controls[request.Path];
            var index = owner.IndexOf(request.Path);
            var (w, h) = OriginalSize(index);
            if (!request.IsDownscaleRequested)
            {
                control.OriginalStarted.TrySetResult();
                Await(control.Original, request.Path);
                return new StaleFakeImage(owner.NewShown(request.Path, ShownStage.Original), w, h, false, w, h);
            }

            Interlocked.Increment(ref control.PreviewCalls);
            Volatile.Write(ref control.PreviewReturned, 0);
            control.PreviewStarted.TrySetResult();
            try
            {
                Await(control.Decode, request.Path);
            }
            finally
            {
                Volatile.Write(ref control.PreviewReturned, 1);
            }

            if (control.PreviewFailure is { } failure) throw failure;
            // A real preview is smaller than the source: 1/4 of each side, so a zoom-detail original has pixels to add.
            // Unless the test held the header read: then the preview does not know the source size (original = its own size).
            return control.HoldReadInfo
                ? new StaleFakeImage(owner.NewShown(request.Path, ShownStage.Preview), w / 4, h / 4, true, w / 4, h / 4)
                : new StaleFakeImage(owner.NewShown(request.Path, ShownStage.Preview), w / 4, h / 4, true, w, h);
        }

        public ImageInfo ReadInfo(string path)
        {
            var control = owner._controls[path];
            if (control.HoldReadInfo)
            {
                control.ReadInfoStarted.TrySetResult();
                Await(control.ReadInfo, path);
            }

            var (w, h) = OriginalSize(owner.IndexOf(path));
            return new ImageInfo(w, h);
        }

        private void Await(TaskCompletionSource gate, string path)
        {
            // A gate nobody opened must fail the decode instead of returning as if it had been released.
            if (gate.Task.Wait(Bound)) return;
            owner._decodeTimeouts.Add(path);
            throw new TimeoutException($"decode gate for {path} was never opened");
        }
    }

    private sealed class RecordingSink(StalePreviewHarness owner) : IPresentationSink
    {
        public void SetCurrentImage(object? image, bool isFileChange = false)
        {
            var shown = image as ShownImage;
            owner.Record(UiEventKind.Image, shown, isFileChange);
            if (shown is { Stage: ShownStage.Thumbnail }) owner._controls[shown.Path].ThumbnailShown.TrySetResult();
        }

        public void SetStatusText(string status) => owner.Record(UiEventKind.Status, text: status);

        public void ApplyInitialViewMode() => owner.Record(UiEventKind.InitialViewMode);

        public void OnPresented(string path) => owner.Record(UiEventKind.Presented, text: path);

        public void TracePresented(long token, string kind, long assignedTimestamp)
        {
        }
    }
}

/// <summary>Preload controller that preloads nothing and records what the presenter told it.</summary>
internal sealed class StalePreloadController : IPreloadController
{
    private readonly object _gate = new();
    private readonly List<int> _preloadAround = [];

    public IReadOnlyList<int> PreloadAroundCalls
    {
        get
        {
            lock (_gate) return [.. _preloadAround];
        }
    }

    public void NotifyNavigation(int index)
    {
    }

    public TimeSpan GetViewerDecodeDelay() => TimeSpan.Zero;

    public Task PreloadAroundAsync(int center)
    {
        lock (_gate) _preloadAround.Add(center);
        return Task.CompletedTask;
    }

    public bool TryConsumePreloadedKey(ImageCacheKey key) => false;

    public void Cancel()
    {
    }

    public void RemovePreloadedKeysForPath(string normalizedPath)
    {
    }

    public void ClearPreloadedKeys()
    {
    }
}

/// <summary>Real file system whose second stat of a held path (the post-present refresh) waits for the test.</summary>
internal sealed class StatGateFileSystem(IFileSystem inner) : IFileSystem
{
    private readonly object _lock = new();
    private readonly Dictionary<string, int> _calls = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (TaskCompletionSource Started, TaskCompletionSource Release)> _held = new(StringComparer.OrdinalIgnoreCase);

    public void HoldSecondStat(string path)
    {
        lock (_lock)
        {
            _held[path] = (new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
                new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        }
    }

    public Task SecondStatStarted(string path)
    {
        lock (_lock) return _held[path].Started.Task;
    }

    public void ReleaseSecondStat(string path)
    {
        lock (_lock)
        {
            if (_held.TryGetValue(path, out var gate)) gate.Release.TrySetResult();
        }
    }

    /// <summary>How many times <paramref name="path"/> has been stat-ed through this file system.</summary>
    public int StatCount(string path)
    {
        lock (_lock) return _calls.GetValueOrDefault(path);
    }

    public FileStat? GetFileStat(string path)
    {
        (TaskCompletionSource Started, TaskCompletionSource Release)? gate = null;
        lock (_lock)
        {
            _calls[path] = _calls.GetValueOrDefault(path) + 1;
            if (_calls[path] == 2 && _held.TryGetValue(path, out var held)) gate = held;
        }

        if (gate is { } g)
        {
            g.Started.TrySetResult();
            if (!g.Release.Task.Wait(StalePreviewHarness.Bound)) throw new TimeoutException("stat gate for " + path + " was never released");
        }

        return inner.GetFileStat(path);
    }

    public bool FileExists(string path) => inner.FileExists(path);
    public bool DirectoryExists(string path) => inner.DirectoryExists(path);
    public void Move(string source, string destination) => inner.Move(source, destination);
    public void Copy(string source, string destination) => inner.Copy(source, destination);
    public void Delete(string path) => inner.Delete(path);
    public Stream OpenReadShared(string path, int bufferSize = 65536) => inner.OpenReadShared(path, bufferSize);
    public Stream OpenAppend(string path, bool durable) => inner.OpenAppend(path, durable);
    public void WriteAllTextAtomic(string path, string text, bool durable = true) => inner.WriteAllTextAtomic(path, text, durable);
    public string ReadAllText(string path) => inner.ReadAllText(path);
    public IEnumerable<string> EnumerateFiles(string directory, string pattern = "*") => inner.EnumerateFiles(directory, pattern);
    public IEnumerable<(string Path, FileStat? Stat)> EnumerateFilesWithStat(string directory, Func<string, bool> include, Action<SkippedEntry> onSkipped) =>
        inner.EnumerateFilesWithStat(directory, include, onSkipped);
    public bool TryProbeReadable(string path, out string? failure) => inner.TryProbeReadable(path, out failure);
    public IEnumerable<(string Path, FileStat? Stat)> EnumerateReadableFilesWithStat(string directory, Func<string, bool> include, Action<SkippedEntry> onSkipped) =>
        inner.EnumerateReadableFilesWithStat(directory, include, onSkipped);
    public IEnumerable<string> EnumerateDirectories(string directory) => inner.EnumerateDirectories(directory);
    public void CreateDirectory(string path) => inner.CreateDirectory(path);
}
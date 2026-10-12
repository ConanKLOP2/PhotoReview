using PhotoReview.App.Input;
using PhotoReview.Core.Model;
using PhotoReview.Imaging.Pixels;
using PhotoReview.Shell.Rendering;
using PhotoReview.Shell.Tests.Overlay;
using PhotoReview.Shell.Tests.Rendering;
using PhotoReview.Shell.Win32.Viewing;

namespace PhotoReview.Shell.Tests.Viewing;

/// <summary>
/// "An old frame must never win over the new image" for the Win32 presentation layer (composer + GPU cache + prefetcher),
/// with the scheduler under the test's control: upload stripes (<see cref="ManualDispatcher"/>), the upgrade deadline (a list of
/// captured actions), the clock (a field) and the device (a fake backend that can be "lost"). The composer test is a bounded
/// exhaustive search over short operation sequences, checking the invariants after EVERY step, not only at the end.
/// </summary>
[Trait("Category", "HotPath")]
public sealed class PresentationStaleOrderTests
{
    private static readonly SizeD Client = new(1000, 700);
    private static readonly RectD Box = new(100, 50, 800, 600);

    /// <summary>One composer over a fake GPU, with the file/quality of every buffer it was given.</summary>
    private sealed class Rig : IDisposable
    {
        private readonly FakeGpuBackend _backend = new();
        private readonly ManualDispatcher _dispatcher = new();
        private readonly FakeFrameClock _clock = new();
        private readonly List<(TimeSpan Delay, Action Action)> _scheduled = [];
        private readonly Dictionary<long, (int File, int Quality)> _origin = [];
        private readonly List<PixelBuffer> _buffers = [];
        private readonly GpuImageCache _cache;
        private long _now = 1_000_000;
        private int _file = -1;
        private int _quality;
        private int _firedDeadlines;
        private int _lastDrawnFile = -1;
        private readonly Dictionary<int, int> _bestQualityShown = [];

        public Rig()
        {
            // 8x8 pixels, 32-byte stride, 64-byte stripes: two rows per background job, four jobs per bitmap.
            _cache = new GpuImageCache(_backend, _dispatcher, 1_000_000_000, 64);
            Composer = new PresentationComposer(_cache, _dispatcher, _clock, () => { }, () => _now, 1000, 120,
                (delay, action) => _scheduled.Add((delay, action)));
        }

        public PresentationComposer Composer { get; }

        public GpuImageCache Cache => _cache;

        public FakeGpuBackend Backend => _backend;

        public ManualDispatcher Dispatcher => _dispatcher;

        public int File => _file;

        public int PendingDeadlines => _scheduled.Count - _firedDeadlines;

        public PixelBuffer NewBuffer(int file, int quality)
        {
            var buffer = ViewingImages.Solid(8, 8, (byte)(file * 16), (byte)quality, 1);
            _buffers.Add(buffer);
            _origin[buffer.Id] = (file, quality);
            return buffer;
        }

        public void Apply(int op)
        {
            switch (op)
            {
                case 0: // the first bitmap of another file (a file change: replaces at once)
                    _file++;
                    _quality = 0;
                    Composer.SetIncoming(NewBuffer(_file, 0), isFileChange: true, deferUploadBytes: 1);
                    break;
                case 1: // a better bitmap of the file on screen (preview -> full)
                    if (_file < 0) break;
                    Composer.SetIncoming(NewBuffer(_file, ++_quality), isFileChange: false, deferUploadBytes: 1);
                    break;
                case 2: // one background upload stripe
                    _dispatcher.RunOne();
                    break;
                case 3: // the newest upgrade deadline fires, long after it was armed
                    _now += 2000;
                    if (_scheduled.Count > 0) _scheduled[^1].Action();
                    _firedDeadlines++;
                    break;
                case 4: // the OLDEST deadline fires late (it must not drag a newer state)
                    _now += 2000;
                    if (_scheduled.Count > 0) _scheduled[0].Action();
                    _firedDeadlines++;
                    break;
                case 5: // a frame is drawn
                    Draw();
                    break;
                default: // a frame is drawn and the crossfade freezes it, as ImageChanging does before a file change
                    Draw();
                    Composer.BeginCrossfade(TimeSpan.FromMilliseconds(100));
                    break;
            }
        }

        private void Draw()
        {
            var context = new RecordingImageContext();
            var frame = Composer.Draw(context, new PresentationInputs(Client, Box, ScalingQuality.HighQuality,
                ScalingQuality.HighQuality, 1.0, false, default));
            if (!frame.MainDrawn) return;
            var main = context.Images[0].BufferId;
            var (file, quality) = _origin[main];
            Assert.True(file >= _lastDrawnFile, $"frame drew a bitmap of file {file} after one of file {_lastDrawnFile}");
            _lastDrawnFile = file;
            Assert.Equal(_file, file); // what is drawn as THE image is always the latest file's
            _bestQualityShown.TryGetValue(file, out var best);
            Assert.True(quality >= best, $"frame drew quality {quality} of file {file} after quality {best} was already drawn");
            _bestQualityShown[file] = quality;
        }

        public void AssertInvariants(string path)
        {
            if (Composer.Current is { } current)
            {
                var (file, quality) = _origin[current.Id];
                Assert.True(file == _file, $"{path}: Current belongs to file {file} but file {_file} was assigned last");
                if (Composer.Pending is { } pending)
                {
                    var (pendingFile, pendingQuality) = _origin[pending.Id];
                    Assert.True(pendingFile == file, $"{path}: Pending is of file {pendingFile}, Current of file {file}");
                    Assert.True(pendingQuality > quality, $"{path}: Pending quality {pendingQuality} is not newer than Current {quality}");
                }
            }
            else
            {
                Assert.Null(Composer.Pending);
            }

            Assert.True(Composer.Current is null || !Composer.Current.IsDisposed);
        }

        public void Dispose()
        {
            Composer.Dispose();
            _cache.Dispose();
            foreach (var buffer in _buffers) buffer.Dispose();
        }
    }

    // ---- composer: every short sequence of events -----------------------------------------------

    [Fact(DisplayName = "STALE composer: EVERY sequence of 5 events (new file, upgrade, stripe, deadline x2, draw x2) keeps Current/Pending/drawn on the latest file, never regressing")]
    public void Composer_EverySequenceOfFiveEvents_NeverShowsAnOlderFileOrAWorseBitmap()
    {
        const int alphabet = 7;
        const int depth = 5;
        var total = (int)Math.Pow(alphabet, depth);
        var sequence = new int[depth];
        for (var n = 0; n < total; n++)
        {
            var code = n;
            for (var i = 0; i < depth; i++)
            {
                sequence[i] = code % alphabet;
                code /= alphabet;
            }

            using var rig = new Rig();
            var path = string.Join(',', sequence);
            for (var i = 0; i < depth; i++)
            {
                rig.Apply(sequence[i]);
                rig.AssertInvariants($"[{path}] after step {i}");
            }

            // Whatever is still queued (late stripes, late deadlines) finishes: the end state is the latest file at its best.
            rig.Dispatcher.RunAll();
            rig.Apply(3);
            rig.Composer.TryPromotePending();
            rig.AssertInvariants($"[{path}] settled");
            Assert.Null(rig.Composer.Pending);
        }

        Assert.Equal(16807, total);
    }

    [Fact(DisplayName = "STALE composer: two upgrades of one file in a row, the OLDER one finishing uploading first: Current never shows it")]
    public void Composer_OlderUpgradeUploadingFirst_IsNeverPromotedOverTheNewerOne()
    {
        using var rig = new Rig();
        rig.Apply(0);
        var current = rig.Composer.Current;
        var first = rig.NewBuffer(0, 1);
        var second = rig.NewBuffer(0, 2);
        rig.Composer.SetIncoming(first, isFileChange: false, deferUploadBytes: 1);
        rig.Composer.SetIncoming(second, isFileChange: false, deferUploadBytes: 1);
        var shown = new List<PixelBuffer?> { rig.Composer.Current };
        rig.Composer.CurrentChanged += (_, _) => shown.Add(rig.Composer.Current);

        rig.Dispatcher.RunAll();

        Assert.Same(second, rig.Composer.Current);
        Assert.DoesNotContain(first, shown);
        Assert.Equal(1, rig.Composer.PromotionCount);
        Assert.NotSame(current, rig.Composer.Current);
    }

    // ---- GPU cache: device loss in the middle of an upgrade -------------------------------------

    [Fact(DisplayName = "STALE gpu: the device is lost mid-prefetch: the dead image never reports 'uploaded', and a re-prefetch completes exactly once")]
    public void GpuCache_DeviceLostMidPrefetch_StaleStripesNeverCompleteTheDeadImage()
    {
        using var rig = new Rig();
        var image = rig.NewBuffer(0, 0);
        var completed = new List<long>();
        rig.Cache.UploadCompleted += completed.Add;
        rig.Cache.Prefetch(image);
        Assert.True(rig.Dispatcher.RunOne());   // one stripe of four
        var rowUploadsBefore = rig.Backend.RowUploads.Count;

        rig.Backend.RaiseDeviceRecreated();     // GPU lost: every bitmap is gone
        rig.Dispatcher.RunAll();                // the queued stripes of the dead image run now

        Assert.Empty(completed);
        Assert.False(rig.Cache.IsFullyUploaded(image.Id));
        Assert.False(rig.Cache.Contains(image.Id));
        Assert.Equal(rowUploadsBefore, rig.Backend.RowUploads.Count);   // nothing was written to a released bitmap
        rig.Cache.Prefetch(image);
        rig.Dispatcher.RunAll();
        Assert.Equal([image.Id], completed);
        Assert.True(rig.Cache.IsFullyUploaded(image.Id));
    }

    [Fact(DisplayName = "STALE gpu: device lost while an upgrade is pending: the old image stays until the deadline, then the upgrade is drawn (uploaded again), once")]
    public void Composer_DeviceLostDuringPendingUpgrade_DeadlinePromotesAndDrawsTheUpgrade()
    {
        using var rig = new Rig();
        rig.Apply(0);
        rig.Apply(5);                           // the first image is on screen
        var original = rig.Composer.Current!;
        rig.Apply(1);                           // upgrade, uploading in the background
        var upgrade = rig.Composer.Pending!;
        rig.Dispatcher.RunOne();

        rig.Backend.RaiseDeviceRecreated();
        rig.Dispatcher.RunAll();

        Assert.Same(original, rig.Composer.Current);
        Assert.Same(upgrade, rig.Composer.Pending);
        Assert.Equal(1, rig.PendingDeadlines);
        rig.Apply(3);                           // the deadline fires
        Assert.Same(upgrade, rig.Composer.Current);
        Assert.Null(rig.Composer.Pending);
        var context = new RecordingImageContext();
        var frame = rig.Composer.Draw(context, new PresentationInputs(Client, Box, ScalingQuality.HighQuality,
            ScalingQuality.HighQuality, 1.0, false, default));
        Assert.True(frame.MainDrawn);
        Assert.Equal(upgrade.Id, Assert.Single(context.Images).BufferId);
        Assert.Equal(1, rig.Composer.PromotionCount);
    }

    // ---- prefetcher: a lookup that finishes after the user moved on -----------------------------

    [Fact(DisplayName = "STALE prefetch: a cache lookup that started for an OLDER navigation and finishes after a newer one is applied is dropped")]
    public void Prefetcher_LateLookupResultOfAnOlderNavigation_IsNotApplied()
    {
        var host = new DeferredHost();
        var backend = new FakeGpuBackend();
        var dispatcher = new ManualDispatcher();
        using var cache = new GpuImageCache(backend, dispatcher, 1_000_000_000, 4000);
        var prefetcher = new PresentationPrefetcher(cache, host);
        using var seven = ViewingImages.Solid(20, 20, 1, 2, 3);
        using var eight = ViewingImages.Solid(20, 20, 4, 5, 6);
        using var six = ViewingImages.Solid(20, 20, 7, 8, 9);
        host.Cache["p6"] = six;
        host.Cache["p7"] = seven;
        host.Cache["p8"] = eight;

        prefetcher.Navigated(5, 10);            // lookup L1 (p6, p7, p4) is handed to the background but has not run
        prefetcher.Navigated(6, 10);            // lookup L2 (p7, p8, p5)
        host.RunBackground(1);                  // L2 finishes and is applied first
        host.RunUi(0);
        var plannedAfterNewer = prefetcher.PlannedIds.Order().ToArray();
        var callsAfterNewer = prefetcher.PrefetchCalls;
        host.RunBackground(0);                  // L1 finishes late...
        host.RunUi(0);                          // ...and its result is posted to the UI thread after that

        Assert.Equal(new[] { seven.Id, eight.Id }.Order(), plannedAfterNewer);
        Assert.Equal(plannedAfterNewer, prefetcher.PlannedIds.Order());
        Assert.Equal(callsAfterNewer, prefetcher.PrefetchCalls);
        Assert.False(cache.Contains(six.Id));   // p6 belongs to the OLD plan only
    }

    private sealed class DeferredHost : IPrefetchHost
    {
        private readonly List<Action> _background = [];
        private readonly List<Action> _ui = [];

        public Dictionary<string, PixelBuffer> Cache { get; } = [];

        public string? PathAt(int index) => index >= 0 && index < 10 ? $"p{index}" : null;

        public PixelBuffer?[] LookupCached(string[] paths) => [.. paths.Select(path => Cache.GetValueOrDefault(path))];

        public void RunBackground(Action work) => _background.Add(work);

        public void PostToUi(Action action) => _ui.Add(action);

        public void ScheduleOnUi(TimeSpan delay, Action action)
        {
        }

        /// <summary>Runs the n-th captured background lookup (it posts its result to <see cref="RunUi"/>).</summary>
        public void RunBackground(int n) => _background[n]();

        /// <summary>Runs the oldest posted UI action that has not run yet.</summary>
        public void RunUi(int n)
        {
            var action = _ui[n];
            _ui.RemoveAt(n);
            action();
        }
    }
}

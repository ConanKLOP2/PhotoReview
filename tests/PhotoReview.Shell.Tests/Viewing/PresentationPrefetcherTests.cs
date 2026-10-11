using PhotoReview.Imaging.Pixels;
using PhotoReview.Shell.Rendering;
using PhotoReview.Shell.Tests.Rendering;
using PhotoReview.Shell.Win32.Viewing;

namespace PhotoReview.Shell.Tests.Viewing;

/// <summary>WP-21: prefetch GPU các ảnh kế tiếp theo hướng duyệt (hướng, biên, thử lại, lượt cũ bị bỏ, không upload trùng).</summary>
[Trait("Category", "HotPath")]
public sealed class PresentationPrefetcherTests : IDisposable
{
    private readonly FakeGpuBackend _backend = new();
    private readonly ManualDispatcher _dispatcher = new();
    private readonly GpuImageCache _cache;
    private readonly PresentationPrefetcher _prefetcher;
    private readonly FakeHost _host = new();
    private readonly List<PixelBuffer> _buffers = [];

    public PresentationPrefetcherTests()
    {
        _cache = new GpuImageCache(_backend, _dispatcher, 1_000_000_000, 4000);
        _prefetcher = new PresentationPrefetcher(_cache, _host);
    }

    public void Dispose()
    {
        _cache.Dispose();
        foreach (var buffer in _buffers)
        {
            buffer.Dispose();
        }
    }

    private PixelBuffer Cached(int index)
    {
        var buffer = ViewingImages.Solid(20, 20, 1, 2, 3);
        _buffers.Add(buffer);
        _host.Cache[$"p{index}"] = buffer;
        return buffer;
    }

    [Theory]
    [InlineData(5, 10, 1, new[] { 6, 7, 4 })]
    [InlineData(5, 10, -1, new[] { 4, 3, 6 })]
    [InlineData(0, 10, 1, new[] { 1, 2 })]
    [InlineData(0, 10, -1, new[] { 1 })]
    [InlineData(9, 10, 1, new[] { 8 })]
    [InlineData(9, 10, -1, new[] { 8, 7 })]
    [InlineData(0, 1, 1, new int[0])]
    public void Plan_IsAheadInTheDirectionOfTravelThenBehind_ClampedToTheCatalog(int index, int count, int direction, int[] expected)
    {
        Assert.Equal(expected, PresentationPrefetcher.Plan(count, index, direction));
    }

    [Fact]
    public void Navigated_PrefetchesTheNeighboursAheadFirstAndRetainsTheirIds()
    {
        var ahead1 = Cached(6);
        var ahead2 = Cached(7);
        var behind = Cached(4);
        _prefetcher.Navigated(index: 5, count: 10);   // lần đầu: hướng tiến

        Assert.Equal(3, _prefetcher.PrefetchCalls);
        Assert.Equal([ahead1.Id, ahead2.Id, behind.Id], _prefetcher.PlannedIds.Order());
        Assert.True(_cache.Contains(ahead1.Id));
        Assert.True(_cache.Contains(behind.Id));
    }

    [Fact]
    public void Navigated_BackwardsFlipsTheDirection()
    {
        _prefetcher.Navigated(5, 10);
        Assert.Equal(1, _prefetcher.Direction);

        _prefetcher.Navigated(4, 10);
        Assert.Equal(-1, _prefetcher.Direction);
        var nextBack = Cached(3);
        _prefetcher.Navigated(4, 10);                 // đứng yên: giữ hướng lùi

        Assert.Equal(-1, _prefetcher.Direction);
        Assert.True(_cache.Contains(nextBack.Id));
    }

    [Fact]
    public void NeighboursNotDecodedYet_ArePrefetchedByTheRetryPasses()
    {
        _prefetcher.Navigated(5, 10);
        Assert.Equal(0, _prefetcher.PrefetchCalls);
        Assert.Equal([TimeSpan.FromMilliseconds(120), TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(1000)],
            _host.Scheduled.Select(s => s.Delay));

        var late = Cached(6);                          // preload hàng xóm xong sau lần điều hướng
        _host.RunScheduled(0);

        Assert.Equal(1, _prefetcher.PrefetchCalls);
        Assert.True(_cache.Contains(late.Id));
    }

    [Fact]
    public void RetryPassesOfAnOlderNavigation_AreDropped()
    {
        _prefetcher.Navigated(5, 10);
        var staleRetry = _host.Scheduled[0].Action;
        _prefetcher.Navigated(6, 10);                  // điều hướng tiếp: lượt thử lại cũ không còn hợp lệ
        var ahead = Cached(6);                         // preload xong SAU lần điều hướng cũ

        staleRetry();

        Assert.False(_cache.Contains(ahead.Id));
        Assert.Empty(_prefetcher.PlannedIds);
        Assert.Equal(0, _prefetcher.PrefetchCalls);
    }

    [Fact]
    public void RetryPassesOfAnOlderNavigation_DoNotEvenStartABackgroundLookup()
    {
        // Lượt cũ còn tra cache (stat file trên thread nền) rồi mới bị Apply bỏ là việc thừa: phải bị chặn ngay từ lượt thử lại.
        _prefetcher.Navigated(5, 10);
        var staleRetry = _host.Scheduled[0].Action;
        _prefetcher.Navigated(6, 10);
        var runsBefore = _host.BackgroundRuns;
        var lookupBefore = _host.LastLookup;

        staleRetry();

        Assert.Equal(runsBefore, _host.BackgroundRuns);
        Assert.Same(lookupBefore, _host.LastLookup);
    }

    [Fact]
    public void AlreadyOnTheGpu_IsNotUploadedTwice()
    {
        var neighbour = Cached(6);
        _cache.GetOrUpload(neighbour);

        _prefetcher.Navigated(5, 10);

        Assert.Equal(0, _prefetcher.PrefetchCalls);
        Assert.Equal(1, _backend.Uploads.Count(u => u.Id == neighbour.Id));
        Assert.Contains(neighbour.Id, _prefetcher.PlannedIds);   // vẫn được giữ lại (Retain)
    }

    [Fact]
    public void DisposedNeighbour_IsSkipped()
    {
        var gone = Cached(6);
        gone.Dispose();

        _prefetcher.Navigated(5, 10);

        Assert.Equal(0, _prefetcher.PrefetchCalls);
        Assert.Empty(_prefetcher.PlannedIds);
    }

    [Fact]
    public void LookupRunsOffTheUiThreadAndPrefetchComesBackOnIt()
    {
        Cached(6);

        _prefetcher.Navigated(5, 10);

        Assert.Equal(1, _host.BackgroundRuns);
        Assert.Equal(1, _host.UiPosts);
        Assert.Equal(["p6", "p7", "p4"], _host.LastLookup);
    }

    [Fact]
    public void NoImage_ClearsThePlanAndCancelsPassesInFlight()
    {
        Cached(6);
        _prefetcher.Navigated(5, 10);
        var retry = _host.Scheduled[0].Action;
        var seven = Cached(7);
        _prefetcher.Navigated(-1, 0);

        retry();

        Assert.Empty(_prefetcher.PlannedIds);
        Assert.False(_cache.Contains(seven.Id));
    }

    [Fact]
    public void PlanChanged_IsRaisedAfterEachApplyingPass()
    {
        var raised = 0;
        _prefetcher.PlanChanged += (_, _) => raised++;

        _prefetcher.Navigated(5, 10);

        Assert.Equal(1, raised);
    }

    private sealed class FakeHost : IPrefetchHost
    {
        public Dictionary<string, PixelBuffer> Cache { get; } = [];

        public List<(TimeSpan Delay, Action Action)> Scheduled { get; } = [];

        public int BackgroundRuns { get; private set; }

        public int UiPosts { get; private set; }

        public string[] LastLookup { get; private set; } = [];

        public string? PathAt(int index) => index >= 0 && index < 10 ? $"p{index}" : null;

        public PixelBuffer?[] LookupCached(string[] paths)
        {
            LastLookup = paths;
            return [.. paths.Select(path => Cache.GetValueOrDefault(path))];
        }

        public void RunBackground(Action work)
        {
            BackgroundRuns++;
            work();
        }

        public void PostToUi(Action action)
        {
            UiPosts++;
            action();
        }

        public void ScheduleOnUi(TimeSpan delay, Action action) => Scheduled.Add((delay, action));

        public void RunScheduled(int index) => Scheduled[index].Action();
    }
}

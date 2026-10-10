using PhotoReview.App.Coordinators;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using Xunit;

namespace PhotoReview.App.Tests.Coordinators;

public sealed partial class ImagePresenterTests
{
    private static readonly TimeSpan DeferredKickTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// A UI scheduler whose <see cref="IUiScheduler.YieldAsync"/> calls stay pending until the test releases them, in call
    /// order (the presenter calls it once for the deferred preload kick, then once before its status bookkeeping).
    /// </summary>
    private sealed class GatedYieldScheduler : IUiScheduler
    {
        private readonly object _gate = new();
        private readonly List<TaskCompletionSource> _yields = [];
        private readonly List<(int Count, TaskCompletionSource Reached)> _waiters = [];

        public void Post(Action action) => action();
        public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }

        public ValueTask YieldAsync(CancellationToken cancellationToken = default)
        {
            var yield = new TaskCompletionSource(); // continuations run inline on Release: deterministic order for the test
            lock (_gate)
            {
                _yields.Add(yield);
                foreach (var waiter in _waiters.Where(w => _yields.Count >= w.Count).ToList())
                {
                    _waiters.Remove(waiter);
                    waiter.Reached.TrySetResult();
                }
            }
            return new ValueTask(yield.Task);
        }

        /// <summary>Completes when at least <paramref name="count"/> YieldAsync calls have been made.</summary>
        public Task WaitForYieldsAsync(int count)
        {
            lock (_gate)
            {
                if (_yields.Count >= count) return Task.CompletedTask;
                var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add((count, reached));
                return reached.Task;
            }
        }

        public void Release(int zeroBasedCall)
        {
            TaskCompletionSource yield;
            lock (_gate) yield = _yields[zeroBasedCall];
            yield.TrySetResult();
        }
    }

    private ImagePresenter CreateScheduledPresenter(IUiScheduler? scheduler) => new(
        _catalog, _clock, _previewService, _thumbnailCache, _preloadController, _compareViewModel, _hashService, _metrics,
        () => _settings, _sessionStore, _sink, fileSystem: null, getSession: () => null, uiScheduler: scheduler);

    [Fact(DisplayName = "DeferNextPreloadKick: the first preload starts only after the scheduler yields, once, then the next present kicks synchronously again")]
    public async Task PresentAsync_DeferNextPreloadKick_KicksAfterTheYieldOnceThenGoesBackToSynchronous()
    {
        var first = CreateFakeImageFile("first.png");
        var second = CreateFakeImageFile("second.png");
        _catalog.Reset([first, second]);
        var scheduler = new GatedYieldScheduler();
        var presenter = CreateScheduledPresenter(scheduler);
        presenter.DeferNextPreloadKick = true;

        var firstPresent = presenter.PresentAsync(0);
        await scheduler.WaitForYieldsAsync(2).WaitAsync(DeferredKickTimeout); // deferred kick + post-frame bookkeeping
        Assert.Empty(_preloadController.PreloadAroundCalls);
        Assert.False(presenter.DeferNextPreloadKick); // consumed when the kick was scheduled

        scheduler.Release(0);
        Assert.Equal([0], _preloadController.PreloadAroundCalls);
        scheduler.Release(1);
        await firstPresent.WaitAsync(DeferredKickTimeout);
        Assert.Equal([0], _preloadController.PreloadAroundCalls); // exactly one kick

        var secondPresent = presenter.PresentAsync(1);
        await scheduler.WaitForYieldsAsync(3).WaitAsync(DeferredKickTimeout); // only the post-frame yield: the kick did not wait for one
        Assert.Equal([0, 1], _preloadController.PreloadAroundCalls);
        scheduler.Release(2);
        await secondPresent.WaitAsync(DeferredKickTimeout);
    }

    [Fact(DisplayName = "DeferNextPreloadKick: a deferred kick is dropped when another navigation became current before the yield returned")]
    public async Task PresentAsync_DeferNextPreloadKick_NavigationSupersededBeforeTheYield_DropsTheKick()
    {
        var path = CreateFakeImageFile("only.png");
        _catalog.Reset([path]);
        var scheduler = new GatedYieldScheduler();
        var presenter = CreateScheduledPresenter(scheduler);
        presenter.DeferNextPreloadKick = true;

        var present = presenter.PresentAsync(0);
        await scheduler.WaitForYieldsAsync(2).WaitAsync(DeferredKickTimeout);
        _clock.NextNavigation(); // the user moved on: another presentation owns the kick now

        scheduler.Release(0);
        scheduler.Release(1);
        await present.WaitAsync(DeferredKickTimeout);

        Assert.Empty(_preloadController.PreloadAroundCalls);
        Assert.False(presenter.DeferNextPreloadKick);
    }

    [Fact(DisplayName = "Without DeferNextPreloadKick the preload kick is synchronous, before the scheduler yields")]
    public async Task PresentAsync_WithoutDeferFlag_KicksBeforeAnyYield()
    {
        var path = CreateFakeImageFile("plain.png");
        _catalog.Reset([path]);
        var scheduler = new GatedYieldScheduler();
        var presenter = CreateScheduledPresenter(scheduler);

        var present = presenter.PresentAsync(0);
        await scheduler.WaitForYieldsAsync(1).WaitAsync(DeferredKickTimeout); // the post-frame yield: the kick is already behind us

        Assert.Equal([0], _preloadController.PreloadAroundCalls);
        scheduler.Release(0);
        await present.WaitAsync(DeferredKickTimeout);
        Assert.Equal([0], _preloadController.PreloadAroundCalls);
    }

    [Fact(DisplayName = "DeferNextPreloadKick without a UI scheduler still kicks synchronously")]
    public async Task PresentAsync_DeferFlagWithoutScheduler_KicksSynchronously()
    {
        var path = CreateFakeImageFile("noscheduler.png");
        _catalog.Reset([path]);
        var presenter = CreateScheduledPresenter(scheduler: null);
        presenter.DeferNextPreloadKick = true;

        await presenter.PresentAsync(0).WaitAsync(DeferredKickTimeout);

        Assert.Equal([0], _preloadController.PreloadAroundCalls);
    }
}

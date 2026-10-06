using System.Collections.Concurrent;
using System.IO;
using PhotoReview.Core.Abstractions;

namespace PhotoReview.Imaging.Tests;

/// <summary>
/// Memory-pause auto-resume of <see cref="PreloadScheduler"/>: a run that paused for memory headroom is re-armed by a
/// low-frequency re-check once the load is below the pause limit minus <see cref="PreloadScheduler.ResumeLoadMargin"/>.
/// The re-check timer is a manual fake (fired by the test, never by time), the load is a fake probe: no sleeps.
/// </summary>
public sealed partial class PreloadSchedulerMutationTests
{
    // PreloadOptions' default MemoryLoadLimit (Create does not override it): pause at >= 0.80, resume only below 0.75.
    private const double PauseLimit = 0.80;

    private sealed class LoadProbe : IMemoryProbe
    {
        private double _load;
        public double Load { get => Volatile.Read(ref _load); set => Volatile.Write(ref _load, value); }
        /// <summary>Optional override of the answer, given the requested maximum load.</summary>
        public Func<double, bool>? Answer { get; set; }
        public ConcurrentQueue<double> Limits { get; } = new();

        public bool HasHeadroom(double maximumLoad, long reserveBytes)
        {
            Limits.Enqueue(maximumLoad);
            return Answer?.Invoke(maximumLoad) ?? Load < maximumLoad;
        }

        public MemorySnapshot? GetSnapshot() => new((uint)(Load * 100), 16L * 1024 * 1024 * 1024);
    }

    private sealed class ManualTime : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];
        public ManualTimer[] Timers { get { lock (_timers) return _timers.ToArray(); } }
        public ManualTimer? Pending => Timers.LastOrDefault(t => !t.Disposed);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state, dueTime);
            lock (_timers) _timers.Add(timer);
            return timer;
        }
    }

    private sealed class ManualTimer(TimerCallback callback, object? state, TimeSpan due) : ITimer
    {
        public TimeSpan Due { get; private set; } = due;
        public bool Disposed { get; private set; }
        // Runs the callback even after Dispose: a real timer callback can already be running when Cancel/Dispose happens.
        public void Fire() => callback(state);
        public bool Change(TimeSpan dueTime, TimeSpan period) { Due = dueTime; return !Disposed; }
        public void Dispose() => Disposed = true;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }

    // A run around image 0 with a 4-image forward window that pauses for memory before starting anything.
    private async Task<(PreloadScheduler Scheduler, Target Target, LoadProbe Probe, ManualTime Time)> PausedRunAsync(
        bool wholeFolder = false, long cacheBytes = 0)
    {
        var entries = Entries(10);
        var target = new Target { Bytes = cacheBytes };
        var probe = new LoadProbe { Load = 0.95 };
        var time = new ManualTime();
        var scheduler = Create(target, entries, workers: 2, new PreloadWindow(4, 0), wholeFolder, probe: probe, time: time);
        await scheduler.PreloadAroundAsync(0).WaitAsync(Guard);
        Assert.Empty(target.Started);
        return (scheduler, target, probe, time);
    }

    [Fact(DisplayName = "Memory pause: once the pressure clears, the re-check resumes preload exactly once")]
    public async Task MemoryPause_PressureClears_ResumesExactlyOnce()
    {
        var (scheduler, target, probe, time) = await PausedRunAsync();
        var check = Assert.Single(time.Timers);
        Assert.Equal(PreloadScheduler.ResumeCheckBaseDelay, check.Due);

        probe.Load = 0.50;
        check.Fire();
        await scheduler.ResumedRunForTests!.WaitAsync(Guard);

        Assert.Equal(1, scheduler.ResumesForTests);
        Assert.Equal(4, target.Started.Count); // the whole window around the unchanged centre
        Assert.Single(time.Timers);            // the resumed run did not pause: nothing re-armed
        check.Fire();                          // a late duplicate callback of the same re-check
        Assert.Equal(1, scheduler.ResumesForTests);
        Assert.Equal(4, target.Started.Count);
    }

    [Fact(DisplayName = "Memory pause: while the pressure persists nothing resumes and the re-checks back off and stop after MaxResumeChecks")]
    public async Task MemoryPause_PressurePersists_BoundedBackoffWithoutResume()
    {
        var (scheduler, target, _, time) = await PausedRunAsync();

        for (var i = 0; i < 50 && time.Pending is { } pending; i++) pending.Fire();

        Assert.Equal([2.0, 4, 8, 16, 30, 30, 30, 30], time.Timers.Select(t => t.Due.TotalSeconds));
        Assert.Equal(PreloadScheduler.MaxResumeChecks, time.Timers.Length);
        Assert.Null(time.Pending);
        Assert.Equal(0, scheduler.ResumesForTests);
        Assert.Empty(target.Started);

        // A real navigation still kicks preload as before, and gives the re-checks a fresh budget.
        await scheduler.PreloadAroundAsync(5).WaitAsync(Guard);
        Assert.Equal(PreloadScheduler.MaxResumeChecks + 1, time.Timers.Length);
        Assert.Equal(PreloadScheduler.ResumeCheckBaseDelay, time.Timers[^1].Due);
    }

    [Fact(DisplayName = "Memory pause: hysteresis -- a load just under the pause limit does not resume; below the margin it does")]
    public async Task MemoryPause_LoadInsideMargin_DoesNotResume()
    {
        var (scheduler, target, probe, time) = await PausedRunAsync();

        probe.Load = PauseLimit - PreloadScheduler.ResumeLoadMargin / 2; // 0.775: preload would not pause here, but must not resume either
        time.Pending!.Fire();
        Assert.Equal(0, scheduler.ResumesForTests);
        Assert.Empty(target.Started);
        Assert.Equal(2, time.Timers.Length); // re-armed
        Assert.Contains(probe.Limits, limit => Math.Abs(limit - (PauseLimit - PreloadScheduler.ResumeLoadMargin)) < 1e-9);

        probe.Load = 0.70;
        time.Pending!.Fire();
        await scheduler.ResumedRunForTests!.WaitAsync(Guard);
        Assert.Equal(1, scheduler.ResumesForTests);
        Assert.Equal(4, target.Started.Count);
    }

    [Theory(DisplayName = "Memory pause: Cancel (folder change) or Dispose cancels the re-check, even one already firing")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MemoryPause_CancelOrDispose_CancelsTheRecheck(bool cancel)
    {
        var (scheduler, target, probe, time) = await PausedRunAsync();
        var check = Assert.Single(time.Timers);

        if (cancel) scheduler.Cancel();
        else scheduler.Dispose();
        Assert.True(check.Disposed);

        probe.Load = 0.50;
        check.Fire();
        Assert.Equal(0, scheduler.ResumesForTests);
        Assert.Empty(target.Started);
        Assert.Single(time.Timers);
    }

    [Fact(DisplayName = "Memory pause: a navigation during the pause preloads around the new image as before and retires the re-check")]
    public async Task MemoryPause_NavigationDuringPause_BehavesAsBefore()
    {
        var (scheduler, target, probe, time) = await PausedRunAsync();
        var check = Assert.Single(time.Timers);
        probe.Load = 0.50;

        scheduler.NotifyNavigation(5);
        Assert.True(check.Disposed);
        await scheduler.PreloadAroundAsync(5).WaitAsync(Guard);

        Assert.Equal(["img-6.jpg", "img-7.jpg", "img-8.jpg", "img-9.jpg"],
            target.Started.Select(Path.GetFileName).Order(StringComparer.Ordinal));
        check.Fire(); // a stale callback
        Assert.Equal(0, scheduler.ResumesForTests);
        Assert.Equal(4, target.Started.Count);
    }

    [Fact(DisplayName = "Memory pause: a navigation that lands while the re-check is probing memory wins; the re-check does not kick a run")]
    public async Task MemoryPause_NavigationDuringTheProbe_SupersedesTheResume()
    {
        var (scheduler, target, probe, time) = await PausedRunAsync();
        // The re-check's own probe (the only call with the resume limit) is the window between its generation check and its kick.
        probe.Answer = maximumLoad =>
        {
            if (maximumLoad < PauseLimit) scheduler.NotifyNavigation(5);
            return true;
        };

        time.Pending!.Fire();

        Assert.Equal(0, scheduler.ResumesForTests);
        Assert.Empty(target.Started); // the navigation's own PreloadAroundAsync (not called here) is what preloads around 5
    }

    [Fact(DisplayName = "Memory pause: no resume once the cache is at the whole-folder fill limit, and no further re-checks")]
    public async Task MemoryPause_CacheAtFillLimit_DoesNotResume()
    {
        var (scheduler, target, probe, time) = await PausedRunAsync(wholeFolder: true, cacheBytes: 950_000_000);
        probe.Load = 0.50;

        time.Pending!.Fire();

        Assert.Equal(0, scheduler.ResumesForTests);
        Assert.Empty(target.Started);
        Assert.Single(time.Timers);
        Assert.Null(time.Pending);
    }

    [Fact(DisplayName = "Memory pause: the re-check does not resume against a running viewer decode; it checks again later")]
    public async Task MemoryPause_ViewerDecoding_WaitsForTheNextCheck()
    {
        var (scheduler, target, probe, time) = await PausedRunAsync();
        probe.Load = 0.50;
        target.ActiveViewer = 1;

        time.Pending!.Fire();
        Assert.Equal(0, scheduler.ResumesForTests);
        Assert.Equal(2, time.Timers.Length);

        target.ActiveViewer = 0;
        time.Pending!.Fire();
        await scheduler.ResumedRunForTests!.WaitAsync(Guard);
        Assert.Equal(1, scheduler.ResumesForTests);
    }

    [Fact(DisplayName = "Memory pause: a resumed run that pauses again keeps backing off (no pause/resume storm)")]
    public async Task MemoryPause_ResumedRunPausesAgain_BackoffContinues()
    {
        var (scheduler, target, probe, time) = await PausedRunAsync();
        // The re-check sees headroom, then the pressure is back before the resumed run probes again.
        probe.Answer = maximumLoad =>
        {
            var ok = probe.Load < maximumLoad;
            if (ok && maximumLoad < PauseLimit) probe.Load = 0.95;
            return ok;
        };
        probe.Load = 0.50;

        time.Pending!.Fire();
        await scheduler.ResumedRunForTests!.WaitAsync(Guard);

        Assert.Equal(1, scheduler.ResumesForTests);
        Assert.Empty(target.Started);
        Assert.Equal(2, time.Timers.Length);
        Assert.Equal(PreloadScheduler.ResumeCheckDelay(1), time.Timers[1].Due); // 4 s, not back to 2 s
    }
}

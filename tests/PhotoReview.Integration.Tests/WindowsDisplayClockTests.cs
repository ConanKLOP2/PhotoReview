using System.Diagnostics;
using PhotoReview.Core.Abstractions;
using PhotoReview.Platform.Windows;

namespace PhotoReview.Integration.Tests;

/// <summary>
/// The vblank clock thread of <see cref="WindowsDisplayClock"/>, driven step by step through a scripted driver and a fake QPC
/// clock: one released semaphore permit is exactly one vblank, so no test waits on wall-clock time and no real display,
/// adapter or DWM call is involved (except the one invariant test of the static DWM fallback).
/// </summary>
public sealed class WindowsDisplayClockTests
{
    private static readonly IntPtr WindowA = new(0xA0);
    private static readonly IntPtr WindowB = new(0xB0);
    private static readonly IntPtr MonitorA = new(0xA1);
    private static readonly IntPtr MonitorB = new(0xB1);
    private static readonly DisplayTiming DwmSentinel = new(111, 222);
    private static readonly long PeriodTicks = Stopwatch.Frequency / 60;
    private static readonly long RetryTicks = (WindowsDisplayClock.RetryFailedAfterMs + 1) * Stopwatch.Frequency / 1000;
    private static readonly long IdleTicks = (WindowsDisplayClock.IdleStopMs + 1) * Stopwatch.Frequency / 1000;

    private sealed class FakeLog : ILog
    {
        private readonly object _gate = new();
        private readonly List<string> _messages = [];
        public bool Enabled => true;
        public SemaphoreSlim Warned { get; } = new(0);
        public SemaphoreSlim Errored { get; } = new(0);
        public IReadOnlyList<string> Messages { get { lock (_gate) return [.. _messages]; } }
        public void Info(string message) { lock (_gate) _messages.Add("INFO: " + message); }
        public void Warn(string message) { lock (_gate) _messages.Add("WARN: " + message); Warned.Release(); }
        public void Error(string message, Exception? ex = null) { lock (_gate) _messages.Add($"ERROR: {message} {ex?.GetType().Name}"); Errored.Release(); }
    }

    private sealed class FakeDriver : IVBlankDriver, IDisposable
    {
        private readonly object _gate = new();
        private readonly SemaphoreSlim _vblank = new(0);
        private readonly SemaphoreSlim _waitEntered = new(0);
        private readonly SemaphoreSlim _opened = new(0);
        private readonly SemaphoreSlim _closed = new(0);
        private readonly Dictionary<IntPtr, IntPtr> _monitorOf = new() { [WindowA] = MonitorA, [WindowB] = MonitorB };
        private readonly List<IntPtr> _openedMonitors = [];
        private readonly List<uint> _closedAdapters = [];
        private long _now = 1_000_000;
        private bool _stopping;

        public bool OpenSucceeds { get; set; } = true;
        public int NextWaitStatus { get; set; }
        public Exception? NextWaitThrows { get; set; }
        public IReadOnlyList<IntPtr> OpenedMonitors { get { lock (_gate) return [.. _openedMonitors]; } }
        public IReadOnlyList<uint> ClosedAdapters { get { lock (_gate) return [.. _closedAdapters]; } }
        public long Now => Interlocked.Read(ref _now);
        public void Advance(long ticks) => Interlocked.Add(ref _now, ticks);

        public IntPtr MonitorFromWindow(IntPtr window) => _monitorOf.GetValueOrDefault(window);

        public bool TryOpen(IntPtr monitor, out uint adapter, out uint source)
        {
            lock (_gate) _openedMonitors.Add(monitor);
            // Adapter handle = the monitor's low bits, so a close can be matched with the open it belongs to.
            adapter = OpenSucceeds ? (uint)(monitor.ToInt64() & 0xFFFF) : 0;
            source = 7;
            _opened.Release();
            return OpenSucceeds;
        }

        public int WaitForVBlank(uint adapter, uint source)
        {
            _waitEntered.Release();
            if (!_vblank.Wait(TimeSpan.FromSeconds(30)) || Volatile.Read(ref _stopping)) return unchecked((int)0xC0000001);
            if (NextWaitThrows is { } ex) { NextWaitThrows = null; throw ex; }
            Advance(PeriodTicks);
            var status = NextWaitStatus;
            return status;
        }

        public void Close(uint adapter)
        {
            lock (_gate) _closedAdapters.Add(adapter);
            _closed.Release();
        }

        public DisplayTiming? DwmTiming() => DwmSentinel;

        /// <summary>Lets the clock thread take one vblank; returns once it is back to waiting for the next one (the sample is recorded).</summary>
        public async Task StepVBlankAsync()
        {
            _vblank.Release();
            await AwaitAsync(_waitEntered, "the clock thread to wait for the next vblank");
        }

        /// <summary>Lets the clock thread take one vblank without waiting for it to come back (for tests where the thread is expected to leave).</summary>
        public void ReleaseVBlank() => _vblank.Release();

        public Task AwaitWaitingAsync() => AwaitAsync(_waitEntered, "the clock thread to wait for a vblank");
        public Task AwaitOpenedAsync() => AwaitAsync(_opened, "the clock thread to open an adapter");
        public Task AwaitClosedAsync() => AwaitAsync(_closed, "the clock thread to close its adapter");

        public static async Task AwaitAsync(SemaphoreSlim signal, string what)
        {
            if (!await signal.WaitAsync(TimeSpan.FromSeconds(20))) throw new TimeoutException("Timed out waiting for " + what);
        }

        public void Dispose()
        {
            Volatile.Write(ref _stopping, true);
            _vblank.Release(1000);
        }
    }

    private static WindowsDisplayClock NewClock(FakeDriver driver, FakeLog? log = null)
        => new(log ?? new FakeLog(), driver, () => driver.Now);

    /// <summary>Starts the thread for <paramref name="window"/> and feeds it enough vblanks for an estimate.</summary>
    private static async Task WarmUpAsync(WindowsDisplayClock clock, FakeDriver driver, IntPtr window)
    {
        Assert.Null(clock.GetTiming(window)); // starts the thread; nothing measured yet
        await driver.AwaitOpenedAsync();
        await driver.AwaitWaitingAsync();
        for (var i = 0; i < VBlankEstimator.MinSamples; i++) await driver.StepVBlankAsync();
    }

    [Fact]
    public void Constructor_NullDriverOrClock_Throws()
    {
        using var driver = new FakeDriver();
        Assert.Throws<ArgumentNullException>(() => new WindowsDisplayClock(null, null!, () => 0));
        Assert.Throws<ArgumentNullException>(() => new WindowsDisplayClock(null, driver, null!));
    }

    [Fact]
    public void GetTiming_NoWindow_ReturnsDwmTimingWithoutStartingAThread()
    {
        using var driver = new FakeDriver();
        var clock = NewClock(driver);

        Assert.Equal(DwmSentinel, clock.GetTiming(IntPtr.Zero));
        Assert.Empty(driver.OpenedMonitors);
    }

    [Fact]
    public void GetTiming_WindowWithoutMonitor_ReturnsDwmTiming()
    {
        using var driver = new FakeDriver();
        var clock = NewClock(driver);

        Assert.Equal(DwmSentinel, clock.GetTiming(new IntPtr(0xDEAD))); // the driver knows no monitor for it
        Assert.Empty(driver.OpenedMonitors);
    }

    [Fact]
    public async Task GetTiming_AfterEnoughVBlanks_ReportsTheMeasuredGrid()
    {
        using var driver = new FakeDriver();
        var clock = NewClock(driver);

        await WarmUpAsync(clock, driver, WindowA);

        var timing = clock.GetTiming(WindowA);
        Assert.NotNull(timing);
        Assert.Equal(PeriodTicks, timing.Value.RefreshPeriod);
        Assert.Equal(driver.Now, timing.Value.LastVBlank); // the latest vblank observed
        Assert.Equal([MonitorA], driver.OpenedMonitors);
    }

    [Fact]
    public async Task GetTiming_FewerThanMinSamples_StaysNull()
    {
        using var driver = new FakeDriver();
        var clock = NewClock(driver);

        Assert.Null(clock.GetTiming(WindowA));
        await driver.AwaitWaitingAsync();
        for (var i = 0; i < VBlankEstimator.MinSamples - 1; i++) await driver.StepVBlankAsync();

        Assert.Null(clock.GetTiming(WindowA));
    }

    [Fact]
    public async Task GetTiming_SecondCallsReuseTheRunningThread()
    {
        using var driver = new FakeDriver();
        var clock = NewClock(driver);

        await WarmUpAsync(clock, driver, WindowA);
        _ = clock.GetTiming(WindowA);
        _ = clock.GetTiming(WindowA);

        Assert.Single(driver.OpenedMonitors);
    }

    [Fact]
    public async Task GetTiming_WindowMovesToAnotherMonitor_ReopensAndRemeasures()
    {
        using var driver = new FakeDriver();
        var clock = NewClock(driver);
        await WarmUpAsync(clock, driver, WindowA);
        Assert.NotNull(clock.GetTiming(WindowA));

        // The window is now on monitor B: A's estimate must not be reported for it.
        Assert.Null(clock.GetTiming(WindowB));
        await driver.StepVBlankAsync(); // the thread notices the new target at its next wake-up, closes A, opens B

        Assert.Equal([MonitorA, MonitorB], driver.OpenedMonitors);
        Assert.Equal([(uint)(MonitorA.ToInt64() & 0xFFFF)], driver.ClosedAdapters);
        Assert.Null(clock.GetTiming(WindowB)); // estimate was reset: nothing measured on B yet

        for (var i = 0; i < VBlankEstimator.MinSamples; i++) await driver.StepVBlankAsync();
        var timing = clock.GetTiming(WindowB);
        Assert.NotNull(timing);
        Assert.Equal(PeriodTicks, timing.Value.RefreshPeriod);
    }

    [Fact]
    public async Task OpenFailure_FallsBackToDwmForThatMonitorAndRetriesAfterTheWindow()
    {
        using var driver = new FakeDriver { OpenSucceeds = false };
        var log = new FakeLog();
        var clock = NewClock(driver, log);

        Assert.Null(clock.GetTiming(WindowA));
        await driver.AwaitOpenedAsync();
        await FakeDriver.AwaitAsync(log.Warned, "the failed open to be reported");

        Assert.Contains(log.Messages, m => m.Contains("opening the adapter failed", StringComparison.Ordinal) && m.Contains("0xA1", StringComparison.Ordinal));
        Assert.Equal(DwmSentinel, clock.GetTiming(WindowA)); // inside the retry window: DWM, no new attempt
        Assert.Single(driver.OpenedMonitors);

        driver.Advance(RetryTicks);
        driver.OpenSucceeds = true;
        Assert.Null(clock.GetTiming(WindowA)); // retried: a new thread, nothing measured yet
        await driver.AwaitOpenedAsync();
        await driver.AwaitWaitingAsync();
        Assert.Equal([MonitorA, MonitorA], driver.OpenedMonitors);
    }

    [Fact]
    public async Task OpenFailure_OtherMonitorIsNotAffected()
    {
        using var driver = new FakeDriver { OpenSucceeds = false };
        var log = new FakeLog();
        var clock = NewClock(driver, log);
        Assert.Null(clock.GetTiming(WindowA));
        await FakeDriver.AwaitAsync(log.Warned, "monitor A to be marked failed");

        driver.OpenSucceeds = true;
        Assert.Null(clock.GetTiming(WindowB)); // B has no failure record: a vblank thread is started for it

        await driver.AwaitWaitingAsync();
        Assert.Equal([MonitorA, MonitorB], driver.OpenedMonitors);
    }

    [Fact]
    public async Task WaitFailure_ClosesTheAdapterAndFallsBackToDwm()
    {
        using var driver = new FakeDriver();
        var log = new FakeLog();
        var clock = NewClock(driver, log);
        await WarmUpAsync(clock, driver, WindowA);

        driver.NextWaitStatus = unchecked((int)0xC0000001);
        driver.ReleaseVBlank(); // this wait answers the failure status
        await FakeDriver.AwaitAsync(log.Warned, "the wait failure to be reported");

        Assert.Contains(log.Messages, m => m.Contains("D3DKMTWaitForVerticalBlankEvent returned 0xC0000001", StringComparison.Ordinal));
        await driver.AwaitClosedAsync();
        Assert.Equal(DwmSentinel, clock.GetTiming(WindowA));
    }

    [Fact]
    public async Task MissingNativeLibrary_FallsBackToDwm()
    {
        using var driver = new FakeDriver();
        var log = new FakeLog();
        var clock = NewClock(driver, log);
        Assert.Null(clock.GetTiming(WindowA));
        await driver.AwaitWaitingAsync();

        driver.NextWaitThrows = new DllNotFoundException("gdi32 missing");
        driver.ReleaseVBlank(); // the wait throws: the thread leaves its loop
        await FakeDriver.AwaitAsync(log.Warned, "the missing library to be reported");

        Assert.Contains(log.Messages, m => m.Contains("DllNotFoundException", StringComparison.Ordinal));
        Assert.Equal(DwmSentinel, clock.GetTiming(WindowA));
        Assert.DoesNotContain(log.Messages, m => m.StartsWith("ERROR", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnexpectedFailure_IsLoggedAndFallsBackToDwm()
    {
        using var driver = new FakeDriver();
        var log = new FakeLog();
        var clock = NewClock(driver, log);
        Assert.Null(clock.GetTiming(WindowA));
        await driver.AwaitWaitingAsync();

        driver.NextWaitThrows = new InvalidOperationException("boom");
        driver.ReleaseVBlank();
        await FakeDriver.AwaitAsync(log.Errored, "the failure to be logged");
        await FakeDriver.AwaitAsync(log.Warned, "the fallback to be reported");

        Assert.Contains(log.Messages, m => m.Contains("thread failed with InvalidOperationException", StringComparison.Ordinal));
        Assert.Equal(DwmSentinel, clock.GetTiming(WindowA));
    }

    [Fact]
    public async Task IdleThread_ExitsDropsItsStaleEstimateAndRestartsOnTheNextCall()
    {
        using var driver = new FakeDriver();
        var clock = NewClock(driver);
        await WarmUpAsync(clock, driver, WindowA);
        Assert.NotNull(clock.GetTiming(WindowA));

        driver.Advance(IdleTicks); // nobody asked for timing for longer than IdleStopMs
        driver.ReleaseVBlank();
        await driver.AwaitClosedAsync(); // the thread saw the idle time at its loop head and exited (adapter closed)

        // The old estimate drifts while nobody feeds it, so it must not be handed out again: null, and a fresh thread.
        Assert.Null(clock.GetTiming(WindowA));
        await driver.AwaitOpenedAsync();
        Assert.Equal([MonitorA, MonitorA], driver.OpenedMonitors);
    }

    [Fact]
    public void DwmTiming_Static_ReturnsNullOrAPositiveGrid()
    {
        var timing = WindowsDisplayClock.DwmTiming();

        if (timing is { } t)
        {
            Assert.True(t.RefreshPeriod > 0);
            Assert.True(t.LastVBlank > 0);
        }
    }
}

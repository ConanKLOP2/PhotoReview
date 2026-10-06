using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using System.Windows.Threading;
using PhotoReview.App.Diagnostics;
using PhotoReview.TestSupport.Windows;

namespace PhotoReview.App.Tests.Diagnostics;

/// <summary>
/// D04: <see cref="PerfDispatcherHooks"/> turns slow dispatcher operations into <c>DispatcherLongOp</c> events (priority
/// + "Type.Method" name) and stays silent for fast ones, aborted ones and after <c>Detach</c>. Real Dispatcher on its own
/// STA thread; the events are read with an in-process EventListener on the PhotoReview-Perf source.
/// </summary>
[Trait("Category", "UI")]
[Collection("GlobalState")] // attaches a listener to the process-wide EventSource and sets PHOTOREVIEW_DIAG_* variables
public sealed class PerfDispatcherHooksTests : IDisposable
{
    private const int DispatcherLongOpId = 22;
    private const int DiagModeId = 24;
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private sealed record Captured(int Id, object?[] Payload);

    private sealed class CaptureListener : EventListener
    {
        public readonly ConcurrentQueue<Captured> Events = new();

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == "PhotoReview-Perf") EnableEvents(eventSource, EventLevel.Informational);
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData) =>
            Events.Enqueue(new Captured(eventData.EventId, eventData.Payload?.ToArray() ?? []));
    }

    private readonly CaptureListener _listener = new();
    private readonly List<string> _envToRestore = [];

    public void Dispose()
    {
        foreach (var name in _envToRestore) Environment.SetEnvironmentVariable(name, null);
        _listener.Dispose();
    }

    // Distinct method names: the hook names an operation "DeclaringType.Method" of its callback.
    private static void SlowWork() => Thread.Sleep(60);

    private static void SlowWorkDetached() => Thread.Sleep(60);

    private static void FastWork()
    {
    }

    private IEnumerable<Captured> LongOpsNamed(string methodName) =>
        _listener.Events.Where(e => e.Id == DispatcherLongOpId && e.Payload[2] is string n && n.EndsWith("." + methodName, StringComparison.Ordinal));

    /// <summary>Runs <paramref name="work"/> on the dispatcher thread and returns once the hook for it has run (a later queued no-op).</summary>
    private static async Task RunAndSettleAsync(Dispatcher dispatcher, Action work, DispatcherPriority priority = DispatcherPriority.Normal)
    {
        await dispatcher.InvokeAsync(work, priority).Task.WaitAsync(Bound);
        // OperationCompleted is raised after the operation's own task completes; a barrier queued behind it runs after the hook.
        await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Normal).Task.WaitAsync(Bound);
    }

    [Fact]
    public async Task SlowOperation_EmitsDispatcherLongOpWithPriorityAndCallbackName()
    {
        using var ui = new DispatcherThread();
        var hooks = PerfDispatcherHooks.Attach(ui.Dispatcher);
        Assert.NotNull(hooks);

        await RunAndSettleAsync(ui.Dispatcher, SlowWork, DispatcherPriority.Normal);

        var evt = Assert.Single(LongOpsNamed(nameof(SlowWork)));
        Assert.Equal(nameof(PerfDispatcherHooksTests) + "." + nameof(SlowWork), evt.Payload[2]);
        Assert.Equal("Normal", evt.Payload[1]);
        Assert.True((double)evt.Payload[0]! >= 16, $"the reported duration {evt.Payload[0]} ms must exceed the 16 ms threshold");
    }

    [Fact]
    public async Task OperationBelowTheThreshold_EmitsNothing()
    {
        using var ui = new DispatcherThread();
        // A threshold no real operation reaches keeps this independent of machine load; SlowWork sleeps 60 ms and is still below it.
        PerfDispatcherHooks.Attach(ui.Dispatcher, longOpThresholdMs: 60_000);

        await RunAndSettleAsync(ui.Dispatcher, SlowWork);

        Assert.Empty(LongOpsNamed(nameof(SlowWork)));
    }

    [Fact]
    public async Task ThresholdIsHonoured_AFastOperationAboveALowThresholdIsReported()
    {
        using var ui = new DispatcherThread();
        // Threshold below zero: every completed operation counts as long, which proves the comparison uses the configured value.
        PerfDispatcherHooks.Attach(ui.Dispatcher, longOpThresholdMs: -1);

        await RunAndSettleAsync(ui.Dispatcher, FastWork);

        Assert.Single(LongOpsNamed(nameof(FastWork)));
    }

    [Fact]
    public async Task DefaultThreshold_IsSixteenMilliseconds()
    {
        Assert.Equal(16, PerfDispatcherHooks.DefaultLongOpThresholdMs);
        using var ui = new DispatcherThread();
        PerfDispatcherHooks.Attach(ui.Dispatcher);
        await RunAndSettleAsync(ui.Dispatcher, SlowWork);
        Assert.Single(LongOpsNamed(nameof(SlowWork)));
    }

    [Fact]
    public async Task Detach_StopsTheReporting()
    {
        using var ui = new DispatcherThread();
        var hooks = PerfDispatcherHooks.Attach(ui.Dispatcher);
        await RunAndSettleAsync(ui.Dispatcher, SlowWork);
        Assert.Single(LongOpsNamed(nameof(SlowWork)));

        hooks!.Detach();
        await RunAndSettleAsync(ui.Dispatcher, SlowWorkDetached);

        Assert.Empty(LongOpsNamed(nameof(SlowWorkDetached)));
    }

    [Fact]
    public async Task AbortedOperation_NeverReportsAndTheNextOperationStillDoes()
    {
        using var ui = new DispatcherThread();
        PerfDispatcherHooks.Attach(ui.Dispatcher);
        var release = new ManualResetEventSlim();
        var blocker = ui.Dispatcher.InvokeAsync(() => release.Wait(Bound));
        var pending = ui.Dispatcher.InvokeAsync(SlowWorkDetached, DispatcherPriority.Background);

        Assert.True(pending.Abort());
        release.Set();
        await blocker.Task.WaitAsync(Bound);
        await RunAndSettleAsync(ui.Dispatcher, SlowWork);

        Assert.Equal(DispatcherOperationStatus.Aborted, pending.Status);
        Assert.Empty(LongOpsNamed(nameof(SlowWorkDetached)));
        Assert.Single(LongOpsNamed(nameof(SlowWork)));
    }

    [Fact]
    public void Attach_WhenTheHooksCannotBeReached_ReturnsNullInsteadOfThrowing()
    {
        // Perf tracing is optional diagnostics: a dispatcher it cannot hook must never take the app down (a null dispatcher is the input that fails).
        Assert.Null(PerfDispatcherHooks.Attach(null!));
    }

    [Fact]
    public void TraceDiagMode_ListsEveryDiagVariableUppercasedAndSorted()
    {
        SetEnv("PHOTOREVIEW_DIAG_ZZ_COVTEST", "2");
        SetEnv("photoreview_diag_aa_covtest", "x");
        SetEnv("PHOTOREVIEW_OTHER_COVTEST", "ignored");

        PerfDispatcherHooks.TraceDiagMode();

        var flags = Assert.Single(_listener.Events, e => e.Id == DiagModeId && e.Payload[0] is string s && s.Contains("_COVTEST=", StringComparison.Ordinal)).Payload[0] as string;
        var parts = flags!.Split(';');
        Assert.Equal(parts.OrderBy(p => p, StringComparer.Ordinal), parts);
        Assert.Contains("PHOTOREVIEW_DIAG_AA_COVTEST=x", parts);
        Assert.Contains("PHOTOREVIEW_DIAG_ZZ_COVTEST=2", parts);
        Assert.True(Array.IndexOf(parts, "PHOTOREVIEW_DIAG_AA_COVTEST=x") < Array.IndexOf(parts, "PHOTOREVIEW_DIAG_ZZ_COVTEST=2"));
        Assert.DoesNotContain(parts, p => p.Contains("OTHER", StringComparison.Ordinal));
    }

    private void SetEnv(string name, string value)
    {
        _envToRestore.Add(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    private sealed class DispatcherThread : IDisposable
    {
        private readonly Thread _thread;
        private Win32DialogGuard? _guard;

        public DispatcherThread()
        {
            using var ready = new ManualResetEventSlim();
            Dispatcher? dispatcher = null;
            _thread = new Thread(() =>
            {
                dispatcher = Dispatcher.CurrentDispatcher;
                _guard = Win32DialogGuard.InstallOnCurrentThread();
                ready.Set();
                Dispatcher.Run();
                _guard.Dispose();
            })
            { IsBackground = true };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            ready.Wait();
            Dispatcher = dispatcher!;
        }

        public Dispatcher Dispatcher { get; }

        public void Dispose()
        {
            Dispatcher.InvokeShutdown();
            _thread.Join(Bound);
            var exception = _guard?.CreateException();
            if (exception is not null) throw exception;
        }
    }
}

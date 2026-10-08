using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using PhotoReview.App.Diagnostics;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.Instance;
using PhotoReview.Core.Model;
using PhotoReview.Platform.Windows;
using PhotoReview.TestSupport.Windows;

namespace PhotoReview.App.Tests;

/// <summary>
/// Branch coverage of the forced-log helpers (<c>WriteForced</c>, <c>LogStartupErrorForced</c>, <c>LogUnhandledForced</c>,
/// <c>LogDiagModeForced</c>) and of <c>App.Dispose</c> in <c>App.xaml.cs</c>. <c>App</c> is a WPF <c>Application</c> (one per
/// process), so the Dispose tests create an uninitialised instance and fill the private fields they exercise by reflection.
/// Each test owns a private <see cref="FileLog"/> under a temp data root and restores the process-wide state it touches.
/// </summary>
[Collection("GlobalState")]
[Trait("Category", "HotPath")]
public sealed class AppLogDisposeBranchTests : IDisposable
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);
    private const BindingFlags AnyStatic = BindingFlags.NonPublic | BindingFlags.Static;
    private const BindingFlags AnyInstance = BindingFlags.NonPublic | BindingFlags.Instance;

    private readonly DataRootFixture _data = new();
    private readonly FileLog _log;
    private readonly List<string> _envToRestore = [];

    public AppLogDisposeBranchTests()
    {
        _log = new FileLog(PhotoReview.Core.AppPaths.FromEnvironment());
        AppLog.Instance = _log;
        AppLog.Enabled = false;
    }

    public void Dispose()
    {
        foreach (var name in _envToRestore) Environment.SetEnvironmentVariable(name, null);
        AppLog.Enabled = false;
        AppLog.Instance = null!;
        _log.Dispose();
        _data.Dispose();
    }

    private static App NewUninitialisedApp() => (App)RuntimeHelpers.GetUninitializedObject(typeof(App));

    private static void SetField(App app, string name, object? value) =>
        typeof(App).GetField(name, AnyInstance)!.SetValue(app, value);

    private static object? GetField(App app, string name) => typeof(App).GetField(name, AnyInstance)!.GetValue(app);

    private static void WriteForced(Action write) =>
        typeof(App).GetMethod("WriteForced", AnyStatic)!.Invoke(null, [write]);

    private static string ReadLog() => File.ReadAllText(AppLog.FilePath);

    // ---- LogUnhandledForced ---------------------------------------------------------------------------------------

    private sealed class ThrowingObject
    {
        public override string ToString() => throw new InvalidOperationException("tostring-failed");
    }

    [Fact]
    public void LogUnhandledForced_NonExceptionObject_IsRecordedThroughItsToString()
    {
        App.LogUnhandledForced("non-exception-marker", "plain-object-text");

        var text = ReadLog();
        Assert.Contains("non-exception-marker", text, StringComparison.Ordinal);
        Assert.Contains("plain-object-text", text, StringComparison.Ordinal);
        Assert.False(AppLog.Enabled);
    }

    [Fact]
    public void LogUnhandledForced_NullObject_IsRecordedAsUnknownExceptionObject()
    {
        App.LogUnhandledForced("null-object-marker", null);

        var text = ReadLog();
        Assert.Contains("null-object-marker", text, StringComparison.Ordinal);
        Assert.Contains("unknown exception object", text, StringComparison.Ordinal);
    }

    [Fact]
    public void LogUnhandledForced_ObjectWhoseToStringThrows_DoesNotThrowAndLeavesLoggingAsItWas()
    {
        Assert.False(AppLog.Enabled);

        var thrown = Record.Exception(() => App.LogUnhandledForced("tostring-throws-marker", new ThrowingObject()));

        Assert.Null(thrown);
        Assert.False(AppLog.Enabled);
    }

    // ---- WriteForced -----------------------------------------------------------------------------------------------

    [Fact]
    public void WriteForced_WriteThrows_RethrowsAndStillRestoresTheEnabledFlag()
    {
        Assert.False(AppLog.Enabled);

        var thrown = Assert.Throws<TargetInvocationException>(() => WriteForced(() => throw new InvalidOperationException("write-failed")));

        Assert.IsType<InvalidOperationException>(thrown.InnerException);
        Assert.False(AppLog.Enabled); // the finally block ran
        // The lock was released too: a second forced write is not blocked and works.
        WriteForced(() => AppLog.Info("after-failed-write-marker"));
        Assert.Contains("after-failed-write-marker", ReadLog(), StringComparison.Ordinal);
    }

    [Fact]
    public void WriteForced_LoggingAlreadyEnabled_StaysEnabledAfterwards()
    {
        AppLog.Enabled = true;

        WriteForced(() => AppLog.Info("already-enabled-marker"));

        Assert.True(AppLog.Enabled);
        Assert.Contains("already-enabled-marker", ReadLog(), StringComparison.Ordinal);
    }

    [Fact]
    public void WriteForced_LoggingOff_IsOnWhileWritingAndFlushedBeforeReturning()
    {
        var enabledDuringWrite = false;

        WriteForced(() =>
        {
            enabledDuringWrite = AppLog.Enabled;
            AppLog.Info("flushed-marker");
        });

        Assert.True(enabledDuringWrite);
        Assert.False(AppLog.Enabled);
        // No explicit flush here: WriteForced itself must have flushed.
        Assert.Contains("flushed-marker", ReadLog(), StringComparison.Ordinal);
    }

    [Fact]
    public void LogStartupErrorForced_RecordsMessageAndExceptionWhileLoggingIsOff()
    {
        App.LogStartupErrorForced("startup-error-marker", new InvalidOperationException("startup-detail"));

        var text = ReadLog();
        Assert.Contains("startup-error-marker", text, StringComparison.Ordinal);
        Assert.Contains("startup-detail", text, StringComparison.Ordinal);
        Assert.False(AppLog.Enabled);
    }

    // ---- LogDiagModeForced -----------------------------------------------------------------------------------------

    [Fact]
    public void LogDiagModeForced_WritesTheDiagSummaryEvenWhileLoggingIsOff()
    {
        Assert.False(AppLog.Enabled);

        typeof(App).GetMethod("LogDiagModeForced", AnyStatic)!.Invoke(null, null);

        Assert.Contains("DIAG MODE: " + DiagOptions.Describe(), ReadLog(), StringComparison.Ordinal);
        Assert.False(AppLog.Enabled);
    }

    // ---- Dispose ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Dispose_ShutsTheLogDown_AfterFlushingWhatWasQueued()
    {
        AppLog.Enabled = true;
        AppLog.Info("queued-before-dispose-marker");

        NewUninitialisedApp().Dispose();

        Assert.False(AppLog.Enabled); // Shutdown switches logging off
        Assert.Contains("queued-before-dispose-marker", ReadLog(), StringComparison.Ordinal);
    }

    [Fact]
    public void Dispose_CalledTwice_SecondCallIsANoOp()
    {
        var app = NewUninitialisedApp();
        app.Dispose();

        Assert.Null(Record.Exception(app.Dispose));
    }

    private sealed class DisposeProbe : IDisposable
    {
        public int Disposed;
        public void Dispose() => Interlocked.Increment(ref Disposed);
    }

    [Fact]
    public void Dispose_DisposesTheContainerOnceAndClearsIt()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IDisposable>(_ => new DisposeProbe()); // container-owned
        var provider = services.BuildServiceProvider();
        var owned = (DisposeProbe)provider.GetRequiredService<IDisposable>();
        var app = NewUninitialisedApp();
        SetField(app, "_services", provider);

        app.Dispose();
        app.Dispose();

        Assert.Equal(1, owned.Disposed);
        Assert.Null(GetField(app, "_services"));
    }

    [Fact]
    public async Task Dispose_DisposesTheForwardCoalescerAndClearsIt()
    {
        CancellationToken delayToken = default;
        var delayStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var coalescer = new ForwardedOpenCoalescer(
            _ => { },
            TimeSpan.FromMinutes(5),
            delay: (_, token) =>
            {
                delayToken = token;
                delayStarted.TrySetResult();
                return Task.Delay(Timeout.Infinite, token);
            });
        coalescer.Submit(["a"]);
        await delayStarted.Task.WaitAsync(Bound);
        Assert.False(delayToken.IsCancellationRequested);
        var app = NewUninitialisedApp();
        SetField(app, "_forwardCoalescer", coalescer);

        app.Dispose();

        Assert.True(delayToken.IsCancellationRequested);
        Assert.Null(GetField(app, "_forwardCoalescer"));
    }

    [Fact]
    public void Dispose_ReleasesTheInstanceLockAndClearsTheScope()
    {
        var prefix = "PhotoReviewAppDispose" + Guid.NewGuid().ToString("N");
        var scope = new InstanceScope(InstanceMode.SingleWindow, _ => { }, namePrefix: prefix);
        using var rival = new InstanceScope(InstanceMode.SingleWindow, _ => { }, namePrefix: prefix);
        Assert.True(scope.TryAcquire(null));
        Assert.False(rival.TryAcquire(null)); // the lock is really held
        var app = NewUninitialisedApp();
        SetField(app, "_instanceScope", scope);

        app.Dispose();

        Assert.Null(GetField(app, "_instanceScope"));
        using var successor = new InstanceScope(InstanceMode.SingleWindow, _ => { }, namePrefix: prefix);
        Assert.True(successor.TryAcquire(null)); // released by Dispose
    }

    [Fact]
    public void Dispose_DisposesThePerfCsvListener_ReleasingItsFileAndClearingTheField()
    {
        var dir = Path.Combine(_data.Path, "perf");
        Environment.SetEnvironmentVariable("PHOTOREVIEW_PERF_TRACE", dir);
        _envToRestore.Add("PHOTOREVIEW_PERF_TRACE");
        var listener = PerfCsvListener.TryStartFromEnvironment();
        Assert.NotNull(listener);
        var csv = Assert.Single(Directory.GetFiles(dir, "perf-*.csv"));
        Assert.Throws<IOException>(() => File.Open(csv, FileMode.Open, FileAccess.Write, FileShare.None)); // held while running
        var app = NewUninitialisedApp();
        SetField(app, "_perfListener", listener);

        app.Dispose();

        Assert.Null(GetField(app, "_perfListener"));
        using (File.Open(csv, FileMode.Open, FileAccess.Write, FileShare.None)) { } // released
    }

    // ---- Dispose: perf dispatcher hooks (real dispatcher thread) ----------------------------------------------------

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

    private const int DispatcherLongOpId = 22;

    private static void MarkerA() { }

    private static void MarkerB() { }

    private static async Task RunAndSettleAsync(Dispatcher dispatcher, Action work)
    {
        await dispatcher.InvokeAsync(work, DispatcherPriority.Normal).Task.WaitAsync(Bound);
        // OperationCompleted is raised after the operation's own task completes; a barrier queued behind it runs after the hook.
        await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Normal).Task.WaitAsync(Bound);
    }

    [Fact]
    [Trait("Category", "UI")]
    public async Task Dispose_DetachesThePerfDispatcherHooks()
    {
        using var listener = new CaptureListener();
        using var ui = new DispatcherThreadHost();
        // Threshold below zero: every completed operation is reported while the hooks are attached.
        var hooks = PerfDispatcherHooks.Attach(ui.Dispatcher, longOpThresholdMs: -1);
        Assert.NotNull(hooks);
        await RunAndSettleAsync(ui.Dispatcher, MarkerA);
        Assert.Contains(listener.Events, e => e.Id == DispatcherLongOpId && e.Payload[2] is string n && n.EndsWith(".MarkerA", StringComparison.Ordinal));
        var app = NewUninitialisedApp();
        SetField(app, "_perfHooks", hooks);

        app.Dispose();
        await RunAndSettleAsync(ui.Dispatcher, MarkerB);

        Assert.DoesNotContain(listener.Events, e => e.Id == DispatcherLongOpId && e.Payload[2] is string n && n.EndsWith(".MarkerB", StringComparison.Ordinal));
    }

    private sealed class DispatcherThreadHost : IDisposable
    {
        private readonly Thread _thread;

        public DispatcherThreadHost()
        {
            using var ready = new ManualResetEventSlim();
            Dispatcher? dispatcher = null;
            _thread = new Thread(() =>
            {
                dispatcher = Dispatcher.CurrentDispatcher;
                ready.Set();
                Dispatcher.Run();
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
        }
    }
}

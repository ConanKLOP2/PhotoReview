using System;
using System.Diagnostics;
using System.Threading;
using System.Windows.Threading;

/// <summary>
/// perf(harness, Q-R29 option C): total time the UI thread spent inside dispatcher operations, via
/// <see cref="Dispatcher.Hooks"/>. Only the outermost operation is timed (a nested frame's operations are already
/// inside it), so <see cref="BusyTicks"/> is wall time the dispatcher could not process input or render. Harness-only:
/// attached for one --perf-session iteration, never in the app.
/// </summary>
internal sealed class UiBusyMeter : IDisposable
{
    private readonly DispatcherHooks _hooks;
    private int _depth;
    private long _outerStart;
    private long _busyTicks;

    private UiBusyMeter(DispatcherHooks hooks)
    {
        _hooks = hooks;
        _hooks.OperationStarted += OnStarted;
        _hooks.OperationCompleted += OnEnded;
    }

    /// <summary>Stopwatch ticks spent in (outermost) dispatcher operations since attach.</summary>
    public long BusyTicks => Interlocked.Read(ref _busyTicks);

    /// <summary>Null when the dispatcher's hooks are unavailable (the measurement is then simply omitted).</summary>
    public static UiBusyMeter? Attach(Dispatcher dispatcher)
    {
        try { return new UiBusyMeter(dispatcher.Hooks); }
        catch (InvalidOperationException) { return null; }
    }

    // Started/Completed fire on the dispatcher thread for operations it runs, so no locking is needed for
    // _depth/_outerStart. (Aborted is not hooked: only a still-pending operation can be aborted, it never ran.)
    private void OnStarted(object? sender, DispatcherHookEventArgs e)
    {
        if (_depth++ == 0) _outerStart = Stopwatch.GetTimestamp();
    }

    private void OnEnded(object? sender, DispatcherHookEventArgs e)
    {
        if (_depth == 0) return; // an operation that was already running when the meter attached
        if (--_depth == 0) Interlocked.Add(ref _busyTicks, Stopwatch.GetTimestamp() - _outerStart);
    }

    public void Dispose()
    {
        _hooks.OperationStarted -= OnStarted;
        _hooks.OperationCompleted -= OnEnded;
    }
}

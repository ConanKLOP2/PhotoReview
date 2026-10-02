using System.IO;
using PhotoReview.Core.Abstractions;

namespace PhotoReview.Core.Session;

/// <summary>
/// Debounces <see cref="SessionStore.Save"/>: <see cref="Update"/> only records the latest state per folder and
/// schedules one write on a worker after the debounce window, so a burst of navigation costs one atomic write
/// instead of one per image. <see cref="Flush"/> writes immediately (folder change, shutdown).
/// </summary>
public sealed class SessionWriter : IDisposable
{
    public static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(500);

    /// <summary>Upper bound <see cref="Dispose"/> waits for an in-flight write (Q-R5): the last write is skipped rather than hanging shutdown.</summary>
    private static readonly TimeSpan ShutdownWait = TimeSpan.FromSeconds(2);

    private readonly SessionStore _store;
    private readonly ILog? _log;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeSpan _debounce;
    private readonly object _gate = new();
    private readonly Dictionary<string, SessionState> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _versions = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private CancellationTokenSource? _timerCts;
    private Task _lastRun = Task.CompletedTask;
    private bool _disposed;
    private int _inFlight; // batches drained by a writer (timer thread included) that are not yet written; guarded by _gate

    /// <param name="delay">Injectable timer; defaults to <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.</param>
    public SessionWriter(SessionStore store, ILog? log = null, TimeSpan? debounce = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _log = log;
        _debounce = debounce ?? DefaultDebounce;
        _delay = delay ?? Task.Delay;
    }

    /// <summary>Records <paramref name="state"/> (copied, so later mutation by the caller is not observed) for a debounced write.</summary>
    public void Update(SessionState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(state.Folder);
        var snapshot = new SessionState
        {
            Folder = state.Folder,
            CurrentPath = state.CurrentPath,
            Skipped = state.Skipped is null ? [] : [.. state.Skipped],
            UpdatedUtc = state.UpdatedUtc,
        };
        lock (_gate)
        {
            if (_disposed) return;
            var key = KeyOf(snapshot.Folder);
            _versions.TryGetValue(key, out var version);
            _versions[key] = version + 1;
            _pending[key] = snapshot;
            if (_timerCts is not null) return; // a write is already scheduled; it will pick up the latest state
            var cts = new CancellationTokenSource();
            _timerCts = cts;
            _lastRun = RunTimerAsync(cts);
        }
    }

    /// <summary>Same identity as the session file name, so a folder with and without a trailing separator is one pending entry (newest wins).</summary>
    private static string KeyOf(string folder)
    {
        try { return SessionStore.CanonicalFolder(folder); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return folder; }
    }

    /// <summary>Writes every pending state now and cancels the scheduled write.</summary>
    public void Flush() => Flush(bounded: true); // UI paths (folder change) must not hang behind a stuck disk write

    private void Flush(bool bounded)
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            cts = _timerCts;
            _timerCts = null;
        }
        cts?.Cancel();
        WritePending(bounded);
    }

    public Task FlushAsync() => Task.Run(Flush);

    /// <summary>Completes when the most recently scheduled debounced write has finished (for tests and shutdown).</summary>
    public Task WhenIdleAsync()
    {
        lock (_gate) return _lastRun;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        var started = System.Diagnostics.Stopwatch.StartNew();
        Flush(bounded: true);
        WaitForInFlightWrites(ShutdownWait - started.Elapsed);
        // _writeLock is intentionally NOT disposed: SemaphoreSlim holds no unmanaged resources unless
        // AvailableWaitHandle is used, and a timer thread may have drained its batch but not yet reached Wait();
        // disposing here made that Wait() throw ObjectDisposedException and lose the last write (R2-A-04).
    }

    /// <summary>
    /// The timer thread may have drained the pending states and not written them yet, so <see cref="Flush"/> sees an empty
    /// batch; wait (bounded) for it so the process does not exit under the last write.
    /// </summary>
    private void WaitForInFlightWrites(TimeSpan timeout)
    {
        lock (_gate)
        {
            if (_inFlight == 0) return;
            BeforeShutdownWaitForTests?.Invoke();
            var deadline = DateTime.UtcNow + timeout;
            while (_inFlight > 0)
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    _log?.Error("Session write may be lost at shutdown: a write was still in flight", null);
                    return;
                }
                Monitor.Wait(_gate, remaining);
            }
        }
    }

    private async Task RunTimerAsync(CancellationTokenSource cts)
    {
        try { await _delay(_debounce, cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        lock (_gate)
        {
            if (!ReferenceEquals(_timerCts, cts)) return; // flushed while we were waiting
            _timerCts = null;
        }
        WritePending(bounded: false);
    }

    /// <summary>How long a bounded flush (<see cref="Flush()"/>, <see cref="Dispose"/>) waits for the writer lock; a test seam so the timeout case does not take seconds.</summary>
    internal TimeSpan BoundedWaitForTests { get; set; } = ShutdownWait;

    /// <summary>Test seam: runs after a non-empty batch was drained and before the write lock is taken.</summary>
    internal Action? AfterDrainForTests { get; set; }

    /// <summary>Test seam: runs in <see cref="Dispose"/> (under the gate) when it is about to wait for an in-flight write.</summary>
    internal Action? BeforeShutdownWaitForTests { get; set; }

    private void WritePending(bool bounded)
    {
        List<(SessionState State, long Version)> batch;
        lock (_gate)
        {
            batch = _pending.Select(pair =>
            {
                _versions.TryGetValue(pair.Key, out var version);
                return (pair.Value, version);
            }).ToList();
            _pending.Clear();
            if (batch.Count > 0) _inFlight++;
        }
        if (batch.Count == 0) return;
        try
        {
            AfterDrainForTests?.Invoke();
            WriteBatch(batch, bounded);
        }
        finally
        {
            lock (_gate)
            {
                _inFlight--;
                Monitor.PulseAll(_gate);
            }
        }
    }

    // A bounded wait that timed out must not lose the state: put it back (unless a newer snapshot arrived) so the next flush writes it.
    private void RequeueSkipped(List<(SessionState State, long Version)> batch)
    {
        lock (_gate)
        {
            if (_disposed) return;
            foreach (var (state, version) in batch)
            {
                var key = KeyOf(state.Folder);
                if (_versions.TryGetValue(key, out var current) && current == version) _pending.TryAdd(key, state);
            }
        }
    }

    private void WriteBatch(List<(SessionState State, long Version)> batch, bool bounded)
    {
        if (batch.Count == 0) return;
        if (bounded)
        {
            if (!_writeLock.Wait(BoundedWaitForTests))
            {
                _log?.Error("Session write skipped: writer busy", null);
                RequeueSkipped(batch);
                return;
            }
        }
        else
        {
            _writeLock.Wait();
        }
        try
        {
            foreach (var (state, version) in batch)
            {
                // A newer snapshot may have arrived while this batch was waiting for
                // the writer lock. Do not let an obsolete batch overwrite it.
                lock (_gate)
                {
                    if (!_versions.TryGetValue(KeyOf(state.Folder), out var current) || current != version)
                        continue;
                }
                try { _store.Save(state); }
                catch (Exception ex)
                {
                    // Best-effort like the rest of session persistence: a failed write must not break navigation, and any
                    // exception type (not only IO) must not drop the rest of the drained batch or fault the timer task.
                    _log?.Error($"Session write failed: {state.Folder}", ex);
                }
            }
        }
        finally { _writeLock.Release(); }
    }
}

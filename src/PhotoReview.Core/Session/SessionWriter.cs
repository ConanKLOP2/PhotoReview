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
            Skipped = [.. state.Skipped],
            UpdatedUtc = state.UpdatedUtc,
        };
        lock (_gate)
        {
            if (_disposed) return;
            _versions.TryGetValue(snapshot.Folder, out var version);
            _versions[snapshot.Folder] = version + 1;
            _pending[snapshot.Folder] = snapshot;
            if (_timerCts is not null) return; // a write is already scheduled; it will pick up the latest state
            var cts = new CancellationTokenSource();
            _timerCts = cts;
            _lastRun = RunTimerAsync(cts);
        }
    }

    /// <summary>Writes every pending state now and cancels the scheduled write.</summary>
    public void Flush() => Flush(bounded: false);

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
        Flush(bounded: true);
        // A skipped (timed-out) write leaves its holder running; that holder still releases the semaphore, so it is
        // only disposed when nothing is in flight.
        if (_writeLock.CurrentCount == 1) _writeLock.Dispose();
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
        }
        WriteBatch(batch, bounded);
    }

    private void WriteBatch(List<(SessionState State, long Version)> batch, bool bounded)
    {
        if (batch.Count == 0) return;
        if (bounded)
        {
            if (!_writeLock.Wait(ShutdownWait))
            {
                _log?.Error("Session write skipped at shutdown: writer busy", null);
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
                    if (!_versions.TryGetValue(state.Folder, out var current) || current != version)
                        continue;
                }
                try { _store.Save(state); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Best-effort like the rest of session persistence: a failed write must not break navigation.
                    _log?.Error($"Session write failed: {state.Folder}", ex);
                }
            }
        }
        finally { _writeLock.Release(); }
    }
}

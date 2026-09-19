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

    private readonly SessionStore _store;
    private readonly ILog? _log;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeSpan _debounce;
    private readonly object _gate = new();
    private readonly Dictionary<string, SessionState> _pending = new(StringComparer.OrdinalIgnoreCase);
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
            if (_disposed) { WriteBatch([snapshot]); return; }
            _pending[snapshot.Folder] = snapshot;
            if (_timerCts is not null) return; // a write is already scheduled; it will pick up the latest state
            var cts = new CancellationTokenSource();
            _timerCts = cts;
            _lastRun = RunTimerAsync(cts);
        }
    }

    /// <summary>Writes every pending state now and cancels the scheduled write.</summary>
    public void Flush()
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            cts = _timerCts;
            _timerCts = null;
        }
        cts?.Cancel();
        WritePending();
    }

    public Task FlushAsync() => Task.Run(Flush);

    /// <summary>Completes when the most recently scheduled debounced write has finished (for tests and shutdown).</summary>
    public Task WhenIdleAsync()
    {
        lock (_gate) return _lastRun;
    }

    public void Dispose()
    {
        lock (_gate) _disposed = true;
        Flush();
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
        WritePending();
    }

    private void WritePending()
    {
        List<SessionState> batch;
        lock (_gate)
        {
            batch = [.. _pending.Values];
            _pending.Clear();
        }
        WriteBatch(batch);
    }

    private void WriteBatch(List<SessionState> batch)
    {
        if (batch.Count == 0) return;
        _writeLock.Wait();
        try
        {
            foreach (var state in batch)
            {
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

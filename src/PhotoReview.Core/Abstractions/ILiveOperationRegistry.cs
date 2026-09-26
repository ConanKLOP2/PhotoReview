namespace PhotoReview.Core.Abstractions;

/// <summary>
/// Q-R27 (decision D): a "this journal operation is executing right now" marker that every PhotoReview process sharing
/// one <c>operations.jsonl</c> can see. A file action holds the marker for its journal Id from just before its Prepared
/// entry is appended until its outcome (Committed/Failed) is appended; startup reconcile skips a pending entry whose
/// marker is live instead of judging it mid-flight. The Windows implementation is an OS named object, which the kernel
/// closes when the owning process dies, so a crashed process's leftovers are still reconciled.
/// </summary>
public interface ILiveOperationRegistry
{
    /// <summary>
    /// Marks <paramref name="operationId"/> live until the returned handle is disposed. Must not throw: when the marker
    /// cannot be created the implementation logs and returns a handle that marks nothing (reconcile then behaves as
    /// before Q-R27), so a marker failure never fails the file action.
    /// </summary>
    IDisposable Begin(string operationId);

    /// <summary>True while some handle from <see cref="Begin"/> for <paramref name="operationId"/> is undisposed (in any process for the OS implementation).</summary>
    bool IsLive(string operationId);
}

/// <summary>
/// Process-local <see cref="ILiveOperationRegistry"/>: the default when no OS registry is injected (tests, tools).
/// Thread-safe; one Id may be begun more than once (reference counted).
/// </summary>
public sealed class InProcessLiveOperationRegistry : ILiveOperationRegistry
{
    private readonly Dictionary<string, int> _live = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public IDisposable Begin(string operationId)
    {
        ArgumentException.ThrowIfNullOrEmpty(operationId);
        lock (_gate)
        {
            _live[operationId] = _live.TryGetValue(operationId, out var count) ? count + 1 : 1;
        }
        return new Marker(this, operationId);
    }

    public bool IsLive(string operationId)
    {
        if (string.IsNullOrEmpty(operationId)) return false;
        lock (_gate)
        {
            return _live.ContainsKey(operationId);
        }
    }

    private void End(string operationId)
    {
        lock (_gate)
        {
            if (!_live.TryGetValue(operationId, out var count)) return;
            if (count <= 1) _live.Remove(operationId);
            else _live[operationId] = count - 1;
        }
    }

    private sealed class Marker(InProcessLiveOperationRegistry owner, string operationId) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.End(operationId);
        }
    }
}

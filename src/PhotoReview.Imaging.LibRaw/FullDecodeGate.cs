using PhotoReview.Core.Abstractions;
using PhotoReview.Imaging.Decoding;

namespace PhotoReview.Imaging.LibRaw;

/// <summary>
/// Single-slot gate serialising LibRaw full decodes (each holds hundreds of MB), with two lanes: waiting
/// <see cref="SourceReadPriority.Viewer"/> decodes are served before waiting <see cref="SourceReadPriority.Preload"/> decodes,
/// except that a preload waiter which has seen <c>maxPreloadAgeCompletions</c> decodes finish since it queued is promoted
/// ahead of newer viewer waiters (aging: a user paging through RAWs must not starve queued preloads forever). A running decode is never interrupted, the number of queued preload
/// waiters is bounded (extra preload requests fail fast so worker threads are released instead of piling up), and a
/// queued waiter is cancellable without disturbing the slot. Every acquisition returns a lease that releases exactly once.
/// </summary>
internal sealed class FullDecodeGate
{
    private readonly object _sync = new();
    private readonly LinkedList<Waiter> _viewerQueue = new();
    private readonly LinkedList<Waiter> _preloadQueue = new();
    private readonly int _maxQueuedPreloads;
    private readonly Action<SourceReadPriority>? _waiterQueued;
    private readonly int _maxPreloadAgeCompletions;
    private long _completions; // decodes finished so far (lease disposals); the aging clock, no wall time involved
    private bool _busy;

    internal const int DefaultMaxPreloadAgeCompletions = 3;

    internal FullDecodeGate(int maxQueuedPreloads, Action<SourceReadPriority>? waiterQueued = null, int maxPreloadAgeCompletions = DefaultMaxPreloadAgeCompletions)
    {
        _maxQueuedPreloads = maxQueuedPreloads;
        _maxPreloadAgeCompletions = Math.Max(1, maxPreloadAgeCompletions);
        _waiterQueued = waiterQueued;
    }

    /// <summary>1 when the slot is free, 0 while a decode holds it.</summary>
    internal int SlotsAvailable
    {
        get { lock (_sync) return _busy ? 0 : 1; }
    }

    internal int QueuedViewers { get { lock (_sync) return _viewerQueue.Count; } }
    internal int QueuedPreloads { get { lock (_sync) return _preloadQueue.Count; } }

    /// <summary>Blocks until the slot is granted. Throws <see cref="OperationCanceledException"/> (slot untouched) or <see cref="DecoderBusyException"/> (preload queue full).</summary>
    internal Lease Enter(SourceReadPriority priority, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Waiter waiter;
        lock (_sync)
        {
            if (!_busy)
            {
                _busy = true;
                return new Lease(this);
            }

            var preload = priority == SourceReadPriority.Preload;
            if (preload && _preloadQueue.Count >= _maxQueuedPreloads) throw new DecoderBusyException("The RAW full-decode queue is busy; this background decode was skipped.");
            waiter = new Waiter { EnqueuedAtCompletion = _completions };
            var queue = preload ? _preloadQueue : _viewerQueue;
            waiter.Queue = queue;
            waiter.Node = queue.AddLast(waiter);
        }

        _waiterQueued?.Invoke(priority);
        try
        {
            waiter.Signal.Wait(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            lock (_sync)
            {
                if (waiter.Granted)
                {
                    // The slot was handed to this waiter in the same instant as the cancellation: pass it on so it is not lost.
                    ReleaseLocked();
                }
                else
                {
                    waiter.Queue!.Remove(waiter.Node!); // leaves the queue at once so it no longer counts toward the preload bound
                }
            }

            throw;
        }

        return new Lease(this);
    }

    private void Release()
    {
        lock (_sync)
        {
            _completions++;
            ReleaseLocked();
        }
    }

    private void ReleaseLocked()
    {
        var preloadIsOverdue = _preloadQueue.First is { } oldest && _completions - oldest.Value.EnqueuedAtCompletion >= _maxPreloadAgeCompletions;
        var next = preloadIsOverdue
            ? Dequeue(_preloadQueue)
            : Dequeue(_viewerQueue) ?? Dequeue(_preloadQueue);
        if (next is null)
        {
            _busy = false;
            return;
        }

        next.Granted = true; // the slot stays busy: ownership moves straight to the waiter
        next.Signal.Release();
    }

    private static Waiter? Dequeue(LinkedList<Waiter> queue)
    {
        var first = queue.First;
        if (first is null) return null;
        queue.RemoveFirst();
        return first.Value;
    }

    private sealed class Waiter
    {
        internal SemaphoreSlim Signal { get; } = new(0, 1);
        internal bool Granted { get; set; }
        internal long EnqueuedAtCompletion { get; init; }
        internal LinkedList<Waiter>? Queue { get; set; }
        internal LinkedListNode<Waiter>? Node { get; set; }
    }

    /// <summary>Holds the slot; disposing more than once releases only once.</summary>
    internal sealed class Lease : IDisposable
    {
        private FullDecodeGate? _gate;

        internal Lease(FullDecodeGate gate) => _gate = gate;

        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }
}

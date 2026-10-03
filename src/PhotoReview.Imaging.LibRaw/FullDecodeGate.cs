using PhotoReview.Core.Abstractions;
using PhotoReview.Imaging.Decoding;

namespace PhotoReview.Imaging.LibRaw;

/// <summary>
/// Single-slot gate serialising LibRaw full decodes (each holds hundreds of MB), with two lanes: waiting
/// <see cref="SourceReadPriority.Viewer"/> decodes are served before waiting <see cref="SourceReadPriority.Preload"/> decodes,
/// except that a preload waiter which has seen <c>MaxPreloadAgeCompletions</c> decodes FINISH since it queued (and since the last
/// preload promotion) is promoted ahead of newer viewer waiters (aging: a user paging through RAWs must not starve queued preloads
/// forever). Only leases marked with <see cref="Lease.MarkWorked"/> (a decode that really started unpacking) advance that clock, so
/// instant failures (memory refusal, corrupt file, cancelled open) cannot promote a preload without any real work in between, and at
/// most ONE preload is promoted per <c>MaxPreloadAgeCompletions</c> worked completions. A running decode is never interrupted, the number of queued preload
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
    private long _completions; // decodes that really ran (worked lease disposals); the aging clock, no wall time involved
    private long _lastPromotionCompletion; // value of _completions at the last aged preload promotion: the aging baseline for the next waiter
    private bool _busy;

    private const int MaxPreloadAgeCompletions = 3;

    internal FullDecodeGate(int maxQueuedPreloads, Action<SourceReadPriority>? waiterQueued = null)
    {
        _maxQueuedPreloads = maxQueuedPreloads;
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

        try
        {
            // The test seam runs inside the guarded region: if it throws, the queued waiter is removed (or a racing grant is passed on)
            // exactly like a cancellation, so the queue entry cannot be orphaned and _busy cannot stay stuck.
            _waiterQueued?.Invoke(priority);
            waiter.Signal.Wait(cancellationToken);
        }
        catch (Exception)
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

    private void Release(bool worked)
    {
        lock (_sync)
        {
            if (worked) _completions++;
            ReleaseLocked();
        }
    }

    private void ReleaseLocked()
    {
        // One promotion per N worked completions: a promotion moves the baseline, so the other queued preload (stamped at almost the
        // same completion) has to age again instead of following right behind.
        var preloadIsOverdue = _preloadQueue.First is { } oldest &&
            _completions - Math.Max(oldest.Value.EnqueuedAtCompletion, _lastPromotionCompletion) >= MaxPreloadAgeCompletions;
        if (preloadIsOverdue) _lastPromotionCompletion = _completions;
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
        private int _worked;

        internal Lease(FullDecodeGate gate) => _gate = gate;

        /// <summary>Declares that this lease really ran a decode (past the memory guard, unpack started): its disposal then advances the aging clock.</summary>
        internal void MarkWorked() => Volatile.Write(ref _worked, 1);

        /// <summary>Test seam: whether <see cref="MarkWorked"/> was called.</summary>
        internal bool IsWorked => Volatile.Read(ref _worked) != 0;

        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release(Volatile.Read(ref _worked) != 0);
    }
}

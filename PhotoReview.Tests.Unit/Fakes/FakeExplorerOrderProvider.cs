using PhotoReview.App;
using PhotoReview.Core.Catalog;

namespace PhotoReview.Tests.Unit.Fakes;

/// <summary>
/// Substitute for <see cref="ExplorerOrderService"/> behind <c>MainWindowTestHooks.Explorer</c>
/// (T14a seam). It returns a caller-supplied order — typically the reverse of the natural sort —
/// so a test can tell "Explorer order was applied" from "Explorer order was ignored" (INV-7) and
/// can observe whether the first frame was presented before or after the snapshot arrived (INV-9).
/// <para>
/// The snapshot is released only when <b>both</b> conditions hold: the configured wall-clock delay
/// has elapsed <b>and</b> <see cref="Release"/> has been called. The delay alone would make the
/// tests a race between machine speed and the app's folder-load latency (the test has to get the
/// user interaction in before the snapshot lands); the explicit gate makes the ordering a fact
/// instead of a hope, while the delay still reproduces the task's "snapshot arrives late" shape.
/// </para>
/// </summary>
internal sealed class FakeExplorerOrderProvider : IProgressiveExplorerOrderProvider
{
    private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string[] _orderedPaths;
    private readonly TimeSpan _delay;
    private int _callCount;
    private int _disposeCount;
    private volatile bool _hasReturned;

    /// <param name="orderedPaths">The order the fake Explorer view reports.</param>
    /// <param name="delay">Minimum time the call blocks before returning. Defaults to none.</param>
    public FakeExplorerOrderProvider(IEnumerable<string> orderedPaths, TimeSpan? delay = null)
    {
        ArgumentNullException.ThrowIfNull(orderedPaths);
        _orderedPaths = [.. orderedPaths];
        _delay = delay ?? TimeSpan.Zero;
    }

    /// <summary>How many times MainWindow asked for a snapshot (one per folder load).</summary>
    public int CallCount => Volatile.Read(ref _callCount);

    /// <summary>The folder of the most recent call, as MainWindow canonicalized it.</summary>
    public string? RequestedFolder { get; private set; }

    /// <summary>True once a call has produced its snapshot. Set before the task completes.</summary>
    public bool HasReturned => _hasReturned;

    /// <summary>Window_Closed must still dispose the provider through the seam.</summary>
    public int DisposeCount => Volatile.Read(ref _disposeCount);

    /// <summary>Lets the pending (and any later) snapshot call finish.</summary>
    public void Release() => _gate.TrySetResult();

    public async Task<ExplorerViewSnapshot> TryGetSnapshotProgressiveAsync(string folder, TimeSpan timeout,
        CancellationToken cancellationToken, IProgress<ExplorerQueryProgress>? progress = null, int batchSize = 16)
    {
        Interlocked.Increment(ref _callCount);
        RequestedFolder = folder;
        await Task.WhenAll(
            Task.Delay(_delay, cancellationToken),
            _gate.Task.WaitAsync(cancellationToken)).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new ExplorerQueryProgress(_orderedPaths.Length, _orderedPaths.Length, 1));
        // Written before the returned task completes, so a continuation that observes the result
        // always observes this flag as true.
        _hasReturned = true;
        return new ExplorerViewSnapshot(folder, _orderedPaths, [], ExplorerGroupState.None,
            ExplorerOrderStatus.Available, null, DateTime.UtcNow);
    }

    public void Dispose()
    {
        Interlocked.Increment(ref _disposeCount);
        // A disposed provider must never strand an in-flight folder load on a gate nobody will open.
        _gate.TrySetResult();
    }
}

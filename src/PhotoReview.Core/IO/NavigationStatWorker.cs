namespace PhotoReview.Core.IO;

/// <summary>
/// Q-R29 option C: runs the navigation path's file-metadata calls (the per-navigation stat that decides
/// missing / unreadable / changed, see <c>ImagePresenter</c>) on one dedicated background thread, so a
/// slow share (NAS / wifi: 2-30 ms per metadata call, or a stalled SMB request) never blocks the UI thread. Lives in Core, not the
/// UI-affine App layer: it owns a thread that blocks by design (ADR 0005 forbids blocking waits in App).
/// </summary>
/// <remarks>
/// A dedicated thread rather than the thread pool: the whole-folder preload keeps up to 8 pool threads busy with
/// blocking decodes, and the current image's stat must not queue behind them or behind pool growth (the same
/// reason the viewer decode has its own lane in <c>PreviewImageService</c>). Work items run in FIFO order; one
/// whose <see cref="CancellationToken"/> is already cancelled when it is dequeued (the navigation was superseded
/// while it waited) is dropped without touching the disk, so a key-held burst never pays for stale stats.
/// Completions run their continuations asynchronously: the awaiting code never runs on this thread, which stays
/// free for the next stat. The thread starts on first use and is a background thread for the process lifetime
/// (it owns nothing disposable, so the shared instance needs no shutdown).
/// </remarks>
public sealed class NavigationStatWorker
{
    /// <summary>The process-wide worker used by <c>ImagePresenter</c>.</summary>
    public static NavigationStatWorker Shared { get; } = new();

    private readonly Queue<Action> _queue = new();
    private readonly object _gate = new();
    private Thread? _thread;

    /// <summary>
    /// Runs <paramref name="work"/> on the worker thread. The task is cancelled (without running the work) when
    /// <paramref name="cancellationToken"/> is cancelled before the item is dequeued; exceptions thrown by the work
    /// fault the task.
    /// </summary>
    public Task<T> RunAsync<T>(Func<T> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (cancellationToken.IsCancellationRequested)
        {
            completion.TrySetCanceled(cancellationToken);
            return completion.Task;
        }

        void Execute()
        {
            if (cancellationToken.IsCancellationRequested)
            {
                completion.TrySetCanceled(cancellationToken);
                return;
            }
            try { completion.TrySetResult(work()); }
            catch (Exception ex) { completion.TrySetException(ex); }
        }

        lock (_gate)
        {
            _queue.Enqueue(Execute);
            if (_thread is null)
            {
                _thread = new Thread(Run) { IsBackground = true, Name = "PhotoReview navigation stat" };
                _thread.Start();
            }
            else
            {
                Monitor.Pulse(_gate);
            }
        }
        return completion.Task;
    }

    private void Run()
    {
        // Above normal, like the viewer decode lane: a stat is a short I/O wait, and the current image's stat must
        // not wait for CPU behind the (normal-priority) preload decodes.
        Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;
        while (true)
        {
            Action next;
            lock (_gate)
            {
                while (_queue.Count == 0) Monitor.Wait(_gate);
                next = _queue.Dequeue();
            }
            next(); // never throws: Execute routes every exception into its task
        }
    }
}

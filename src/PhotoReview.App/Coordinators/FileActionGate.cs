using System;
using System.Threading;
using System.Threading.Tasks;

namespace PhotoReview.App.Coordinators;

/// <summary>
/// OC14: the single mutual-exclusion gate for file actions (Move/Copy/Recycle) and Undo.
/// Every entry point (keyboard, context menu, buttons, tests) reaches it through MainViewModel, so
/// window code-behind only forwards commands. A second caller while the gate is held is a no-op;
/// the gate is always released, including when the guarded work throws.
/// </summary>
public sealed class FileActionGate
{
    private readonly object _releasedGate = new();
    private int _held;
    private TaskCompletionSource? _released;

    /// <summary>True while a file action or undo holds the gate.</summary>
    public bool IsHeld => Volatile.Read(ref _held) != 0;

    /// <summary>Acquires the gate; false when it is already held.</summary>
    public bool TryEnter() => Interlocked.CompareExchange(ref _held, 1, 0) == 0;

    /// <summary>Releases the gate and completes any <see cref="WhenReleasedAsync"/> waiter.</summary>
    public void Exit()
    {
        TaskCompletionSource? released;
        lock (_releasedGate)
        {
            Volatile.Write(ref _held, 0);
            released = _released;
            _released = null;
        }
        released?.TrySetResult();
    }

    /// <summary>R7-7: completes when the gate is next released (already completed when it is not held).</summary>
    public Task WhenReleasedAsync()
    {
        lock (_releasedGate)
        {
            if (!IsHeld) return Task.CompletedTask;
            return (_released ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }
    }

    /// <summary>
    /// Runs <paramref name="work"/> while holding the gate. Returns false (work not run) when the
    /// gate is already held.
    /// </summary>
    public async Task<bool> RunExclusiveAsync(Func<Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (!TryEnter()) return false;
        try { await work(); return true; }
        finally { Exit(); }
    }
}

using System;
using System.Threading.Tasks;

namespace PhotoReview.App.Coordinators;

/// <summary>
/// OC14: the single mutual-exclusion gate for file actions (Move/Copy/Recycle) and Undo/duplicate
/// cleanup. Every entry point (keyboard, context menu, buttons, tests) reaches it through
/// MainViewModel, so window code-behind only forwards commands.
///
/// Two ways to hold the gate, both mutually exclusive with each other:
/// <list type="bullet">
/// <item><description><see cref="RunExclusiveAsync"/> (Undo, duplicate cleanup): a second caller
/// while the gate is held -- by either mechanism -- is a no-op (work not run, <c>false</c> returned).
/// Undoing or scanning for duplicates while a Move/Delete is queued/running, or while another Undo/
/// duplicate scan is in flight, would race the same catalog/history state, so it is refused rather
/// than queued.</description></item>
/// <item><description><see cref="RunQueuedAsync"/> (Q-T1: Move/Delete/Copy review actions): a second
/// caller while another queued action is running is QUEUED, not dropped -- every submitted action
/// runs exactly once, one at a time, in the order it was submitted, so holding down the Move/Delete
/// key steps through several photos instead of silently losing key-repeats. It still refuses (no-op)
/// while <see cref="RunExclusiveAsync"/> holds the gate: an in-flight Undo/duplicate scan must finish
/// (or be left alone) before new Move/Delete actions run against a catalog it might still change.
/// A failure in one queued action does not cancel the ones behind it: each action's own exception
/// propagates only through THAT action's own returned task.</description></item>
/// </list>
/// The gate is always released, including when the guarded work throws.
/// </summary>
public sealed class FileActionGate
{
    private readonly object _lock = new();
    private int _held;
    private int _queueLength;
    private Task _tail = Task.CompletedTask;
    private TaskCompletionSource? _released;

    /// <summary>True while an exclusive holder (<see cref="TryEnter"/>/<see cref="RunExclusiveAsync"/>) holds the gate, or a queued action (<see cref="RunQueuedAsync"/>) is pending/running.</summary>
    public bool IsHeld
    {
        get { lock (_lock) return _held != 0 || _queueLength != 0; }
    }

    /// <summary>Acquires the gate exclusively; false when it is already held by either mechanism.</summary>
    public bool TryEnter()
    {
        lock (_lock)
        {
            if (_held != 0 || _queueLength != 0) return false;
            _held = 1;
            return true;
        }
    }

    /// <summary>Releases the gate taken by <see cref="TryEnter"/> and wakes any <see cref="WhenReleasedAsync"/> waiter once nothing else holds it.</summary>
    public void Exit()
    {
        TaskCompletionSource? released;
        lock (_lock)
        {
            _held = 0;
            released = TryTakeReleasedWaiterLocked();
        }
        released?.TrySetResult();
    }

    /// <summary>R7-7: completes when the gate is next fully released -- no exclusive holder and an empty action queue (already completed when neither applies).</summary>
    public Task WhenReleasedAsync()
    {
        lock (_lock)
        {
            if (_held == 0 && _queueLength == 0) return Task.CompletedTask;
            return (_released ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }
    }

    /// <summary>
    /// Runs <paramref name="work"/> exclusively (Undo, duplicate cleanup): false (work not run) when
    /// the gate is already held, by either an exclusive holder or a queued action.
    /// </summary>
    public async Task<bool> RunExclusiveAsync(Func<Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (!TryEnter()) return false;
        try { await work(); return true; }
        finally { Exit(); }
    }

    /// <summary>
    /// Q-T1: queues <paramref name="work"/> behind every previously queued action and runs it once
    /// its turn comes (FIFO); returns false immediately (work not run, nothing queued) only when an
    /// exclusive holder (<see cref="TryEnter"/>/<see cref="RunExclusiveAsync"/>) currently holds the
    /// gate. Once accepted into the queue, the action always eventually runs -- it is never dropped.
    /// </summary>
    /// <remarks>
    /// RV-A01: <paramref name="work"/> is never started under <see cref="_lock"/>. The action's queue
    /// slot (<see cref="_tail"/>) is published under the lock first, and only then -- outside it --
    /// does the work start (still synchronously in the caller when the queue is idle, so a
    /// confirmation dialog appears without an extra dispatcher hop). A modal shown in the work's
    /// synchronous prefix runs a nested dispatcher loop; a second action submitted from that loop
    /// therefore sees this action's slot and waits for it (FIFO, INV-4) instead of re-entering the
    /// Monitor and running concurrently, and other threads never block on the gate meanwhile.
    /// </remarks>
    public Task<bool> RunQueuedAsync(Func<Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        Task previous;
        TaskCompletionSource turnDone;
        lock (_lock)
        {
            if (_held != 0) return Task.FromResult(false);
            _queueLength++;
            previous = _tail;
            // Completed (always with success) once this action has finished and left the queue. It only
            // orders the queue; the caller observes the action's own task below, which carries its
            // result/exception/cancellation exactly. Asynchronous continuations: the next action never
            // starts inline inside this one's finally.
            turnDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _tail = turnDone.Task;
        }
        return RunAfterAsync(previous, work, turnDone);
    }

    private async Task<bool> RunAfterAsync(Task previous, Func<Task> work, TaskCompletionSource turnDone)
    {
        try
        {
            // Wait our turn. `previous` is the predecessor's turnDone task, which only ever completes
            // successfully (its own exception propagates through ITS OWN returned task), so a failed
            // action never stops the queue.
            await previous;
            await work();
            return true;
        }
        finally
        {
            TaskCompletionSource? released;
            lock (_lock)
            {
                _queueLength--;
                released = TryTakeReleasedWaiterLocked();
            }
            turnDone.TrySetResult();
            released?.TrySetResult();
        }
    }

    /// <summary>Call only while holding <see cref="_lock"/>. Returns the waiter to complete, or null while still held.</summary>
    private TaskCompletionSource? TryTakeReleasedWaiterLocked()
    {
        if (_held != 0 || _queueLength != 0) return null;
        var waiter = _released;
        _released = null;
        return waiter;
    }
}

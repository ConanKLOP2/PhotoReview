using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PhotoReview.App.Coordinators;
using Xunit;

namespace PhotoReview.App.Tests.Coordinators;

public sealed class FileActionGateTests
{
    [Fact]
    public void TryEnter_IsExclusive_UntilExit()
    {
        var gate = new FileActionGate();
        Assert.True(gate.TryEnter());
        Assert.True(gate.IsHeld);
        Assert.False(gate.TryEnter());
        gate.Exit();
        Assert.False(gate.IsHeld);
        Assert.True(gate.TryEnter());
    }

    [Fact]
    public async Task RunExclusiveAsync_SecondCallWhileHeld_IsNoOp()
    {
        var gate = new FileActionGate();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runs = 0;

        var first = gate.RunExclusiveAsync(async () => { runs++; await release.Task; });
        var second = await gate.RunExclusiveAsync(() => { runs++; return Task.CompletedTask; });

        Assert.False(second);
        Assert.True(gate.IsHeld);
        release.SetResult();
        Assert.True(await first);
        Assert.Equal(1, runs);
        Assert.False(gate.IsHeld);
    }

    [Fact(DisplayName = "Q-T1: RunQueuedAsync queues a second call while the first is running (does not drop it)")]
    public async Task RunQueuedAsync_SecondCallWhileRunning_IsQueuedNotDropped()
    {
        var gate = new FileActionGate();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new List<int>();

        var first = gate.RunQueuedAsync(async () => { order.Add(1); await release.Task; });
        var second = gate.RunQueuedAsync(() => { order.Add(2); return Task.CompletedTask; });

        // The second action must not have run yet: it is queued behind the first, which is still
        // blocked on `release`, so its task cannot legitimately be complete at this point.
        Assert.False(second.IsCompleted);
        Assert.True(gate.IsHeld);

        release.SetResult();
        Assert.True(await first);
        Assert.True(await second);
        Assert.Equal([1, 2], order);
        Assert.False(gate.IsHeld);
    }

    [Fact(DisplayName = "Q-T1: RunQueuedAsync is a no-op while an exclusive holder (RunExclusiveAsync/TryEnter) holds the gate")]
    public async Task RunQueuedAsync_WhileExclusivelyHeld_IsNoOp()
    {
        var gate = new FileActionGate();
        Assert.True(gate.TryEnter());

        var queued = await gate.RunQueuedAsync(() => Task.CompletedTask);

        Assert.False(queued);
        gate.Exit();
    }

    [Fact(DisplayName = "Q-T1: RunExclusiveAsync is a no-op while a queued action is pending/running")]
    public async Task RunExclusiveAsync_WhileQueuedActionPending_IsNoOp()
    {
        var gate = new FileActionGate();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var queued = gate.RunQueuedAsync(() => release.Task);
        var exclusive = await gate.RunExclusiveAsync(() => Task.CompletedTask);

        Assert.False(exclusive);
        release.SetResult();
        Assert.True(await queued);
    }

    [Fact(DisplayName = "R7-7: WhenReleasedAsync completes only once the holder releases the gate")]
    public async Task WhenReleasedAsync_CompletesOnExit()
    {
        var gate = new FileActionGate();
        Assert.True(gate.WhenReleasedAsync().IsCompleted); // not held

        Assert.True(gate.TryEnter());
        var released = gate.WhenReleasedAsync();
        Assert.False(released.IsCompleted);

        gate.Exit();
        await released;
        Assert.False(gate.IsHeld);
    }

    [Fact]
    public async Task RunExclusiveAsync_ReleasesGate_WhenWorkThrows()
    {
        var gate = new FileActionGate();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            gate.RunExclusiveAsync(() => throw new InvalidOperationException("boom")));
        Assert.False(gate.IsHeld);
        Assert.True(await gate.RunExclusiveAsync(() => Task.CompletedTask));
    }

    [Fact]
    public async Task RunExclusiveAsync_ManyConcurrentCallers_OnlyOneRuns()
    {
        var gate = new FileActionGate();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runs = 0;
        var tasks = new Task<bool>[32];
        for (var i = 0; i < tasks.Length; i++)
            tasks[i] = Task.Run(() => gate.RunExclusiveAsync(async () => { System.Threading.Interlocked.Increment(ref runs); await release.Task; }));
        // Release the holder only after every other caller has been turned away. The holder cannot finish
        // before then, so no late starter can slip in after it exits (a fixed Task.Delay was racy on a busy
        // 2-vCPU runner: callers that started after the release legitimately ran too).
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (tasks.Count(t => t.IsCompleted) < tasks.Length - 1)
        {
            Assert.True(DateTime.UtcNow < deadline, "Losing callers were not rejected in time.");
            await Task.Delay(10);
        }
        release.SetResult();
        var results = await Task.WhenAll(tasks);
        Assert.Equal(1, runs);
        Assert.Single(results, r => r);
    }

    [Fact(DisplayName = "RV-A01: an action queued re-entrantly from the first action's synchronous prefix (modal dialog loop) waits for the first")]
    public async Task RunQueuedAsync_WorkReentersSynchronously_SecondActionWaitsForFirst()
    {
        var gate = new FileActionGate();
        var releaseA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new List<string>();
        Task<bool>? second = null;

        // A's synchronous prefix stands in for ShowConfirmation/PickFolder: a nested dispatcher loop on the
        // SAME thread from which another key press reaches RunQueuedAsync before A has awaited anything.
        var first = gate.RunQueuedAsync(async () =>
        {
            order.Add("A-start");
            second = gate.RunQueuedAsync(() => { order.Add("B-start"); return Task.CompletedTask; });
            await releaseA.Task;
            order.Add("A-end");
        });

        Assert.NotNull(second);
        Assert.Equal(["A-start"], order);
        Assert.False(second.IsCompleted);
        Assert.True(gate.IsHeld);

        releaseA.SetResult();
        Assert.True(await first.WaitAsync(Wait.DefaultTimeout));
        Assert.True(await second.WaitAsync(Wait.DefaultTimeout));
        Assert.Equal(["A-start", "A-end", "B-start"], order);
        Assert.False(gate.IsHeld);
    }

    [Fact(DisplayName = "RV-A01: actions queued re-entrantly at several depths still run one at a time in submission order")]
    public async Task RunQueuedAsync_NestedReentrantSubmissions_RunInSubmissionOrder()
    {
        var gate = new FileActionGate();
        var releaseA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new List<string>();
        var running = 0;
        var maxRunning = 0;
        Task<bool>? b = null, c = null, d = null;

        async Task Track(string name, Func<Task> body)
        {
            running++;
            maxRunning = Math.Max(maxRunning, running);
            order.Add(name + "-start");
            await body();
            order.Add(name + "-end");
            running--;
        }

        var a = gate.RunQueuedAsync(() => Track("A", async () =>
        {
            b = gate.RunQueuedAsync(() => Track("B", () =>
            {
                d = gate.RunQueuedAsync(() => Track("D", () => Task.CompletedTask));
                return Task.CompletedTask;
            }));
            c = gate.RunQueuedAsync(() => Track("C", () => Task.CompletedTask));
            await releaseA.Task;
        }));

        Assert.Equal(["A-start"], order);
        Assert.NotNull(b);
        Assert.NotNull(c);
        releaseA.SetResult();
        await Task.WhenAll(a, b, c).WaitAsync(Wait.DefaultTimeout);
        Assert.NotNull(d);
        await d.WaitAsync(Wait.DefaultTimeout);

        Assert.Equal(["A-start", "A-end", "B-start", "B-end", "C-start", "C-end", "D-start", "D-end"], order);
        Assert.Equal(1, maxRunning);
        Assert.False(gate.IsHeld);
    }

    [Fact(DisplayName = "RV-A01: another thread is not blocked by the gate while a queued action is in its synchronous prefix")]
    public async Task IsHeld_FromOtherThreadWhileQueuedWorkInSyncPrefix_DoesNotBlock()
    {
        var gate = new FileActionGate();
        using var entered = new ManualResetEventSlim();
        using var releasePrefix = new ManualResetEventSlim();

        // A blocks in its synchronous prefix (a modal dialog keeps its thread there the same way).
        var submit = Task.Factory.StartNew(() => gate.RunQueuedAsync(() =>
        {
            entered.Set();
            releasePrefix.Wait();
            return Task.CompletedTask;
        }), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();

        try
        {
            Assert.True(entered.Wait(Wait.DefaultTimeout), "Queued work never started.");

            var probe = Task.Factory.StartNew(() =>
            {
                var held = gate.IsHeld;
                var tryEnter = gate.TryEnter();
                var released = gate.WhenReleasedAsync();
                return (held, tryEnter, releasedCompleted: released.IsCompleted);
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

            // Hang guard only (TimeoutException = the bug): on a gate that runs the prefix under its lock the probe
            // stays blocked until A returns.
            var (held, tryEnter, releasedCompleted) = await probe.WaitAsync(Wait.DefaultTimeout);
            Assert.True(held);
            Assert.False(tryEnter);
            Assert.False(releasedCompleted);
        }
        finally
        {
            releasePrefix.Set();
        }

        Assert.True(await submit.WaitAsync(Wait.DefaultTimeout));
        await gate.WhenReleasedAsync().WaitAsync(Wait.DefaultTimeout);
        Assert.False(gate.IsHeld);
    }

    [ThreadStatic] private static bool t_insideFirstActionCompletion;

    [Fact(DisplayName = "RV-A01: the next queued action never starts inline on the finishing action's stack (no SynchronizationContext)")]
    public async Task RunQueuedAsync_PreviousActionCompletes_NextActionDoesNotRunInlineOnItsStack()
    {
        var gate = new FileActionGate();
        bool? secondRanInline = null;

        await Task.Run(async () =>
        {
            Assert.Null(SynchronizationContext.Current); // plain pool thread: no context to post continuations to
            // Synchronous continuations on purpose: completing it runs A's remainder (and the gate's finally) right
            // inside SetResult on THIS thread, so anything the gate chains inline would run inside it too.
            var releaseA = new TaskCompletionSource();
            var first = gate.RunQueuedAsync(async () => await releaseA.Task);
            var second = gate.RunQueuedAsync(() =>
            {
                secondRanInline = t_insideFirstActionCompletion;
                return Task.CompletedTask;
            });

            t_insideFirstActionCompletion = true;
            try { releaseA.SetResult(); }
            finally { t_insideFirstActionCompletion = false; }

            Assert.True(await first.WaitAsync(Wait.DefaultTimeout));
            Assert.True(await second.WaitAsync(Wait.DefaultTimeout));
        });

        Assert.False(secondRanInline);
        Assert.False(gate.IsHeld);
    }

    [Fact(DisplayName = "Q-T1: a failing queued action faults only its own task; the next one still runs")]
    public async Task RunQueuedAsync_FirstActionThrows_OnlyItsTaskFaultsAndNextRuns()
    {
        var gate = new FileActionGate();
        var releaseA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRan = false;

        var first = gate.RunQueuedAsync(async () =>
        {
            await releaseA.Task;
            throw new InvalidOperationException("boom");
        });
        var second = gate.RunQueuedAsync(() => { secondRan = true; return Task.CompletedTask; });
        Assert.False(secondRan);

        releaseA.SetResult();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => first.WaitAsync(Wait.DefaultTimeout));
        Assert.Equal("boom", ex.Message);
        Assert.True(await second.WaitAsync(Wait.DefaultTimeout));
        Assert.True(secondRan);
        Assert.False(gate.IsHeld);
    }

    [Fact(DisplayName = "Q-T1: a queued action that throws synchronously faults its own task, releases the gate and does not stall the queue")]
    public async Task RunQueuedAsync_WorkThrowsSynchronously_TaskFaultsAndQueueContinues()
    {
        var gate = new FileActionGate();

        var first = gate.RunQueuedAsync(() => throw new InvalidOperationException("sync boom"));

        Assert.True(first.IsFaulted);
        await Assert.ThrowsAsync<InvalidOperationException>(() => first);
        Assert.False(gate.IsHeld);
        Assert.True(await gate.RunQueuedAsync(() => Task.CompletedTask).WaitAsync(Wait.DefaultTimeout));
    }

    [Fact(DisplayName = "Q-T1: a cancelled queued action leaves its own task Canceled (not Faulted) and releases the gate")]
    public async Task RunQueuedAsync_WorkCancelled_TaskIsCanceledAndGateReleased()
    {
        var gate = new FileActionGate();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var run = gate.RunQueuedAsync(() => Task.FromCanceled(cts.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(Wait.DefaultTimeout));
        Assert.True(run.IsCanceled);
        Assert.False(gate.IsHeld);
        Assert.True(await gate.RunExclusiveAsync(() => Task.CompletedTask));
    }

    [Fact(DisplayName = "R7-7: WhenReleasedAsync waits for the whole action queue, not just the running action")]
    public async Task WhenReleasedAsync_WithQueuedActions_CompletesWhenQueueDrains()
    {
        var gate = new FileActionGate();
        var releaseA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var a = gate.RunQueuedAsync(() => releaseA.Task);
        var b = gate.RunQueuedAsync(() => releaseB.Task);
        var released = gate.WhenReleasedAsync();

        releaseA.SetResult();
        Assert.True(await a.WaitAsync(Wait.DefaultTimeout));
        Assert.False(released.IsCompleted);
        Assert.True(gate.IsHeld);

        releaseB.SetResult();
        Assert.True(await b.WaitAsync(Wait.DefaultTimeout));
        await released.WaitAsync(Wait.DefaultTimeout);
        Assert.False(gate.IsHeld);
    }

    [Fact(DisplayName = "Q-T1: with nothing queued, the action starts synchronously in the caller (no extra hop before a modal)")]
    public async Task RunQueuedAsync_IdleGate_StartsWorkSynchronously()
    {
        var gate = new FileActionGate();
        var started = false;
        var heldInside = false;

        var run = gate.RunQueuedAsync(() =>
        {
            started = true;
            heldInside = gate.IsHeld;
            return Task.CompletedTask;
        });

        Assert.True(started);
        Assert.True(heldInside);
        Assert.True(run.IsCompletedSuccessfully);
        Assert.True(await run);
        Assert.False(gate.IsHeld);
    }
}

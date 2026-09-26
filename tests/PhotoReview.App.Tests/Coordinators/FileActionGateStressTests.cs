using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PhotoReview.App.Coordinators;
using PhotoReview.TestSupport;
using Xunit;

namespace PhotoReview.App.Tests.Coordinators;

/// <summary>Barrier-driven stress of <see cref="FileActionGate"/>: no sleeps, every thread released at the same instant.</summary>
public sealed class FileActionGateStressTests
{
    private const int Rounds = 200;
    private const int Threads = 8;

    [Fact(DisplayName = "Exactly one of many simultaneous TryEnter calls wins, in every round")]
    public async Task TryEnter_SimultaneousCallers_ExactlyOneWins()
    {
        var gate = new FileActionGate();
        for (var round = 0; round < Rounds; round++)
        {
            using var barrier = new Barrier(Threads);
            var winners = 0;
            var tasks = Enumerable.Range(0, Threads).Select(_ => Task.Factory.StartNew(() =>
            {
                barrier.SignalAndWait();
                if (gate.TryEnter()) Interlocked.Increment(ref winners);
            }, TaskCreationOptions.LongRunning)).ToArray();
            await Task.WhenAll(tasks).WaitAsync(Wait.DefaultTimeout);

            Assert.Equal(1, winners);
            Assert.True(gate.IsHeld);
            gate.Exit();
            Assert.False(gate.IsHeld);
        }
    }

    [Fact(DisplayName = "A waiter registered while the holder exits is never lost: WhenReleasedAsync completes in every interleaving")]
    public async Task WhenReleasedAsync_RacingWithExit_NeverLosesTheWakeup()
    {
        var gate = new FileActionGate();
        for (var round = 0; round < Rounds; round++)
        {
            Assert.True(gate.TryEnter());
            using var barrier = new Barrier(Threads + 1);
            var waiters = Enumerable.Range(0, Threads).Select(_ => Task.Factory.StartNew(() =>
            {
                barrier.SignalAndWait();
                return gate.WhenReleasedAsync();
            }, TaskCreationOptions.LongRunning).Unwrap()).ToArray();
            var exit = Task.Factory.StartNew(() =>
            {
                barrier.SignalAndWait();
                gate.Exit();
            }, TaskCreationOptions.LongRunning);

            await Task.WhenAll(waiters.Append(exit)).WaitAsync(Wait.DefaultTimeout);

            Assert.False(gate.IsHeld);
        }
    }

    [Fact(DisplayName = "Exit on a gate nobody holds is harmless and leaves it usable")]
    public void Exit_WithoutHolder_IsHarmless()
    {
        var gate = new FileActionGate();

        gate.Exit();
        gate.Exit();

        Assert.False(gate.IsHeld);
        Assert.True(gate.TryEnter());
        gate.Exit();
    }

    [Fact(DisplayName = "Work that is cancelled or throws still releases the gate and completes waiters")]
    public async Task RunExclusiveAsync_CancelledOrFaultedWork_ReleasesAndWakesWaiters()
    {
        var gate = new FileActionGate();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = gate.RunExclusiveAsync(async () =>
        {
            await release.Task;
            throw new OperationCanceledException();
        });
        var waiter = gate.WhenReleasedAsync();
        Assert.False(waiter.IsCompleted);

        release.SetResult();

        await Assert.ThrowsAsync<OperationCanceledException>(() => run);
        await waiter.WaitAsync(Wait.DefaultTimeout);
        Assert.False(gate.IsHeld);
        Assert.True(await gate.RunExclusiveAsync(() => Task.CompletedTask));
    }
}

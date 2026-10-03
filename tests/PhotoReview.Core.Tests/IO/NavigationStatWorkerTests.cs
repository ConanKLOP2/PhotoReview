using System.Threading;
using System.Threading.Tasks;
using PhotoReview.Core.IO;

namespace PhotoReview.Core.Tests.IO;

[Trait("Category", "HotPath")]
public sealed class NavigationStatWorkerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    [Fact(DisplayName = "Q-R29 C: the stat worker never runs the awaiting code on its own thread (it stays free for the next stat)")]
    public async Task RunAsync_ContinuationDoesNotRunOnTheWorkerThread()
    {
        var worker = new NavigationStatWorker();
        using var gate = new ManualResetEventSlim(false);
        var task = worker.RunAsync(() =>
        {
            gate.Wait(Timeout); // held until the continuation below is registered, so it cannot run on the caller
            return Environment.CurrentManagedThreadId;
        });
        // ExecuteSynchronously runs the continuation on whichever thread completes the task.
        var continuation = task.ContinueWith(_ => Environment.CurrentManagedThreadId, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        gate.Set();

        var workerThread = await task.WaitAsync(Timeout);
        var continuationThread = await continuation.WaitAsync(Timeout);

        Assert.NotEqual(workerThread, continuationThread);
    }

    [Fact(DisplayName = "Q-R29 C: the stat worker runs work off the calling thread and surfaces its exception on the task")]
    public async Task RunAsync_RunsOffTheCallerAndFaultsWithTheWorkException()
    {
        var worker = new NavigationStatWorker();
        var caller = Environment.CurrentManagedThreadId;

        var ranOn = await worker.RunAsync(() => Environment.CurrentManagedThreadId).WaitAsync(Timeout);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            worker.RunAsync<int>(() => throw new InvalidOperationException("boom")).WaitAsync(Timeout));

        Assert.NotEqual(caller, ranOn);
        Assert.Equal("boom", error.Message);
    }

    [Fact(DisplayName = "Q-R29 C: work whose token is already cancelled is never run")]
    public async Task RunAsync_CancelledToken_DoesNotRunTheWork()
    {
        var worker = new NavigationStatWorker();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var ran = false;

        var task = worker.RunAsync(() => ran = true, cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(Timeout));
        Assert.False(ran);
    }
}

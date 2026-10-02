using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace PhotoReview.App.Tests;

public sealed class TaskLoggingTests
{
    [Fact]
    public async Task FireAndLog_FaultedTask_StillReportsTheFaultToAwaiters()
    {
        var tcs = new TaskCompletionSource();
        tcs.Task.FireAndLog("test context");

        tcs.SetException(new InvalidOperationException("boom"));

        // A continuation does not consume the exception: awaiting still throws.
        await Assert.ThrowsAsync<InvalidOperationException>(() => tcs.Task);
    }

    [Fact]
    public void FireAndLog_CompletedAndCancelledTasks_DoNotThrow()
    {
        Task.CompletedTask.FireAndLog("ok");
        Task.FromCanceled(new CancellationToken(true)).FireAndLog("cancelled");
        Assert.Throws<ArgumentNullException>(() => ((Task)null!).FireAndLog("null"));
    }

    [Fact(DisplayName = "A2: FireAndLog(start) swallows a synchronous throw of the start delegate instead of letting it escape")]
    public void FireAndLogStart_SynchronousThrow_DoesNotEscape()
    {
        TaskLogging.FireAndLog(() => throw new ObjectDisposedException("scheduler"), "disposed");
        TaskLogging.FireAndLog(() => throw new OperationCanceledException(), "cancelled");
        TaskLogging.FireAndLog(() => null, "absent collaborator");
        Assert.Throws<ArgumentNullException>(() => TaskLogging.FireAndLog((Func<Task?>)null!, "null"));
    }

    [Fact(DisplayName = "A2: FireAndLog(start) still observes the task the delegate returns")]
    public async Task FireAndLogStart_ReturnedTaskFaults_StillReportsToAwaiters()
    {
        var tcs = new TaskCompletionSource();
        TaskLogging.FireAndLog(() => tcs.Task, "returned task");

        tcs.SetException(new InvalidOperationException("boom"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => tcs.Task);
    }
}

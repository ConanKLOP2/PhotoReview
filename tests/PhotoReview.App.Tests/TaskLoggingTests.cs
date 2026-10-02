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
}

using PhotoReview.Core.Abstractions;

namespace PhotoReview.Core.Tests.Abstractions;

/// <summary>RV-S07: <see cref="ImmediateUiScheduler.InvokeAsync"/> reports a failing action like the WPF dispatcher does.</summary>
public sealed class ImmediateUiSchedulerTests
{
    [Fact(DisplayName = "RV-S07: InvokeAsync whose action throws returns a faulted task instead of throwing synchronously")]
    public async Task InvokeAsync_ActionThrows_ReturnsFaultedTask()
    {
        var failure = new InvalidOperationException("boom");
        Task? task = null;

        var syncException = Record.Exception(() => { task = ImmediateUiScheduler.Instance.InvokeAsync(() => throw failure); });

        Assert.Null(syncException);
        Assert.NotNull(task);
        Assert.True(task!.IsFaulted);
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => task));
    }

    [Fact(DisplayName = "RV-S07: InvokeAsync still runs the action inline and completes")]
    public async Task InvokeAsync_ActionSucceeds_RunsInlineAndCompletes()
    {
        var ran = false;

        var task = ImmediateUiScheduler.Instance.InvokeAsync(() => ran = true);

        Assert.True(ran);
        Assert.True(task.IsCompletedSuccessfully);
        await task;
    }

    [Fact(DisplayName = "RV-S07: InvokeAsync with a null action still throws ArgumentNullException at the call")]
    public void InvokeAsync_NullAction_Throws() =>
        Assert.Throws<ArgumentNullException>(() => { _ = ImmediateUiScheduler.Instance.InvokeAsync(null!); });
}

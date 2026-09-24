using System;
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
        await Task.Delay(100);
        release.SetResult();
        var results = await Task.WhenAll(tasks);
        Assert.Equal(1, runs);
        Assert.Single(results, r => r);
    }
}

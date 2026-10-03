using PhotoReview.Core.Instance;
using PhotoReview.Core.IO;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.Instance;

/// <summary>Mutation-testing gaps in SecondInstanceHandoff, ForwardedOpenCoalescer and NavigationStatWorker.</summary>
public sealed class InstanceAndWorkerMutationGapTests
{
    private sealed class FakeClient(Func<Task<ForwardOutcome>> send) : IInstanceForwardClient
    {
        public Task<ForwardOutcome> SendAsync(IReadOnlyList<string> paths, TimeSpan timeout, CancellationToken cancellationToken = default) => send();
    }

    [Theory]
    [InlineData(ForwardOutcome.Delivered, true)]
    [InlineData(ForwardOutcome.Unknown, true)]
    [InlineData(ForwardOutcome.Rejected, false)]
    [InlineData(ForwardOutcome.NoInstance, false)]
    public async Task TryForward_Outcome_IsLoggedWithThePathCountAndMappedToTheExitDecision(ForwardOutcome outcome, bool expected)
    {
        var log = new MutationRecordingLog();
        var client = new FakeClient(() => Task.FromResult(outcome));

        var result = await SecondInstanceHandoff.TryForwardAsync(client, [@"C:\a.jpg", @"C:\b.jpg"], TimeSpan.FromSeconds(1), log);

        Assert.Equal(expected, result);
        var info = Assert.Single(log.Infos);
        Assert.Contains(outcome.ToString(), info, StringComparison.Ordinal);
        Assert.Contains("2 path(s)", info, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TryForward_ClientThrows_LogsAWarningNamingTheExceptionAndReturnsFalse()
    {
        var log = new MutationRecordingLog();
        var client = new FakeClient(() => throw new InvalidOperationException("pipe broke"));

        var result = await SecondInstanceHandoff.TryForwardAsync(client, [], TimeSpan.FromSeconds(1), log);

        Assert.False(result);
        var warning = Assert.Single(log.Warnings);
        Assert.Contains("InvalidOperationException", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void Coalescer_OpenCallbackThrows_IsLoggedAndTheNextSubmitStillWorks()
    {
        var log = new MutationRecordingLog();
        var opened = new List<string?>();
        var attempts = 0;
        using var coalescer = new ForwardedOpenCoalescer(path =>
        {
            opened.Add(path);
            if (attempts++ == 0) throw new InvalidOperationException("window gone");
        }, TimeSpan.FromSeconds(1), log, (_, _) => Task.CompletedTask);

        // The injected delay is already complete, so the whole flush runs synchronously inside Submit.
        coalescer.Submit([@"C:\a.jpg"]);
        Assert.Single(log.Errors);
        coalescer.Submit([@"C:\b.jpg"]);

        Assert.Equal([@"C:\a.jpg", @"C:\b.jpg"], opened);
        Assert.Contains("Forwarded open failed", log.Errors[0].Message, StringComparison.Ordinal);
        Assert.IsType<InvalidOperationException>(log.Errors[0].Exception);
    }

    [Fact]
    public async Task Coalescer_WindowDelayFails_IsLoggedAndTheOpenStillHappens()
    {
        var log = new MutationRecordingLog();
        var opened = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var coalescer = new ForwardedOpenCoalescer(p => opened.TrySetResult(p), TimeSpan.FromSeconds(1), log,
            (_, _) => throw new InvalidOperationException("timer broke"));

        coalescer.Submit([@"C:\a.jpg"]);

        Assert.Equal(@"C:\a.jpg", await opened.Task.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Contains(log.Errors, e => e.Message.Contains("window failed", StringComparison.Ordinal));
    }

    [Fact]
    public void Coalescer_WindowIsCancelledWithoutDispose_DoesNotOpenAnything()
    {
        var opened = 0;
        using var coalescer = new ForwardedOpenCoalescer(_ => Interlocked.Increment(ref opened), TimeSpan.FromSeconds(1), null,
            (_, _) => Task.FromCanceled(new CancellationToken(canceled: true))); // completes synchronously, so Submit has finished the whole flush

        coalescer.Submit([@"C:\a.jpg"]);

        Assert.Equal(0, Volatile.Read(ref opened));
    }

    [Fact]
    public async Task StatWorker_ItemCancelledWhileQueued_IsCancelledWithoutRunningItsWork()
    {
        var worker = new NavigationStatWorker();
        using var firstRunning = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        using var cts = new CancellationTokenSource();
        var secondRan = 0;

        var first = worker.RunAsync(() =>
        {
            firstRunning.Set();
            releaseFirst.Wait(TimeSpan.FromSeconds(30));
            return 1;
        });
        Assert.True(firstRunning.Wait(TimeSpan.FromSeconds(10)), "the worker never started the first item");
        var second = worker.RunAsync(() => Interlocked.Increment(ref secondRan), cts.Token); // queued behind the running first item
        await cts.CancelAsync();
        releaseFirst.Set();

        Assert.Equal(1, await first.WaitAsync(TimeSpan.FromSeconds(30)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal(0, Volatile.Read(ref secondRan));
    }

    [Fact]
    public async Task StatWorker_WorkThrows_FaultsOnlyThatTask()
    {
        var worker = new NavigationStatWorker();

        var bad = worker.RunAsync<int>(() => throw new InvalidOperationException("stat failed"));
        var good = worker.RunAsync(() => 7);

        await Assert.ThrowsAsync<InvalidOperationException>(() => bad.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal(7, await good.WaitAsync(TimeSpan.FromSeconds(30)));
    }
}
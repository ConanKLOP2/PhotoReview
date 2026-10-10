using PhotoReview.App;
using PhotoReview.Integration.Tests.Infrastructure;

namespace PhotoReview.Integration.Tests;

/// <summary>
/// Audit D-03: the STA harness turns an unannounced <c>AppLog.Error</c> (the only trace a fire-and-forget failure leaves)
/// into a failing test body. These probes pin the guard itself; the rest of the UI suite is its regression net.
/// </summary>
[Collection("GlobalState")]
public sealed class StaTestHostLogGuardTests
{
    [Fact]
    public async Task UnexpectedAppLogError_FailsTheBody()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => StaTestHost.RunAsync(() =>
        {
            AppLog.Error("log-guard-probe-unexpected");
            return Task.CompletedTask;
        }));

        Assert.Contains("log-guard-probe-unexpected", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FireAndForgetFault_IsReportedByTheBody()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => StaTestHost.RunAsync(async () =>
        {
            // The same call the app uses for a deliberately unawaited task: its fault is logged, never thrown.
            Task.FromException(new InvalidOperationException("log-guard-probe-boom")).FireAndLog("log-guard-probe-context");
            await StaTestHost.DrainAsync(TimeSpan.FromMilliseconds(150));
        }));

        Assert.Contains("log-guard-probe-context", ex.Message, StringComparison.Ordinal);
        Assert.Contains("log-guard-probe-boom", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnnouncedAppLogError_DoesNotFailTheBody_AndOthersStillDo()
    {
        await StaTestHost.RunAsync(() =>
        {
            StaTestHost.ExpectLoggedError("log-guard-probe-announced", "this probe makes the app log it on purpose", mustOccur: true);
            AppLog.Error("log-guard-probe-announced: fine");
            return Task.CompletedTask;
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => StaTestHost.RunAsync(() =>
        {
            StaTestHost.ExpectLoggedError("log-guard-probe-announced", "only this one is allowed");
            AppLog.Error("log-guard-probe-announced: fine");
            AppLog.Error("log-guard-probe-other");
            return Task.CompletedTask;
        }));
        Assert.Contains("log-guard-probe-other", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("log-guard-probe-announced", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnnouncedMustOccurError_ThatNeverHappens_FailsTheBody()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => StaTestHost.RunAsync(() =>
        {
            StaTestHost.ExpectLoggedError("log-guard-probe-never", "required", mustOccur: true);
            return Task.CompletedTask;
        }));

        Assert.Contains("log-guard-probe-never", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WarningsAndInfo_DoNotFailTheBody()
    {
        await StaTestHost.RunAsync(() =>
        {
            AppLog.Info("log-guard-probe-info");
            AppLog.Warn("log-guard-probe-warn");
            return Task.CompletedTask;
        });
    }
}

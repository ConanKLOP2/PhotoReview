using PhotoReview.Core.Abstractions;
using PhotoReview.Platform.Windows;

namespace PhotoReview.Integration.Tests;

[Trait("Category", "Integration")]
public sealed class InstanceLockLoggingTests
{
    [Fact(DisplayName = "InstanceLock logs a warning when the mutex is released from a foreign thread (CORE-09)")]
    public void Dispose_FromForeignThread_LogsWarningInsteadOfSwallowing()
    {
        var log = new RecordingLog();
        InstanceLock? instanceLock = null;
        var key = "test-key-" + Guid.NewGuid().ToString("N");

        // The creating thread stays alive (a dead owner would make the mutex abandoned instead of foreign-owned).
        using var created = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var creator = new Thread(() =>
        {
            instanceLock = new InstanceLock(key, log);
            created.Set();
            release.Wait();
        });
        creator.Start();
        created.Wait();
        Assert.True(instanceLock!.IsOwner);

        try
        {
            instanceLock.Dispose(); // test thread != creating thread: ReleaseMutex throws ApplicationException
        }
        finally
        {
            release.Set();
            creator.Join();
        }

        Assert.Contains(log.Warnings, w => w.Contains("InstanceLock", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "InstanceLock does not warn on a normal same-thread dispose (CORE-09)")]
    public void Dispose_FromOwningThread_DoesNotWarn()
    {
        var log = new RecordingLog();
        var instanceLock = new InstanceLock("test-key-" + Guid.NewGuid().ToString("N"), log);

        instanceLock.Dispose();

        Assert.Empty(log.Warnings);
    }

    private sealed class RecordingLog : ILog
    {
        public List<string> Warnings { get; } = [];
        public bool Enabled => true;
        public void Info(string message) { }
        public void Warn(string message) => Warnings.Add(message);
        public void Error(string message, Exception? ex = null) { }
    }
}

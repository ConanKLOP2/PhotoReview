using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Model;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.FileActions;

public sealed class RecoveryRetryFileCheckFaultTests
{
    private static readonly AppPaths Paths = new(@"C:\Users\test\AppData\Local");
    private static readonly DateTime Stamp = new(2026, 9, 29, 0, 0, 0, DateTimeKind.Utc);

    private sealed class FixedClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow { get; } = utcNow;
    }

    [Fact(DisplayName = "C3: a single Move retry whose source check throws ArgumentException is a failed result, not an exception")]
    public async Task RetrySingle_FileCheckThrows_ReturnsFailedResult()
    {
        var fileSystem = new CrashPointFileSystem(new InMemoryFileSystem());
        fileSystem.ArgumentFaultPaths.Add(@"C:\photos\bad.jpg");
        var clock = new FixedClock(Stamp);
        var journal = new OperationJournal(Paths, fileSystem, clock);
        var service = new RecoveryRetryService(journal, fileSystem, clock);
        var failed = new JournalEntry("id1", FileOperationType.Move, JournalState.Failed,
            @"C:\photos\bad.jpg", @"C:\selected\bad.jpg", 10, Stamp, Stamp);

        var result = await service.RetryMoveOrCopyAsync(failed);

        Assert.False(result.Succeeded);
        Assert.Contains("simulated invalid path", result.Message, StringComparison.Ordinal);
    }
}

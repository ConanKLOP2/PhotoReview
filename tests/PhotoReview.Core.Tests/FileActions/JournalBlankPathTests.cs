using System.Text.Json;
using PhotoReview.Core;
using PhotoReview.Core.FileActions;
using PhotoReview.Core.Model;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.FileActions;

public sealed class JournalBlankPathTests
{
    private static readonly AppPaths Paths = new(@"C:\Users\test\AppData\Local");
    private static readonly DateTime Stamp = new(2026, 9, 29, 0, 0, 0, DateTimeKind.Utc);

    private static string Line(string source, string? destination) =>
        "{\"Id\":\"id1\",\"Type\":\"Move\",\"State\":\"Prepared\",\"Source\":" + JsonSerializer.Serialize(source) +
        ",\"Destination\":" + JsonSerializer.Serialize(destination) +
        ",\"Size\":1,\"LastWriteUtc\":\"2026-01-01T00:00:00Z\",\"TimestampUtc\":\"2026-01-01T00:00:00Z\"}";

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\0")]
    public void TryParse_BlankOrNulSource_IsRejected(string source)
    {
        Assert.Null(JournalLineParser.TryParse(Line(source, @"C:\b.jpg")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("\0")]
    public void TryParse_BlankDestination_IsReadAsNull(string destination)
    {
        var entry = JournalLineParser.TryParse(Line(@"C:\a.jpg", destination));

        Assert.NotNull(entry);
        Assert.Null(entry!.Destination);
    }

    [Fact]
    public void TryParse_ValidLine_IsAccepted()
    {
        var entry = JournalLineParser.TryParse(Line(@"C:\a.jpg", @"C:\b.jpg"));

        Assert.NotNull(entry);
        Assert.Equal(@"C:\b.jpg", entry!.Destination);
    }

    [Fact]
    public void Reconcile_EntryWhoseFileCheckThrows_DoesNotStopTheOtherEntries()
    {
        var inner = new InMemoryFileSystem();
        inner.AddFile(@"C:\selected\b.jpg", new string('j', 10), Stamp);
        var fileSystem = new CrashPointFileSystem(inner);
        fileSystem.ArgumentFaultPaths.Add(@"C:\photos\bad.jpg");
        var journal = new OperationJournal(Paths, fileSystem, new FixedClock(Stamp.AddMinutes(1)));
        journal.Append(new JournalEntry("bad", FileOperationType.Move, JournalState.Prepared,
            @"C:\photos\bad.jpg", @"C:\selected\bad.jpg", 10, Stamp, Stamp));
        journal.Append(new JournalEntry("good", FileOperationType.Move, JournalState.Prepared,
            @"C:\photos\b.jpg", @"C:\selected\b.jpg", 10, Stamp, Stamp));

        var outcome = Assert.Single(journal.ReconcilePendingOperations());

        Assert.Equal("good", outcome.Id);
        Assert.Equal(JournalState.Committed, outcome.State);
    }

    private sealed class FixedClock(DateTime utcNow) : PhotoReview.Core.Abstractions.IClock
    {
        public DateTime UtcNow { get; } = utcNow;
    }
}

using PhotoReview.Core.Model;
using PhotoReview.Core.FileActions;

namespace PhotoReview.TestSupport;

/// <summary>
/// Test-only views over <see cref="OperationJournal.ReadPendingAndFailedOperations"/> (the production API reads both in one pass).
/// </summary>
public static class OperationJournalTestExtensions
{
    /// <summary>The latest line of every Id that is still Prepared.</summary>
    public static IReadOnlyList<JournalEntry> ReadPendingOperations(this OperationJournal journal)
        => journal.ReadPendingAndFailedOperations().Where(e => e.State == JournalState.Prepared).ToList();

    /// <summary>The latest line of every Id that is Failed.</summary>
    public static IReadOnlyList<JournalEntry> ReadFailedOperations(this OperationJournal journal)
        => journal.ReadPendingAndFailedOperations().Where(e => e.State == JournalState.Failed).ToList();
}

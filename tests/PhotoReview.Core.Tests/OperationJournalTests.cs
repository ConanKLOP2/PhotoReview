using System.IO;
using System.Linq;
using System.Text.Json;
using PhotoReview.Core.Model;

namespace PhotoReview.Core.Tests;

/// <summary>
/// Journal durability and Move-commit contracts. The constructor
/// reproduces the original suite's committed-Move setup for every test.
/// </summary>
[Collection("GlobalState")]
public sealed class OperationJournalTests : IDisposable
{
    private readonly DataRootFixture _data = new();
    private readonly OperationJournal _journal = new();
    private readonly string _source;
    private readonly string _destination;
    private readonly string _operationId;
    private readonly FileInfo _info;

    public OperationJournalTests()
    {
        var root = _data.Path;
        _source = Path.Combine(root, "source.jpg");
        _destination = Path.Combine(root, "dest", "source.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(_destination)!);
        File.WriteAllBytes(_source, [1, 2, 3, 4]);
        _info = new FileInfo(_source);
        _operationId = Guid.NewGuid().ToString("N");
        _journal.Append(new JournalEntry(_operationId, FileOperationType.Move, JournalState.Prepared, _source, _destination,
            _info.Length, _info.LastWriteTimeUtc, DateTime.UtcNow));
        File.Move(_source, _destination);
        _journal.Append(new JournalEntry(_operationId, FileOperationType.Move, JournalState.Committed, _source, _destination,
            _info.Length, _info.LastWriteTimeUtc, DateTime.UtcNow));
    }

    public void Dispose() => _data.Dispose();

    private string JournalFile =>
        Directory.GetFiles(Path.Combine(_data.Path, "app-data"), "operations.jsonl").Single();

    [Fact(DisplayName = "Journal committed Move")]
    public void JournalCommittedMove() =>
        Assert.Contains(_journal.ReadCommittedMoves(),
            x => x.Source == _source && x.Destination == _destination);

    [Fact(DisplayName = "Journal has no pending committed Move")]
    public void JournalHasNoPendingCommittedMove() => Assert.Empty(_journal.ReadPendingOperations());

    [Fact(DisplayName = "Journal entries are durably written as JSONL")]
    [Trait("Category", "Integration")]
    public void JournalEntriesAreDurablyWrittenAsJsonl()
    {
        var journalFile = JournalFile;
        Assert.True(new FileInfo(journalFile).Length > 0
            && File.ReadAllLines(journalFile).All(line => line.StartsWith("{", StringComparison.Ordinal)));
    }

    [Fact(DisplayName = "Journal readers tolerate an invalid JSONL line")]
    public void JournalReadersTolerateInvalidJsonlLine()
    {
        File.AppendAllText(JournalFile, "{not-valid-json}" + Environment.NewLine);
        Assert.True(_journal.ReadCommittedMoves().Any(x => x.Id == _operationId)
            && _journal.ReadPendingOperations().Count == 0);
    }

    [Fact(DisplayName = "Journal concurrent append/read remains line-consistent")]
    [Trait("Category", "Integration")]
    public void JournalConcurrentAppendReadRemainsLineConsistent()
    {
        Parallel.For(0, 8, i => _journal.Append(new JournalEntry($"parallel-{i}", FileOperationType.Move, JournalState.Committed,
            _source, _destination, 4, _info.LastWriteTimeUtc, DateTime.UtcNow)));
        Assert.Equal(8, _journal.ReadCommittedMoves()
            .Count(x => x.Id.StartsWith("parallel-", StringComparison.Ordinal)));
    }

    [Fact(DisplayName = "Move preserves bytes")]
    public void MovePreservesBytes() =>
        Assert.True(File.ReadAllBytes(_destination).SequenceEqual(new byte[] { 1, 2, 3, 4 }));

    [Fact(DisplayName = "Legacy operations fixture deserializes to enums")]
    public void LegacyOperationsFixtureDeserializesToEnums()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "operations-legacy.jsonl");
        Assert.True(File.Exists(fixturePath), $"Fixture not found at {fixturePath}");
        var lines = File.ReadAllLines(fixturePath);
        var entries = lines.Select(line => JsonSerializer.Deserialize<JournalEntry>(line)!).ToList();
        Assert.Equal(4, entries.Count);

        Assert.Equal("op-1", entries[0].Id);
        Assert.Equal(FileOperationType.Move, entries[0].Type);
        Assert.Equal(JournalState.Prepared, entries[0].State);

        Assert.Equal("op-1", entries[1].Id);
        Assert.Equal(FileOperationType.Move, entries[1].Type);
        Assert.Equal(JournalState.Committed, entries[1].State);

        Assert.Equal("op-2", entries[2].Id);
        Assert.Equal(FileOperationType.Recycle, entries[2].Type);
        Assert.Equal(JournalState.Prepared, entries[2].State);

        Assert.Equal("op-3", entries[3].Id);
        Assert.Equal(FileOperationType.Copy, entries[3].Type);
        Assert.Equal(JournalState.Failed, entries[3].State);
    }
}

/// <summary>Pending-operation reconciliation contracts.</summary>
[Collection("GlobalState")]
public sealed class JournalReconciliationTests : IDisposable
{
    private readonly DataRootFixture _data = new();
    private readonly OperationJournal _journal = new();

    public void Dispose() => _data.Dispose();

    [Fact(DisplayName = "Pending recycle is reconciled without replay when source remains")]
    public void PendingRecycleIsReconciledWithoutReplay()
    {
        var journalSource = Path.Combine(_data.Path, "pending-recycle.jpg");
        File.WriteAllBytes(journalSource, [1, 2, 3]);
        var pendingId = Guid.NewGuid().ToString("N");
        _journal.Append(new JournalEntry(pendingId, FileOperationType.Recycle, JournalState.Prepared, journalSource, null, 3,
            File.GetLastWriteTimeUtc(journalSource), DateTime.UtcNow));
        var reconciled = _journal.ReconcilePendingOperations();
        Assert.Contains(reconciled, x => x.Id == pendingId && x.State == JournalState.Failed);
    }

    [Fact(DisplayName = "Pending move is committed only when source is absent and destination fingerprint matches")]
    public void PendingMoveIsCommittedWhenSourceAbsentAndFingerprintMatches()
    {
        var pendingMoveSource = Path.Combine(_data.Path, "pending-move.jpg");
        var pendingMoveDestination = Path.Combine(_data.Path, "pending-dest", "pending-move.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(pendingMoveDestination)!);
        File.WriteAllBytes(pendingMoveDestination, [8, 9, 10]);
        var pendingMoveId = Guid.NewGuid().ToString("N");
        _journal.Append(new JournalEntry(pendingMoveId, FileOperationType.Move, JournalState.Prepared, pendingMoveSource,
            pendingMoveDestination, 3, DateTime.UtcNow, DateTime.UtcNow));
        var moveReconciled = _journal.ReconcilePendingOperations();
        Assert.Contains(moveReconciled, x => x.Id == pendingMoveId && x.State == JournalState.Committed);
    }

    [Fact(DisplayName = "Pending copy with mismatched destination is failed without replay")]
    public void PendingCopyWithMismatchedDestinationIsFailed()
    {
        var mismatchedSource = Path.Combine(_data.Path, "pending-mismatch.jpg");
        var mismatchedDestination = Path.Combine(_data.Path, "pending-mismatch-dest.jpg");
        File.WriteAllBytes(mismatchedDestination, [1, 2]);
        var mismatchId = Guid.NewGuid().ToString("N");
        _journal.Append(new JournalEntry(mismatchId, FileOperationType.Copy, JournalState.Prepared, mismatchedSource,
            mismatchedDestination, 3, DateTime.UtcNow, DateTime.UtcNow));
        var mismatchReconciled = _journal.ReconcilePendingOperations();
        Assert.True(mismatchReconciled.Any(x => x.Id == mismatchId && x.State == JournalState.Failed)
            && _journal.ReadPendingOperations().All(x => x.Id != mismatchId));
    }

    [Fact(DisplayName = "Pending move with source still present is failed without replay")]
    public void PendingMoveWithSourceStillPresentIsFailed()
    {
        var sourceStillExists = Path.Combine(_data.Path, "pending-source-exists.jpg");
        var sourceStillDestination = Path.Combine(_data.Path, "pending-source-exists-dest.jpg");
        File.WriteAllBytes(sourceStillExists, [4, 5, 6]);
        File.WriteAllBytes(sourceStillDestination, [4, 5, 6]);
        var sourceExistsId = Guid.NewGuid().ToString("N");
        _journal.Append(new JournalEntry(sourceExistsId, FileOperationType.Move, JournalState.Prepared, sourceStillExists,
            sourceStillDestination, 3, DateTime.UtcNow, DateTime.UtcNow));
        var sourceExistsReconciled = _journal.ReconcilePendingOperations();
        Assert.Contains(sourceExistsReconciled, x => x.Id == sourceExistsId && x.State == JournalState.Failed);
    }
}

/// <summary>Recovery retry contracts.</summary>
[Collection("GlobalState")]
public sealed class RecoveryRetryServiceTests : IDisposable
{
    private readonly DataRootFixture _data = new();
    private readonly OperationJournal _journal = new();

    public void Dispose() => _data.Dispose();

    private JournalEntry SeedFailedMove(out string retrySource, out string retryDestination)
    {
        retrySource = Path.Combine(_data.Path, "retry.jpg");
        retryDestination = Path.Combine(_data.Path, "retry-dest", "retry.jpg");
        File.WriteAllBytes(retrySource, [7, 8, 9]);
        var retryInfo = new FileInfo(retrySource);
        return new JournalEntry("old-failed", FileOperationType.Move, JournalState.Failed, retrySource, retryDestination,
            retryInfo.Length, retryInfo.LastWriteTimeUtc, DateTime.UtcNow, "previous failure");
    }

    [Fact(DisplayName = "Recovery retry validates fingerprint and journals success")]
    public void RecoveryRetryValidatesFingerprintAndJournalsSuccess()
    {
        var entry = SeedFailedMove(out var retrySource, out var retryDestination);
        var retryResult = RecoveryRetryService.RetryMoveOrCopy(entry, _journal);
        Assert.True(retryResult.Succeeded && File.Exists(retryDestination) && !File.Exists(retrySource));
    }

    [Fact(DisplayName = "Recovery retry rejects invalid source state")]
    public void RecoveryRetryRejectsInvalidSourceState()
    {
        var entry = SeedFailedMove(out _, out var retryDestination);
        RecoveryRetryService.RetryMoveOrCopy(entry, _journal);
        Assert.True(File.Exists(retryDestination)
            && RecoveryRetryService.RetryMoveOrCopy(entry with { Source = retryDestination }, _journal).Succeeded == false);
    }
}

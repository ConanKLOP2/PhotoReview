using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Model;

namespace PhotoReview.Core.FileActions;

/// <summary>
/// Shared Prepared -> mutate -> verify -> Committed/Failed journal sequence (CORE-07), used by
/// <see cref="FileActionService"/> and <see cref="RecoveryRetryService"/> so a fix applies to both.
/// Not thread-safe: one instance per operation.
/// <para>Q-R27: the operation's live marker (<see cref="OperationJournal.LiveOperations"/>) is taken just before Prepared is
/// appended and released right after the outcome (Committed/Failed) is appended, so another process's startup reconcile
/// never judges it mid-flight. <see cref="Dispose"/> releases it on any path that ends without an outcome.</para>
/// </summary>
internal sealed class JournalTransaction : IDisposable
{
    private readonly OperationJournal _journal;
    private readonly IClock _clock;
    private readonly JournalEntry _prepared;
    private readonly bool _failWithoutPrepared;
    private IDisposable? _liveMarker;

    /// <param name="failWithoutPrepared">
    /// Retry semantics: an existing Failed record is being re-attempted, so a failure is journaled even when the
    /// Prepared append itself failed (the caller then reports JournalPersisted=false).
    /// </param>
    public JournalTransaction(OperationJournal journal, IClock clock, JournalEntry prepared, bool failWithoutPrepared = false)
    {
        _failWithoutPrepared = failWithoutPrepared;
        _journal = journal;
        _clock = clock;
        _prepared = prepared;
    }

    /// <summary>True once the Prepared record was appended (a Failed record is only written after this).</summary>
    public bool IsPrepared { get; private set; }

    /// <summary>True once the file mutation was verified; from then on the operation counts as succeeded.</summary>
    public bool MutationCompleted { get; private set; }

    // ADR 0007 J-D: in PowerLossSafe mode the Prepared record costs a WriteThrough + Flush(true) (~2 ms, tail > 15 ms),
    // so it is written on a pool thread and awaited: the mutation still starts only after Prepared is durable, and the
    // rest of the caller (mutation + Committed) already continues off the caller's thread (ConfigureAwait(false)).
    // Fast mode keeps the ~0.4 ms cache write inline -- a thread hop would cost more than the write.
    public Task BeginAsync()
    {
        if (_journal.Durability != JournalDurability.PowerLossSafe)
        {
            Begin();
            return Task.CompletedTask;
        }
        return Task.Run(Begin);
    }

    public void Begin()
    {
        // Before the Prepared line exists anywhere: a reconcile that can read it can also see the marker.
        _liveMarker ??= _journal.LiveOperations.Begin(_prepared.Id);
        try
        {
            _journal.Append(_prepared);
        }
        catch
        {
            ReleaseLiveMarker(); // nothing pending was written (or a retry's Failed follows under failWithoutPrepared)
            throw;
        }
        IsPrepared = true;
    }

    /// <summary>Releases the live marker (idempotent). Called once the outcome is appended, or by <see cref="Dispose"/>.</summary>
    private void ReleaseLiveMarker()
    {
        var marker = _liveMarker;
        _liveMarker = null;
        marker?.Dispose();
    }

    public void Dispose() => ReleaseLiveMarker();

    /// <summary>Throws <see cref="JournalCodedException"/> (<paramref name="errorCode"/>) unless the destination has the prepared size.</summary>
    public void VerifyDestination(IFileSystem fileSystem, string destination, string errorCode)
    {
        var stat = fileSystem.GetFileStat(destination);
        if (stat is null || stat.Length != _prepared.Size)
            throw new JournalCodedException(errorCode);
        MutationCompleted = true;
    }

    /// <summary>
    /// Move check: the source must be gone and the destination must have the prepared size. File.Move across volumes
    /// is MoveFileEx(MOVEFILE_COPY_ALLOWED), which reports success after the copy even when deleting the source failed
    /// (read-only file, a handle without FILE_SHARE_DELETE). Such a Move did not happen from the user's point of view:
    /// it throws <see cref="JournalErrors.MoveSourceNotRemoved"/> and the destination copy is kept (nothing is deleted).
    /// </summary>
    public void VerifyMoved(IFileSystem fileSystem, string source, string destination, string sizeErrorCode)
    {
        if (fileSystem.FileExists(source))
            throw new JournalCodedException(JournalErrors.MoveSourceNotRemoved);
        VerifyDestination(fileSystem, destination, sizeErrorCode);
    }

    /// <summary>For operations with nothing to verify (Recycle).</summary>
    public void MarkMutationCompleted() => MutationCompleted = true;

    /// <summary>Appends Committed. A journal failure does not undo the mutation: it is returned in <paramref name="journalError"/>.</summary>
    public JournalEntry Commit(out string? journalError)
    {
        var committed = _prepared with { State = JournalState.Committed, TimestampUtc = _clock.UtcNow };
        try
        {
            _journal.Append(committed);
            journalError = null;
        }
        catch (Exception journalException)
        {
            journalError = journalException.Message;
        }
        finally
        {
            ReleaseLiveMarker(); // only after the outcome line: until then the entry is Prepared and must look live
        }
        return committed;
    }

    /// <summary>
    /// Appends Failed for <paramref name="failure"/> when Prepared was written. Returns the record (null when none was
    /// due) and the journal error message if the append itself failed.
    /// </summary>
    public JournalEntry? Fail(Exception failure, out string? journalError)
    {
        journalError = null;
        if (!IsPrepared && !_failWithoutPrepared)
        {
            ReleaseLiveMarker();
            return null;
        }
        var (errorCode, errorText) = JournalErrors.ForJournal(failure);
        var failed = _prepared with { State = JournalState.Failed, TimestampUtc = _clock.UtcNow, Error = errorText, ErrorCode = errorCode };
        try
        {
            _journal.Append(failed);
        }
        catch (Exception journalException)
        {
            journalError = journalException.Message;
        }
        finally
        {
            ReleaseLiveMarker(); // only after the outcome line (see Commit)
        }
        return failed;
    }
}

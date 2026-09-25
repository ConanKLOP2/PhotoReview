using System.Globalization;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Model;

namespace PhotoReview.Core.FileActions;

/// <summary>
/// INV-6 / ADR 0003 startup step (restored in review r7; lost in T46d): reconcile every pending journal operation
/// (full scan) and bootstrap the Undo history from the recent committed Moves.
/// <para>The journal scan and file checks run on the thread pool, so the first image is not delayed; only the
/// in-memory Undo history is updated on the UI thread (<see cref="IUiScheduler"/>), below any Move the user already
/// made meanwhile. Entries prepared after <see cref="RunAsync"/> started (actions of this process) are left alone.</para>
/// </summary>
public static class JournalStartupRecovery
{
    /// <summary>Returns the entries reconcile had to mark Failed (the caller points the user to Recovery). Never throws.</summary>
    public static async Task<IReadOnlyList<JournalEntry>> RunAsync(
        OperationJournal journal, UndoService undo, IClock clock, IUiScheduler uiScheduler, ILog? log = null)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(undo);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(uiScheduler);

        var startedUtc = clock.UtcNow;
        try
        {
            var (reconciled, history) = await Task.Run(() =>
            {
                // Reconcile first: a pending Move it confirms as Committed is then part of the Undo history.
                var outcome = journal.ReconcilePendingOperations(preparedBeforeUtc: startedUtc);
                return (outcome, undo.ReadStartupHistory());
            }).ConfigureAwait(false);

            await uiScheduler.InvokeAsync(() => undo.SeedHistory(history)).ConfigureAwait(false);

            var failed = reconciled.Where(entry => entry.State == JournalState.Failed).ToList();
            if (reconciled.Count > 0)
                log?.Warn(string.Format(CultureInfo.InvariantCulture,
                    "Startup journal reconcile: {0} pending operation(s), {1} failed.", reconciled.Count, failed.Count));
            return failed;
        }
        catch (Exception ex)
        {
            // A locked/unreadable journal must not take the viewer down; the Recovery window can still be opened.
            log?.Error("Startup journal reconcile failed", ex);
            return [];
        }
    }
}

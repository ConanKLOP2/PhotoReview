using System.Globalization;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Model;

namespace PhotoReview.Core.FileActions;

/// <summary>
/// INV-6 / ADR 0003 startup step (restored in review r7; lost in T46d): reconcile every pending journal operation
/// (full scan). Decision P03 (2026-09-27): Undo is limited to the current session, so this step no longer seeds the
/// Undo stack from journal history — after a restart (or another window in <c>InstanceMode.PerFolder</c>), Ctrl+Z
/// has nothing to undo until the user makes a Move/Recycle in this session.
/// <para>The journal scan and file checks run on the thread pool, so the first image is not delayed.</para>
/// </summary>
public static class JournalStartupRecovery
{
    /// <summary>Returns the entries reconcile had to mark Failed (the caller points the user to Recovery). Never throws.</summary>
    public static async Task<IReadOnlyList<JournalEntry>> RunAsync(
        OperationJournal journal, IClock clock, ILog? log = null)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(clock);

        var startedUtc = clock.UtcNow;
        try
        {
            var reconciled = await Task.Run(
                () => journal.ReconcilePendingOperations(preparedBeforeUtc: startedUtc)).ConfigureAwait(false);

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

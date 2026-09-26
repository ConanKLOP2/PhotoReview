namespace PhotoReview.Core.Catalog;

/// <summary>
/// Keeps the Explorer-order query of the folder load that is (or was last) running, so the Diagnostics window can show
/// the real result without a second query. Nothing is read from disk or Explorer here; <see cref="Current"/> only
/// returns a snapshot that already completed in memory.
/// </summary>
public sealed class LatestExplorerSnapshot
{
    private volatile Task<ExplorerViewSnapshot>? _task;

    /// <summary>Records the query of a new load; null when that load does not use the Explorer order.</summary>
    public void Set(Task<ExplorerViewSnapshot>? task) => _task = task;

    /// <summary>The completed snapshot of the latest load, or null when none was queried, it is still running or it failed.</summary>
    public ExplorerViewSnapshot? Current => _task is { IsCompletedSuccessfully: true } task ? task.Result : null;
}

namespace PhotoReview.App;

/// <summary>Helpers for deliberately fire-and-forget tasks.</summary>
internal static class TaskLogging
{
    /// <summary>
    /// Observes a fire-and-forget task: a fault is logged with <paramref name="context"/> instead of surfacing context-free
    /// from <c>TaskScheduler.UnobservedTaskException</c> at GC time. Cancellation is not a fault and stays silent.
    /// </summary>
    public static void FireAndLog(this Task task, string context)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (task.IsCompletedSuccessfully) return;
        _ = task.ContinueWith(
            t => AppLog.Error(context, t.Exception?.GetBaseException()),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}

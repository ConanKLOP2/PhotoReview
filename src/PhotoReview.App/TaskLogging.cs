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

    /// <summary>
    /// Starts a fire-and-forget operation whose start itself may throw synchronously (a non-async method returning a task, e.g.
    /// one that touches a scheduler disposed after the window closed): a synchronous throw is logged (cancellation silent)
    /// instead of escaping into the caller, and the returned task is observed like <see cref="FireAndLog(Task, string)"/>.
    /// A null task (null-conditional call on an absent collaborator) is a no-op.
    /// </summary>
    public static void FireAndLog(Func<Task?> start, string context)
    {
        ArgumentNullException.ThrowIfNull(start);
        try
        {
            start()?.FireAndLog(context);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Error(context, ex);
        }
    }
}

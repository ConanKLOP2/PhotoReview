namespace PhotoReview.TestSupport;

/// <summary>
/// Extensions for adding timeout guards to async test operations.
/// </summary>
public static class AwaitExtensions
{
    /// <summary>
    /// Awaits a task with a timeout. If the task does not complete within the timeout,
    /// throws a <see cref="TimeoutException"/> naming the operation.
    /// </summary>
    /// <param name="task">The task to await.</param>
    /// <param name="timeout">Maximum time to wait for completion.</param>
    /// <param name="what">Short description of what is being awaited (for error message).</param>
    /// <returns>A task that completes when the source task completes or the timeout expires.</returns>
    /// <exception cref="TimeoutException">Thrown when timeout is exceeded.</exception>
    public static async Task WithTimeout(this Task task, TimeSpan timeout, string what)
    {
        try
        {
            await task.WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"Timeout after {timeout.TotalSeconds:F1}s waiting for: {what}");
        }
    }

    /// <summary>
    /// Awaits a task with a timeout and returns its result. If the task does not complete within the timeout,
    /// throws a <see cref="TimeoutException"/> naming the operation.
    /// </summary>
    /// <typeparam name="T">The task result type.</typeparam>
    /// <param name="task">The task to await.</param>
    /// <param name="timeout">Maximum time to wait for completion.</param>
    /// <param name="what">Short description of what is being awaited (for error message).</param>
    /// <returns>The result of the source task.</returns>
    /// <exception cref="TimeoutException">Thrown when timeout is exceeded.</exception>
    public static async Task<T> WithTimeout<T>(this Task<T> task, TimeSpan timeout, string what)
    {
        try
        {
            return await task.WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"Timeout after {timeout.TotalSeconds:F1}s waiting for: {what}");
        }
    }
}

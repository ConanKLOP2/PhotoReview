namespace PhotoReview.TestSupport;

/// <summary>
/// Deadline-bounded polling for conditions that a background component reaches on its own schedule.
/// Prefer a real signal (TaskCompletionSource, event) when the code under test offers one.
/// </summary>
public static class Wait
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Polls <paramref name="condition"/> until it is true, or throws <see cref="TimeoutException"/> naming
    /// <paramref name="what"/>. Uses <c>Task.Delay(10)</c> between checks, never <c>Task.Yield</c>: a yield loop
    /// busy-spins a pool thread and starved the code under test on 2-vCPU CI.
    /// </summary>
    public static async Task UntilAsync(Func<bool> condition, string what, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(condition);
        var deadline = DateTime.UtcNow + (timeout ?? DefaultTimeout);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out waiting for: {what}");
            await Task.Delay(10);
        }
    }
}

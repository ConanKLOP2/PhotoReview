namespace PhotoReview.TestSupport;

/// <summary>
/// Deadline-bounded polling for conditions that a background component reaches on its own schedule.
/// Prefer a real signal (TaskCompletionSource, event) when the code under test offers one.
/// </summary>
public static class Wait
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Hang bound (not a speed budget) for a test thread that builds WPF/HWND state from a cold process: theme/font load and
    /// JIT on a busy 2-vCPU runner can legitimately take longer than any short fixed ceiling although nothing is wrong.
    /// </summary>
    public static readonly TimeSpan ColdStartHangBound = TimeSpan.FromSeconds(120);

    /// <summary>
    /// Joins a test-owned thread under a generous hang bound and, when it really hangs, fails with what was waited for, how long
    /// and the thread state (instead of a bare "timed out"). Start the thread with <c>IsBackground = true</c> so a hung one
    /// cannot keep the test host alive.
    /// </summary>
    public static void JoinOrFail(Thread thread, string what, TimeSpan? bound = null)
    {
        ArgumentNullException.ThrowIfNull(thread);
        var limit = bound ?? ColdStartHangBound;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        if (!thread.Join(limit))
        {
            throw new TimeoutException(
                $"{what} did not finish within {limit.TotalSeconds:0} s (waited {stopwatch.Elapsed.TotalSeconds:0.0} s, thread state {thread.ThreadState}).");
        }
    }

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

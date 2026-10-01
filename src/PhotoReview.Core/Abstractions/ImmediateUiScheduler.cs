namespace PhotoReview.Core.Abstractions;

/// <summary>
/// Trình điều phối UI tức thì (dùng cho test hoặc CLI không có Dispatcher).
/// </summary>
public sealed class ImmediateUiScheduler : IUiScheduler
{
    public static readonly ImmediateUiScheduler Instance = new();

    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        action();
    }

    public Task InvokeAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        // RV-S07: like Dispatcher.InvokeAsync(...).Task, a failing action faults the returned task instead of throwing at
        // the call, so code that is correct against the real dispatcher behaves the same here (tests, CLI).
        try
        {
            action();
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            return Task.FromException(ex);
        }
    }

    public async ValueTask YieldAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.Yield();
    }
}

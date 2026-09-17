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
        action();
        return Task.CompletedTask;
    }

    public async ValueTask YieldAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.Yield();
    }
}

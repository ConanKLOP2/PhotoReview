using System.Windows.Threading;
using PhotoReview.Core.Abstractions;

namespace PhotoReview.App.Services;

/// <summary>
/// Trình điều phối UI dựa trên WPF Dispatcher.
/// </summary>
public sealed class DispatcherUiScheduler : IUiScheduler
{
    private readonly Dispatcher _dispatcher;

    public DispatcherUiScheduler(Dispatcher? dispatcher = null)
    {
        _dispatcher = dispatcher ?? (System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher);
    }

    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _dispatcher.BeginInvoke(action);
    }

    public Task InvokeAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return _dispatcher.InvokeAsync(action).Task;
    }

    public async ValueTask YieldAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Dispatcher.Yield(DispatcherPriority.Background);
    }
}

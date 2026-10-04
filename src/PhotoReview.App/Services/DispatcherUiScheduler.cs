using System.Windows.Threading;
using PhotoReview.Core.Abstractions;

namespace PhotoReview.App.Services;

/// <summary>
/// Trình điều phối UI dựa trên WPF Dispatcher.
/// </summary>
public sealed class DispatcherUiScheduler : IUiScheduler
{
    private readonly Dispatcher _dispatcher;

    /// <param name="applicationDispatcher">Seam for the application dispatcher (default: <c>Application.Current?.Dispatcher</c>).</param>
    public DispatcherUiScheduler(Dispatcher? dispatcher = null, Func<Dispatcher?>? applicationDispatcher = null)
    {
        _dispatcher = dispatcher ?? (applicationDispatcher ?? AmbientDispatcher.Application)() ?? Dispatcher.CurrentDispatcher;
    }

    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _ = _dispatcher.BeginInvoke(action);
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

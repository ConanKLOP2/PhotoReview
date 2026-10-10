using System.Windows.Threading;
using PhotoReview.Core.Abstractions;

namespace PhotoReview.App.Services;

/// <summary>
/// Trình điều phối UI dựa trên WPF Dispatcher. C-04: cũng là <see cref="IUiDispatcher"/> (mức ưu tiên ánh xạ theo tên sang
/// <see cref="DispatcherPriority"/>), để mã dùng chung chỉ phụ thuộc interface.
/// </summary>
public sealed class DispatcherUiScheduler : IUiDispatcher
{
    private readonly Dispatcher _dispatcher;

    /// <param name="applicationDispatcher">Seam for the application dispatcher (default: <c>Application.Current?.Dispatcher</c>).</param>
    public DispatcherUiScheduler(Dispatcher? dispatcher = null, Func<Dispatcher?>? applicationDispatcher = null)
    {
        _dispatcher = dispatcher ?? (applicationDispatcher ?? AmbientDispatcher.Application)() ?? Dispatcher.CurrentDispatcher;
    }

    public bool CheckAccess() => _dispatcher.CheckAccess();

    public void Post(Action action) => Post(action, UiPriority.Normal);

    public void Post(Action action, UiPriority priority)
    {
        ArgumentNullException.ThrowIfNull(action);
        _ = _dispatcher.BeginInvoke(action, ToDispatcherPriority(priority));
    }

    public Task InvokeAsync(Action action) => InvokeAsync(action, UiPriority.Normal);

    public Task InvokeAsync(Action action, UiPriority priority)
    {
        ArgumentNullException.ThrowIfNull(action);
        return _dispatcher.InvokeAsync(action, ToDispatcherPriority(priority)).Task;
    }

    public ValueTask YieldAsync(CancellationToken cancellationToken = default) => YieldAsync(UiPriority.Background, cancellationToken);

    public async ValueTask YieldAsync(UiPriority priority, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Dispatcher.Yield(ToDispatcherPriority(priority));
    }

    /// <summary>The C-04 mapping by name (Send->Send, Normal->Normal, Render->Render, Background->Background); an undefined value is Normal.</summary>
    internal static DispatcherPriority ToDispatcherPriority(UiPriority priority) => priority switch
    {
        UiPriority.Send => DispatcherPriority.Send,
        UiPriority.Normal => DispatcherPriority.Normal,
        UiPriority.Render => DispatcherPriority.Render,
        UiPriority.Background => DispatcherPriority.Background,
        _ => DispatcherPriority.Normal,
    };
}

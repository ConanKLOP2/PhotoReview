using System.Windows.Threading;

namespace PhotoReview.App.Services;

/// <summary>The WPF application's dispatcher, when an <see cref="System.Windows.Application"/> exists (null in headless hosts).</summary>
internal static class AmbientDispatcher
{
    public static readonly Func<Dispatcher?> Application = static () => System.Windows.Application.Current?.Dispatcher;
}

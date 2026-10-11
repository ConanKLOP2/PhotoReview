using PhotoReview.App.Composition;
using PhotoReview.App.Coordinators;
using ReviewMetrics = PhotoReview.Core.Diagnostics.ReviewMetrics;

namespace PhotoReview.App.Services;

/// <summary>
/// C-16 (WP-09): the WPF app's <see cref="IPresentationSinkFactory"/> - builds the <see cref="WpfPresentationSink"/> the shared
/// composition root hands to <c>ImagePresenter</c>, counting off-thread updates into the app's <see cref="ReviewMetrics"/>.
/// </summary>
public sealed class WpfPresentationSinkFactory(ReviewMetrics metrics) : IPresentationSinkFactory
{
    public IPresentationSink Create(MainViewModelSinkCallbacks callbacks)
    {
        ArgumentNullException.ThrowIfNull(callbacks);
        return new WpfPresentationSink(
            onSetCurrentImage: callbacks.SetCurrentImage,
            onSetStatusText: callbacks.SetStatusText,
            onApplyInitialViewMode: callbacks.ApplyInitialViewMode,
            onPresented: callbacks.OnPresented,
            metrics: metrics);
    }
}

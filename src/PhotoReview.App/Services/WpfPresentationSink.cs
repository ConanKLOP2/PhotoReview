using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using PhotoReview.App.Coordinators;
using ReviewMetrics = PhotoReview.Core.Diagnostics.ReviewMetrics;

namespace PhotoReview.App.Services;

/// <summary>
/// Cầu nối tiếp nhận kết quả hiển thị ảnh từ ImagePresenter sang WPF và ViewModel.
/// </summary>
public sealed class WpfPresentationSink : IPresentationSink
{
    private readonly Action<object?, bool>? _onSetCurrentImage;
    private readonly Action<string>? _onSetStatusText;
    private readonly Action? _onApplyInitialViewMode;
    private readonly Action<string>? _onPresented;
    private readonly Action<long, string, long>? _onTracePresented;
    private readonly Dispatcher? _dispatcher;
    private readonly ReviewMetrics? _metrics;
    private int _crossThreadLogged;
    private RenderFrameTrace? _renderTrace;

    public WpfPresentationSink(
        Action<object?, bool>? onSetCurrentImage = null,
        Action<string>? onSetStatusText = null,
        Action? onApplyInitialViewMode = null,
        Action<string>? onPresented = null,
        Action<long, string, long>? onTracePresented = null,
        Dispatcher? dispatcher = null,
        ReviewMetrics? metrics = null)
    {
        _onSetCurrentImage = onSetCurrentImage;
        _onSetStatusText = onSetStatusText;
        _onApplyInitialViewMode = onApplyInitialViewMode;
        _onPresented = onPresented;
        _onTracePresented = onTracePresented;
        _dispatcher = dispatcher ?? System.Windows.Application.Current?.Dispatcher;
        _metrics = metrics;
    }

    private void InvokeUi(Action action)
    {
        if (_dispatcher is not null && !_dispatcher.CheckAccess())
        {
            // AR04 / ADR 0005: the App layer is UI-affine, so this safety-net branch should never be
            // taken; count it (Diagnostics: CrossThreadPresentCount, expected 0) and log the first hit.
            _metrics?.RecordCrossThreadPresent();
            if (AppLog.Enabled && Interlocked.Exchange(ref _crossThreadLogged, 1) == 0)
            {
                AppLog.Warn($"WpfPresentationSink: update arrived off the UI thread (managed thread {Environment.CurrentManagedThreadId}); marshalled via Dispatcher.Invoke. ADR 0005 expects 0.\n{Environment.StackTrace}");
            }
            _dispatcher.Invoke(action);
        }
        else
        {
            action();
        }
    }

    public void SetCurrentImage(object? image, bool isFileChange = false) => InvokeUi(() => _onSetCurrentImage?.Invoke(image, isFileChange));

    public void SetStatusText(string status) => InvokeUi(() => _onSetStatusText?.Invoke(status));

    /// <summary>
    /// PR-B: set by <c>MainWindow</c> after constructing <c>PointerInputController</c> (<c>_pointer.ApplyInitialViewAsync</c>)
    /// so the initial-view application can do Fit width/Fit height scroll placement, which needs the surface the
    /// pointer controller owns. Overrides the constructor's <c>onApplyInitialViewMode</c> callback when set.
    /// </summary>
    public Action? ApplyInitialViewModeOverride { get; set; }

    public void ApplyInitialViewMode() => InvokeUi(() => (ApplyInitialViewModeOverride ?? _onApplyInitialViewMode)?.Invoke());

    public void OnPresented(string path) => InvokeUi(() => _onPresented?.Invoke(path));

    public void TracePresented(long token, string kind, long assignedTimestamp)
    {
        if (_onTracePresented is not null)
        {
            _onTracePresented(token, kind, assignedTimestamp);
            return;
        }

        InvokeUi(() =>
        {
            if (PhotoReviewPerf.Log.IsEnabled())
            {
                // perf(render-metric): CompositionTarget.Rendering fires at the START of a frame
                // (before layout/render run), so the FIRST tick after the assign only measures the
                // dispatcher/vsync wait, not the actual pixel work -- that is still reported as
                // Rendered/Presented below, unchanged, for continuity with existing consumers. The
                // SECOND tick means the frame that actually contains the new image has finished
                // rendering, which is the more accurate "time to see the new image" proxy; it is
                // reported as a separate RenderedFrame event instead of replacing the first.
                // RV-A17: one pending trace at a time (RenderFrameTrace replaces it), so a minimized window that never
                // renders cannot pile up Rendering handlers.
                _renderTrace ??= new RenderFrameTrace(
                    h => CompositionTarget.Rendering += h,
                    h => CompositionTarget.Rendering -= h,
                    (t, k, assigned) =>
                    {
                        PhotoReviewPerf.Log.Rendered(t, PhotoReviewPerf.Ms(assigned));
                        PhotoReviewPerf.Log.Presented(t, k);
                    },
                    (t, assigned) => PhotoReviewPerf.Log.RenderedFrame(t, PhotoReviewPerf.Ms(assigned)));
                _renderTrace.Start(token, kind, assignedTimestamp);
            }
        });
    }
}

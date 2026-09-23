using System;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using PhotoReview.App.Coordinators;
using PhotoReview.App.Diagnostics;
using ReviewMetrics = PhotoReview.Core.Diagnostics.ReviewMetrics;

namespace PhotoReview.App.Services;

/// <summary>
/// Cầu nối tiếp nhận kết quả hiển thị ảnh từ ImagePresenter sang WPF và ViewModel.
/// </summary>
public sealed class WpfPresentationSink : IPresentationSink
{
    private readonly Action<object?>? _onSetCurrentImage;
    private readonly Action<string>? _onSetStatusText;
    private readonly Action? _onApplyInitialViewMode;
    private readonly Action<string>? _onPresented;
    private readonly Action<long, string, long>? _onTracePresented;
    private readonly Dispatcher? _dispatcher;
    private readonly ReviewMetrics? _metrics;
    private int _crossThreadLogged;

    public WpfPresentationSink(
        Action<object?>? onSetCurrentImage = null,
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

    public void SetCurrentImage(object? image) => InvokeUi(() => _onSetCurrentImage?.Invoke(image));

    public void SetStatusText(string status) => InvokeUi(() => _onSetStatusText?.Invoke(status));

    public void ApplyInitialViewMode() => InvokeUi(() => _onApplyInitialViewMode?.Invoke());

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
                EventHandler? handler = null;
                handler = (_, _) =>
                {
                    CompositionTarget.Rendering -= handler;
                    PhotoReviewPerf.Log.Rendered(token, PhotoReviewPerf.Ms(assignedTimestamp));
                    PhotoReviewPerf.Log.Presented(token, kind);
                };
                CompositionTarget.Rendering += handler;
            }
        });
    }
}

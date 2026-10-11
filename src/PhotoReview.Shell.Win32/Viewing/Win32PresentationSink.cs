using PhotoReview.App;
using PhotoReview.App.Composition;
using PhotoReview.App.Coordinators;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Shell.Win32.Startup;

namespace PhotoReview.Shell.Win32.Viewing;

/// <summary>
/// WP-21 (C-16): <see cref="IPresentationSink"/> của shell Win32, thay <c>WpfPresentationSink</c>. Nhận kết quả trình bày từ
/// <c>ImagePresenter</c> (dựng trong đồ thị dùng chung TRƯỚC khi cửa sổ tồn tại) và:
/// <list type="bullet">
/// <item>chuyển callback của view model (kích thước nguồn -&gt; ViewerState, trạng thái, chế độ xem đầu, <c>OnPresented</c>) và đẩy
/// ảnh sang <see cref="ShellPresentationHub"/> SAU khi ViewerState đã cập nhật (như thứ tự WPF: bề mặt đổi ảnh và layout sau);</item>
/// <item>giữ ADR 0005: mọi cập nhật phải ở UI thread; lọt sang thread khác thì được đăng lên dispatcher (không chặn thread gọi,
/// FIFO nên giữ thứ tự) và đếm vào <c>ReviewMetrics.CrossThreadPresentCount</c> (kỳ vọng 0), log lần đầu - như
/// <c>WpfPresentationSink.InvokeUi</c> (nó dùng Dispatcher.Invoke đồng bộ; shell không được chặn trên Task);</item>
/// <item>mốc đo <c>Rendered/Presented/RenderedFrame</c> do hub phát khi khung CÓ ẢNH ĐÓ thật sự được Present (C-18).</item>
/// </list>
/// </summary>
internal sealed class Win32PresentationSink : IPresentationSink
{
    private readonly ShellPresentationHub _hub;
    private readonly MainViewModelSinkCallbacks _callbacks;
    private readonly IUiDispatcher? _dispatcher;
    private readonly ReviewMetrics? _metrics;
    private int _crossThreadLogged;

    internal Win32PresentationSink(ShellPresentationHub hub, MainViewModelSinkCallbacks callbacks, IUiDispatcher? dispatcher = null,
        ReviewMetrics? metrics = null)
    {
        _hub = hub ?? throw new ArgumentNullException(nameof(hub));
        _callbacks = callbacks ?? throw new ArgumentNullException(nameof(callbacks));
        _dispatcher = dispatcher;
        _metrics = metrics;
    }

    public void SetCurrentImage(object? image, bool isFileChange = false) =>
        InvokeUi(() =>
        {
            // Thứ tự như WPF: view model cập nhật ViewerState (kích thước nguồn; phát ImageChanging trước đó) TRƯỚC, rồi bề
            // mặt mới đổi ảnh và layout.
            _callbacks.SetCurrentImage(image, isFileChange);
            _hub.SetCurrentImage(image, isFileChange);
        });

    public void SetStatusText(string status) => InvokeUi(() => _callbacks.SetStatusText(status));

    public void ApplyInitialViewMode() => InvokeUi(_callbacks.ApplyInitialViewMode);

    public void OnPresented(string path) => InvokeUi(() => _callbacks.OnPresented(path));

    public void TracePresented(long token, string kind, long assignedTimestamp) =>
        InvokeUi(() => _hub.AddTrace(token, kind, assignedTimestamp));

    private void InvokeUi(Action action)
    {
        if (_dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            _metrics?.RecordCrossThreadPresent();
            if (AppLog.Enabled && Interlocked.Exchange(ref _crossThreadLogged, 1) == 0)
            {
                AppLog.Warn("Win32PresentationSink: update arrived off the UI thread (managed thread "
                    + $"{Environment.CurrentManagedThreadId}); marshalled to the UI dispatcher. ADR 0005 expects 0.");
            }

            // Không chặn thread gọi (ADR 0005: không block trên Task); thứ tự giữ nguyên vì dispatcher chạy theo FIFO.
            dispatcher.Post(action);
            return;
        }

        action();
    }
}

/// <summary>C-16: <see cref="IPresentationSinkFactory"/> của shell Win32 (đăng ký bởi <c>AddShellViewing</c>).</summary>
internal sealed class Win32PresentationSinkFactory(ShellPresentationHub hub, IUiDispatcher? dispatcher = null, ReviewMetrics? metrics = null)
    : IPresentationSinkFactory
{
    public IPresentationSink Create(MainViewModelSinkCallbacks callbacks) =>
        new Win32PresentationSink(hub, callbacks, dispatcher, metrics);
}

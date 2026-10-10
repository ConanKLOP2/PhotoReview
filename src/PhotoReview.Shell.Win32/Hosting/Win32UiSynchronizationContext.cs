using System.Runtime.ExceptionServices;
using PhotoReview.Core.Abstractions;

namespace PhotoReview.Shell.Win32.Hosting;

/// <summary>
/// <see cref="SynchronizationContext"/> của UI thread Win32 (C-04, ADR 0005 / N-5): mọi continuation <c>await</c> bắt trên
/// UI thread quay về đúng thread đó. <see cref="Post"/> = <see cref="UiPriority.Normal"/>. Cài trên UI thread TRƯỚC khi tạo
/// service (<see cref="Install"/>), để code dùng chung không cần biết dispatcher.
/// </summary>
internal sealed class Win32UiSynchronizationContext : SynchronizationContext
{
    private readonly Win32UiDispatcher _dispatcher;

    public Win32UiSynchronizationContext(Win32UiDispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    internal Win32UiDispatcher Dispatcher => _dispatcher;

    /// <summary>Cài context cho thread hiện tại (phải là UI thread của dispatcher); Dispose khôi phục context cũ.</summary>
    public static IDisposable Install(Win32UiDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        dispatcher.VerifyAccess();
        var previous = Current;
        SetSynchronizationContext(new Win32UiSynchronizationContext(dispatcher));
        return new Restore(previous);
    }

    public override void Post(SendOrPostCallback d, object? state)
    {
        ArgumentNullException.ThrowIfNull(d);
        // Dispatcher đã dừng (cửa sổ đóng, app thoát): continuation không còn chỗ chạy - bỏ, như WPF sau Dispatcher shutdown.
        _ = _dispatcher.TryPost(d, state, UiPriority.Normal);
    }

    /// <summary>Đồng bộ: trên UI thread chạy ngay; từ thread khác chặn THREAD ĐÓ (không phải UI) tới khi UI chạy xong.</summary>
    public override void Send(SendOrPostCallback d, object? state)
    {
        ArgumentNullException.ThrowIfNull(d);
        if (_dispatcher.CheckAccess())
        {
            d(state);
            return;
        }

        // InvokeAsync (không TryPost): nếu dispatcher dừng trong lúc chờ, Task bị huỷ nên thread gọi không treo mãi.
        var invocation = _dispatcher.InvokeAsync(() => d(state), UiPriority.Send);
        ((IAsyncResult)invocation).AsyncWaitHandle.WaitOne();
        if (invocation.IsCanceled)
        {
            throw new InvalidOperationException("The UI dispatcher shut down before the synchronous call ran.");
        }

        if (invocation.Exception is { } failure)
        {
            ExceptionDispatchInfo.Throw(failure.InnerException ?? failure);
        }
    }

    public override SynchronizationContext CreateCopy() => this;

    private sealed class Restore(SynchronizationContext? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            SetSynchronizationContext(previous);
        }
    }
}

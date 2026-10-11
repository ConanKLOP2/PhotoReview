using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Diagnostics;

namespace PhotoReview.Shell.Win32.Startup;

/// <summary>Phần cửa sổ chính mà một lần mở chuyển tiếp (Q-R10) cần; <see cref="ShellMainWindow"/> triển khai.</summary>
internal interface IForwardedOpenTarget
{
    /// <summary>Cửa sổ đã quyết định đóng hoặc đã đóng: yêu cầu tới muộn không được chạm vào nó.</summary>
    bool IsClosingOrClosed { get; }

    bool IsMinimized { get; }

    /// <summary>SC_RESTORE: trả về trạng thái trước khi thu nhỏ (Maximized/fullscreen), không ép Normal (R7-5).</summary>
    void Restore();

    void Activate();

    Task OpenPathAsync(string path);
}

/// <summary>
/// Bản Win32 của <c>App.OpenForwarded</c> (UI thread, được <c>ForwardedOpenCoalescer</c> đăng lên): đưa cửa sổ ra trước rồi mở
/// đường dẫn đầu tiên. Không có đường dẫn = chỉ đưa ra trước. Một yêu cầu chạy đua với việc tắt, hay cửa sổ biến mất giữa lúc kiểm
/// tra và lúc gọi, bị bỏ qua (không ném). Lỗi mở được log (<c>Forwarded open failed</c>), không làm sập tiến trình.
/// </summary>
internal sealed class ForwardedOpenHandler(IForwardedOpenTarget target, ILog log)
{
    private readonly IForwardedOpenTarget _target = target ?? throw new ArgumentNullException(nameof(target));
    private readonly ILog _log = log ?? throw new ArgumentNullException(nameof(log));

    /// <summary>Số lần đã bỏ qua yêu cầu vì cửa sổ đang đóng (chẩn đoán/test).</summary>
    public int IgnoredCount { get; private set; }

    public Task HandleAsync(string? path)
    {
        if (_target.IsClosingOrClosed)
        {
            IgnoredCount++;
            return Task.CompletedTask;
        }

        try
        {
            if (_target.IsMinimized)
            {
                _target.Restore();
            }

            _target.Activate();
        }
        catch (InvalidOperationException ex)
        {
            IgnoredCount++;
            _log.Warn($"Forwarded launch: could not activate the main window: {ex.Message}");
            return Task.CompletedTask;
        }

        return path is null ? Task.CompletedTask : OpenAsync(path);
    }

    private async Task OpenAsync(string path)
    {
        try
        {
            await _target.OpenPathAsync(path);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.Error("Forwarded open failed", ex);
        }
    }
}

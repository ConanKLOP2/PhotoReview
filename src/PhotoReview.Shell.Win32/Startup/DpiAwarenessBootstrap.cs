using PhotoReview.Shell.Interop;

namespace PhotoReview.Shell.Win32.Startup;

/// <summary>Seam của <see cref="DpiAwarenessBootstrap"/> (test thay bằng fake; production = user32).</summary>
internal interface IDpiAwarenessNative
{
    /// <summary>Thread hiện tại đang ở DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2.</summary>
    bool IsPerMonitorV2();

    /// <summary>SetProcessDpiAwarenessContext(PER_MONITOR_AWARE_V2); false khi bị từ chối (đã đặt trước, hoặc OS cũ).</summary>
    bool TrySetPerMonitorV2();
}

internal sealed class NativeDpiAwareness : IDpiAwarenessNative
{
    public static readonly NativeDpiAwareness Instance = new();

    public bool IsPerMonitorV2() =>
        User32.AreDpiAwarenessContextsEqual(User32.GetThreadDpiAwarenessContext(), User32.DpiAwarenessContextPerMonitorAwareV2);

    public bool TrySetPerMonitorV2() => User32.SetProcessDpiAwarenessContext(User32.DpiAwarenessContextPerMonitorAwareV2);
}

/// <summary>
/// DPI PerMonitorV2 (NO-WPF-EXEC-PLAN mục 3, NE-2): <c>app.manifest</c> khai báo từ lúc nạp; đây là lưới an toàn khi tiến trình
/// bị host (testhost, trình nạp lạ) tạo mà không có manifest của exe. Phải chạy TRƯỚC cửa sổ đầu tiên (đổi awareness sau khi
/// đã có cửa sổ không còn tác dụng). Không bao giờ ném.
/// </summary>
internal static class DpiAwarenessBootstrap
{
    /// <returns>True khi PerMonitorV2 có hiệu lực sau lời gọi.</returns>
    public static bool EnsurePerMonitorV2(IDpiAwarenessNative? native = null)
    {
        var api = native ?? NativeDpiAwareness.Instance;
        try
        {
            if (api.IsPerMonitorV2())
            {
                return true;
            }

            return api.TrySetPerMonitorV2() && api.IsPerMonitorV2();
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            return false;
        }
    }
}

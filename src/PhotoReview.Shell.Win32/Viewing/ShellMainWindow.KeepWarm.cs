using PhotoReview.Shell.Rendering;
using PhotoReview.Shell.Win32.Viewing;

namespace PhotoReview.Shell.Win32;

// WP21-STALL: giữ GPU ấm khi người dùng đang tương tác (KeepWarm.cs: chính sách; Rendering/GpuKeepAlive.cs: luồng nền). Cửa sổ chỉ
// nối: nghe thông điệp nhập liệu (handler đứng đầu chuỗi, luôn trả false), và dọn khi đóng.
internal sealed partial class ShellMainWindow
{
    private GpuKeepAlive? _keepAlive;
    private KeepWarmController? _keepWarm;
    private FrameLog? _frameLog;

    /// <summary>Luồng giữ ấm (null khi tắt/WARP; test đọc <see cref="GpuKeepAlive.Ticks"/>).</summary>
    internal GpuKeepAlive? KeepAlive => _keepAlive;

    /// <summary>Bộ điều khiển giữ ấm (null khi tắt/WARP; test gọi <see cref="KeepWarmController.NoteInteraction"/>).</summary>
    internal KeepWarmController? KeepWarm => _keepWarm;

    private void RegisterKeepWarm()
    {
        _frameLog = _options.FrameLogPath is { Length: > 0 } logPath ? FrameLog.FromEnvironment(_ => logPath) : FrameLog.FromEnvironment();
        var options = _options.KeepWarm ?? KeepWarmOptions.FromEnvironment();
        if (!options.Enabled || (_options.PreferWarp && _options.KeepWarmWork is null))
        {
            return; // tắt, hoặc WARP (CPU raster, không có GPU để giữ ấm; test có thể đưa việc giả): không luồng/thiết bị/handler
        }

        _keepAlive = new GpuKeepAlive(options.IntervalMs, _options.KeepWarmWork ?? GpuKeepAlive.OpenHardwareWork);
        _keepWarm = new KeepWarmController(options, () => _window.IsActive, _keepAlive.Activate);
        _window.InsertMessageHandlerFirst(_keepWarm);
    }

    private void DisposeKeepWarm()
    {
        Interlocked.Exchange(ref _keepAlive, null)?.Dispose();
        Interlocked.Exchange(ref _frameLog, null)?.Dispose();
    }
}

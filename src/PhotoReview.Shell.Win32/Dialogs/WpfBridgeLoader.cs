using System.Runtime.CompilerServices;
using PhotoReview.Core.Abstractions;
using PhotoReview.Shell.Interop;
using PhotoReview.Shell.Win32.Hosting;
using PhotoReview.Shell.WpfBridge;

namespace PhotoReview.Shell.Win32.Dialogs;

/// <summary>
/// WP-19b (L-SHELL, NE-5): lớp DUY NHẤT của Shell.Win32 chạm tới <c>PhotoReview.Shell.WpfBridge</c>. Bọc
/// <see cref="ISecondaryWindowHost"/> (C-13b) thành <see cref="ISecondaryWindowPort"/> cho <see cref="Win32DialogService"/> và
/// <c>Win32ZoomPromptService</c>, và nối <see cref="ISecondaryWindowMessagePump"/> vào <see cref="MessageLoop"/> để cửa sổ phụ WPF
/// modeless nhận phím. Mọi đường vào WPF đi qua <see cref="SecondaryWindowPorts.CreateLazy"/> -> <see cref="Create"/>
/// (<see cref="MethodImplOptions.NoInlining"/>): chỉ chạy khi mở cửa sổ phụ đầu tiên, nên shell không nạp WPF khi chưa dùng.
/// </summary>
internal sealed class WpfBridgeLoader : ISecondaryWindowPort, IRawMessageFilter, IDisposable
{
    private readonly ISecondaryWindowHost _host;
    private readonly ISecondaryWindowMessagePump? _pump;
    private readonly MessageLoop? _loop;
    private bool _disposed;

    private WpfBridgeLoader(ISecondaryWindowHost host, MessageLoop? loop)
    {
        _host = host;
        _pump = host as ISecondaryWindowMessagePump;
        _loop = loop;
        loop?.AddMessageFilter(this);
    }

    /// <summary>Tạo host thật. Gọi qua <see cref="SecondaryWindowPorts"/>: lỗi nạp assembly bắt ở đó, ngoài lớp này.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static WpfBridgeLoader Create(IServiceProvider services, MessageLoop? loop)
        => new(SecondaryWindowHostFactory.Create(services), loop);

    public bool ShowSettings(nint ownerHwnd, SettingsTarget target) => _host.ShowSettings(ownerHwnd, target);

    public void ShowRecovery(nint ownerHwnd) => _host.ShowRecovery(ownerHwnd);

    public void ShowDiagnostics(nint ownerHwnd) => _host.ShowDiagnostics(ownerHwnd);

    public void ShowBenchmark(nint ownerHwnd, string? folder) => _host.ShowBenchmark(ownerHwnd, folder);

    public void ShowSkippedFiles(nint ownerHwnd, IReadOnlyList<SkippedEntry> entries) => _host.ShowSkippedFiles(ownerHwnd, entries);

    public bool ShowBatchReview(nint ownerHwnd, IReadOnlyList<BatchReviewItem> items) => _host.ShowBatchReview(ownerHwnd, items);

    public int? PromptCustomZoom(nint ownerHwnd, int currentPercent) => _host.PromptCustomZoom(ownerHwnd, currentPercent);

    public bool PreFilterMessage(in WindowMessage message) => false;

    public bool PreFilterRawMessage(in Msg message)
        => _pump is { HasModelessWindows: true } pump
           && pump.PreFilterMessage(message.Hwnd, message.Message, message.WParam, message.LParam, message.Time, message.PointX, message.PointY);

    /// <summary>Gỡ bộ lọc phím và đóng cửa sổ modeless còn mở.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _loop?.RemoveMessageFilter(this);
        (_host as IDisposable)?.Dispose();
    }
}

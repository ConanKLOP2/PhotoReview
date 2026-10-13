using System.Runtime.CompilerServices;
using PhotoReview.Core.Abstractions;
using PhotoReview.Shell.Interop;
using PhotoReview.Shell.Win32.Dialogs;
using PhotoReview.Shell.Win32.Hosting;

namespace PhotoReview.Shell.WpfBridge.Tests;

/// <summary>
/// WP-35 GĐ5: bộ nối CHỈ DÙNG TRONG TEST (trước đây là <c>WpfBridgeLoader</c> của Shell.Win32, đã gỡ cùng tham chiếu
/// Shell.Win32 -> Shell.WpfBridge). Bọc <see cref="ISecondaryWindowHost"/> (C-13b) thành <see cref="ISecondaryWindowPort"/> của
/// Shell.Win32 và nối <see cref="ISecondaryWindowMessagePump"/> vào <see cref="MessageLoop"/> như shell từng làm, để các test của
/// cầu (host, cửa sổ WPF thật, nạp lười) vẫn chạy trên HWND thật cho tới khi WP-37 xoá project WPF. Shell thật KHÔNG dùng lớp này.
/// </summary>
internal sealed class WpfBridgePort : ISecondaryWindowPort, IRawMessageFilter, IDisposable
{
    private readonly ISecondaryWindowHost _host;
    private readonly ISecondaryWindowMessagePump? _pump;
    private readonly MessageLoop? _loop;
    private bool _disposed;

    private WpfBridgePort(ISecondaryWindowHost host, MessageLoop? loop)
    {
        _host = host;
        _pump = host as ISecondaryWindowMessagePump;
        _loop = loop;
        loop?.AddMessageFilter(this);
    }

    /// <summary>Tạo host thật (chỉ khi mở cửa sổ phụ đầu tiên: <see cref="MethodImplOptions.NoInlining"/> giữ assembly WPF ngoài đường nóng).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static WpfBridgePort Create(IServiceProvider services, MessageLoop? loop)
        => new(SecondaryWindowHostFactory.Create(services), loop);

    /// <summary>Cổng nạp lười như <c>SecondaryWindowPorts.CreateLazy</c> trước GĐ5: chỉ tạo host khi <c>.Value</c> được đọc lần đầu.</summary>
    internal static Lazy<ISecondaryWindowPort?> CreateLazy(IServiceProvider services, MessageLoop? loop)
    {
        ArgumentNullException.ThrowIfNull(services);
        return new Lazy<ISecondaryWindowPort?>(() => Create(services, loop), LazyThreadSafetyMode.ExecutionAndPublication);
    }

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

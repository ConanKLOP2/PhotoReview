using Microsoft.Extensions.DependencyInjection;
using PhotoReview.App.Presenters;
using PhotoReview.App.Services;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Shell.Win32.Hosting;

namespace PhotoReview.Shell.Win32.Dialogs.Native;

/// <summary>
/// WP-35 GĐ1: cổng cửa sổ phụ KHÔNG WPF (hộp thoại Win32 gốc, AOT-an toàn). Làm được: Diagnostics, SkippedFiles, BatchReview,
/// ZoomPrompt (<see cref="Supports"/>); Settings/Recovery/Benchmark chưa có (trả false/no-op) nên <see cref="SecondaryWindowRouter"/>
/// chuyển sang cầu WPF hoặc cổng tạm. Mọi hàm gọi trên UI thread của shell; <see cref="Dispose"/> đóng các hộp thoại còn mở
/// (shell thoát giữa chừng) - sau đó mọi lệnh hiện là no-op trả về như Huỷ.
/// </summary>
internal sealed class Win32SecondaryWindowPort : ISecondaryWindowPort, IDisposable
{
    private readonly IServiceProvider _services;
    private readonly INativeDialogChrome? _chrome;
    private readonly Action<NativeDialog>? _ready;
    private readonly MessageLoop? _loop;
    private readonly List<NativeDialog> _open = [];
    private readonly int _uiThreadId = Environment.CurrentManagedThreadId;
    private bool _disposed;

    /// <param name="services">DI của shell (ReviewMetrics, LatestExplorerSnapshot, IClipboardService).</param>
    /// <param name="chrome">Giao diện tối (null = <see cref="DarkDialogChrome"/>); test thay bản ghi lại.</param>
    /// <param name="ready">Test: gọi sau khi hộp thoại đã hiện, trước vòng lặp modal (mô phỏng người dùng).</param>
    /// <param name="loop">Vòng lặp của shell: vòng modal chạy lồng trong nó (khung hình, việc nền, phím WPF modeless); null = đơn giản.</param>
    public Win32SecondaryWindowPort(IServiceProvider services, INativeDialogChrome? chrome = null, Action<NativeDialog>? ready = null,
        MessageLoop? loop = null)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _chrome = chrome;
        _ready = ready;
        _loop = loop;
    }

    /// <summary>Số hộp thoại đang mở (kể cả lồng nhau).</summary>
    internal int OpenCount => _open.Count;

    public bool Supports(SecondaryWindowKind kind)
        => kind is SecondaryWindowKind.Diagnostics or SecondaryWindowKind.SkippedFiles or SecondaryWindowKind.BatchReview
            or SecondaryWindowKind.ZoomPrompt;

    public bool ShowSettings(nint ownerHwnd, SettingsTarget target) => false;

    public void ShowRecovery(nint ownerHwnd)
    {
    }

    public void ShowBenchmark(nint ownerHwnd, string? folder)
    {
    }

    public void ShowDiagnostics(nint ownerHwnd)
    {
        if (_disposed)
        {
            return;
        }

        var snapshot = _services.GetService<ReviewMetrics>()?.Snapshot() ?? new ReviewMetrics().Snapshot();
        var display = DiagnosticsPresenter.Build(snapshot, _services.GetService<LatestExplorerSnapshot>()?.Current);
        var clipboard = _services.GetService<IClipboardService>();
        Run(new DiagnosticsDialog(display, text => clipboard?.TrySetText(text) ?? false, _chrome), ownerHwnd);
    }

    public void ShowSkippedFiles(nint ownerHwnd, IReadOnlyList<SkippedEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        Run(new ListDialog(ListDialogContent.ForSkippedFiles(entries), _chrome), ownerHwnd);
    }

    public bool ShowBatchReview(nint ownerHwnd, IReadOnlyList<BatchReviewItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return Run(new ListDialog(ListDialogContent.ForBatchReview(items), _chrome), ownerHwnd) == NativeDialog.IdOk;
    }

    public int? PromptCustomZoom(nint ownerHwnd, int currentPercent)
    {
        var dialog = new ZoomPromptDialog(currentPercent, _chrome);
        return Run(dialog, ownerHwnd) == NativeDialog.IdOk ? dialog.Value : null;
    }

    private int Run(NativeDialog dialog, nint owner)
    {
        if (_disposed)
        {
            return NativeDialog.IdCancel;
        }

        if (Environment.CurrentManagedThreadId != _uiThreadId)
        {
            throw new InvalidOperationException("Native dialogs must be shown on the shell UI thread.");
        }

        _open.Add(dialog);
        try
        {
            return dialog.ShowModal(owner, _ready, _loop);
        }
        finally
        {
            _open.Remove(dialog);
        }
    }

    /// <summary>Đóng mọi hộp thoại còn mở (lồng nhau: cái trong cùng trước). Idempotent.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        for (var i = _open.Count - 1; i >= 0; i--)
        {
            _open[i].Close();
        }
    }
}

/// <summary>
/// WP-35 GĐ1: định tuyến theo TỪNG cửa sổ - cổng Win32 cho cửa sổ nó hỗ trợ; cửa sổ chưa có thì rơi về cầu WPF (nạp lười, chỉ khi
/// cần); cầu vắng (bản AOT) thì rơi về <see cref="SecondaryWindowFallback"/>. Chủ sở hữu của hai cổng: <see cref="Dispose"/>
/// đóng cả hai (cầu chỉ khi đã nạp: không nạp WPF để dọn).
/// </summary>
internal sealed class SecondaryWindowRouter : ISecondaryWindowPort, IDisposable
{
    private readonly ISecondaryWindowPort _native;
    private readonly Lazy<ISecondaryWindowPort?> _bridge;
    private readonly SecondaryWindowFallback? _fallback;

    public SecondaryWindowRouter(ISecondaryWindowPort native, Lazy<ISecondaryWindowPort?> bridge, SecondaryWindowFallback? fallback)
    {
        _native = native ?? throw new ArgumentNullException(nameof(native));
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        _fallback = fallback;
    }

    public bool Supports(SecondaryWindowKind kind) => true;

    public bool ShowSettings(nint ownerHwnd, SettingsTarget target)
        => Pick(SecondaryWindowKind.Settings) is { } port ? port.ShowSettings(ownerHwnd, target) : _fallback?.OpenSettings() ?? false;

    public void ShowRecovery(nint ownerHwnd)
    {
        if (Pick(SecondaryWindowKind.Recovery) is { } port) port.ShowRecovery(ownerHwnd);
        else _fallback?.Recovery();
    }

    public void ShowDiagnostics(nint ownerHwnd)
    {
        if (Pick(SecondaryWindowKind.Diagnostics) is { } port) port.ShowDiagnostics(ownerHwnd);
        else _fallback?.Diagnostics();
    }

    public void ShowBenchmark(nint ownerHwnd, string? folder)
    {
        if (Pick(SecondaryWindowKind.Benchmark) is { } port) port.ShowBenchmark(ownerHwnd, folder);
        else _fallback?.Benchmark();
    }

    public void ShowSkippedFiles(nint ownerHwnd, IReadOnlyList<SkippedEntry> entries)
    {
        if (Pick(SecondaryWindowKind.SkippedFiles) is { } port) port.ShowSkippedFiles(ownerHwnd, entries);
        else _fallback?.SkippedFiles();
    }

    public bool ShowBatchReview(nint ownerHwnd, IReadOnlyList<BatchReviewItem> items)
        => Pick(SecondaryWindowKind.BatchReview) is { } port ? port.ShowBatchReview(ownerHwnd, items) : _fallback?.BatchReview() ?? false;

    public int? PromptCustomZoom(nint ownerHwnd, int currentPercent)
        => Pick(SecondaryWindowKind.ZoomPrompt) is { } port ? port.PromptCustomZoom(ownerHwnd, currentPercent) : null;

    /// <summary>Cổng Win32 nếu nó làm được; ngược lại cầu WPF (nạp lười), hoặc null khi cầu vắng/lỗi nạp.</summary>
    private ISecondaryWindowPort? Pick(SecondaryWindowKind kind)
    {
        if (_native.Supports(kind))
        {
            return _native;
        }

        try
        {
            var bridge = _bridge.Value;
            return bridge is not null && bridge.Supports(kind) ? bridge : null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        (_native as IDisposable)?.Dispose();
        if (_bridge.IsValueCreated && _bridge.Value is IDisposable bridge)
        {
            bridge.Dispose();
        }
    }
}

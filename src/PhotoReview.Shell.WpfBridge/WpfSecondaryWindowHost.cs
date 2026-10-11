using System.Runtime.CompilerServices;
using PhotoReview.Core.Abstractions;

namespace PhotoReview.Shell.WpfBridge;

/// <summary>
/// C-13b thực thi (WP-19b): mở cửa sổ phụ WPF (<c>PhotoReview.App.WpfWindows</c>) từ shell Win32, cùng tiến trình, owner = HWND của
/// shell. WPF CHỈ được nạp khi mở cửa sổ phụ đầu tiên: lớp này không có trường/chữ ký kiểu WPF; mọi chạm tới WPF nằm trong
/// <see cref="WpfSession"/>, chỉ chạm tới sau <see cref="EnsureWpf"/> (<see cref="MethodImplOptions.NoInlining"/>). Mọi hàm gọi trên UI
/// thread của shell (thread tạo host).
/// </summary>
internal sealed class WpfSecondaryWindowHost : ISecondaryWindowHost, ISecondaryWindowMessagePump, IDisposable
{
    private readonly IServiceProvider _services;
    private readonly int _uiThreadId = Environment.CurrentManagedThreadId;
    private bool _wpfLoaded;
    private int _modelessCount;
    private bool _disposed;

    public WpfSecondaryWindowHost(IServiceProvider services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
    }

    /// <summary>WPF đã được nạp bởi host này (chẩn đoán/test).</summary>
    internal bool IsWpfLoaded => _wpfLoaded;

    public bool HasModelessWindows => _modelessCount > 0;

    public bool ShowSettings(nint ownerHwnd, SettingsTarget target)
    {
        EnsureWpf();
        return WpfSession.ShowSettings(_services, ownerHwnd, target);
    }

    public void ShowRecovery(nint ownerHwnd)
    {
        EnsureWpf();
        WpfSession.ShowRecovery(_services, ownerHwnd);
    }

    public void ShowDiagnostics(nint ownerHwnd)
    {
        EnsureWpf();
        WpfSession.ShowDiagnostics(_services, ownerHwnd);
    }

    public void ShowBenchmark(nint ownerHwnd, string? folder)
    {
        EnsureWpf();
        WpfSession.ShowBenchmark(ownerHwnd, folder, opened: () => _modelessCount++, closed: () => _modelessCount--);
    }

    public void ShowSkippedFiles(nint ownerHwnd, IReadOnlyList<SkippedEntry> entries)
    {
        EnsureWpf();
        WpfSession.ShowSkippedFiles(ownerHwnd, entries);
    }

    public bool ShowBatchReview(nint ownerHwnd, IReadOnlyList<BatchReviewItem> items)
    {
        EnsureWpf();
        return WpfSession.ShowBatchReview(ownerHwnd, items);
    }

    public int? PromptCustomZoom(nint ownerHwnd, int currentPercent)
    {
        EnsureWpf();
        return WpfSession.PromptCustomZoom(ownerHwnd, currentPercent);
    }

    public bool PreFilterMessage(nint hwnd, uint message, nint wParam, nint lParam, uint time, int pointX, int pointY)
    {
        if (_modelessCount == 0)
        {
            return false;
        }

        VerifyAccess();
        return Forward(hwnd, message, wParam, lParam, time, pointX, pointY);
    }

    /// <summary>Đóng các cửa sổ modeless còn mở. Không tắt <c>Application</c> WPF (chỉ tạo một lần mỗi tiến trình).</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        VerifyAccess();
        _disposed = true;
        if (_wpfLoaded)
        {
            CloseModeless();
        }
    }

    /// <summary>Tạo <c>Application</c> WPF lười lần đầu (NoInlining: JIT của hàm gọi không nạp WPF).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void EnsureWpf()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        VerifyAccess();
        if (!_wpfLoaded)
        {
            WpfSession.EnsureApplication();
            _wpfLoaded = true;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool Forward(nint hwnd, uint message, nint wParam, nint lParam, uint time, int pointX, int pointY)
        => WpfSession.RaiseThreadMessage(hwnd, message, wParam, lParam, time, pointX, pointY);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CloseModeless() => WpfSession.CloseAll();

    private void VerifyAccess()
    {
        if (Environment.CurrentManagedThreadId != _uiThreadId)
        {
            throw new InvalidOperationException(
                "ISecondaryWindowHost must be called on the shell UI thread (WPF windows have thread affinity).");
        }
    }
}

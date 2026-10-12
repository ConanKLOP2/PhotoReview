using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using PhotoReview.App.Services;
using PhotoReview.Core.Abstractions;

namespace PhotoReview.Shell.WpfBridge;

/// <summary>
/// Phần chạm tới WPF của <see cref="WpfSecondaryWindowHost"/>: chỉ được chạm khi mở cửa sổ phụ đầu tiên. Tạo <see cref="Application"/>
/// WPF lười (<c>OnExplicitShutdown</c>, không <c>Run</c>) nếu thread chưa có; dispatcher WPF chạy nhờ message loop Win32 của shell
/// (Dispatcher gửi WM_* vào cửa sổ ẩn của nó, loop <c>DispatchMessage</c> chung) và nhờ <see cref="RaiseThreadMessage"/> cho phím.
/// </summary>
internal static partial class WpfSession
{
    private const uint MbOk = 0x0;
    private const uint MbIconWarning = 0x30;

    private static readonly List<Window> Modeless = [];

    /// <summary>Hộp modal đang trong <c>ShowDialog</c> (lồng nhau được): đóng được khi shell thoát giữa chừng (không treo vòng lồng).</summary>
    private static readonly List<Window> Modal = [];

    public static void EnsureApplication()
    {
        if (Application.Current is null)
        {
            _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        }
    }

    public static bool ShowSettings(IServiceProvider services, nint ownerHwnd, SettingsTarget target)
    {
        var window = SecondaryWindowFactory.CreateSettings(services, target);
        return window is not null && ShowModal(window, ownerHwnd) == true;
    }

    public static void ShowRecovery(IServiceProvider services, nint ownerHwnd)
    {
        var window = SecondaryWindowFactory.CreateRecovery(services,
            (title, message) => _ = MessageBoxW(IsValidWindow(ownerHwnd) ? ownerHwnd : 0, message, title, MbOk | MbIconWarning));
        if (window is not null)
        {
            ShowModal(window, ownerHwnd);
        }
    }

    public static void ShowDiagnostics(IServiceProvider services, nint ownerHwnd)
        => ShowModal(SecondaryWindowFactory.CreateDiagnostics(services), ownerHwnd);

    public static void ShowSkippedFiles(nint ownerHwnd, IReadOnlyList<SkippedEntry> entries)
        => ShowModal(SecondaryWindowFactory.CreateSkippedFiles(entries), ownerHwnd);

    public static bool ShowBatchReview(nint ownerHwnd, IReadOnlyList<BatchReviewItem> items)
        => ShowModal(SecondaryWindowFactory.CreateBatchReview(items), ownerHwnd) == true;

    public static int? PromptCustomZoom(nint ownerHwnd, int currentPercent)
        => SecondaryWindowFactory.PromptCustomZoom(IsValidWindow(ownerHwnd) ? ownerHwnd : 0, currentPercent);

    public static void ShowBenchmark(nint ownerHwnd, string? folder, Action opened, Action closed)
    {
        var window = SecondaryWindowFactory.CreateBenchmark(folder);
        SetOwner(window, ownerHwnd);
        Modeless.Add(window);
        opened();
        window.Closed += (_, _) =>
        {
            Modeless.Remove(window);
            closed();
        };
        window.Show();
    }

    /// <summary>
    /// Gửi message cho <see cref="ComponentDispatcher"/> như vòng lặp của <c>Dispatcher.Run</c> (phím, accelerator, Tab).
    /// </summary>
    public static bool RaiseThreadMessage(nint hwnd, uint message, nint wParam, nint lParam, uint time, int pointX, int pointY)
    {
        var msg = new MSG
        {
            hwnd = hwnd,
            message = (int)message,
            wParam = wParam,
            lParam = lParam,
            time = (int)time,
            pt_x = pointX,
            pt_y = pointY,
        };
        return ComponentDispatcher.RaiseThreadMessage(ref msg);
    }

    public static void CloseAll()
    {
        foreach (var window in Modal.ToArray().Reverse().Concat(Modeless.ToArray()))
        {
            window.Close();
        }
    }

    private static bool? ShowModal(Window window, nint ownerHwnd)
    {
        SetOwner(window, ownerHwnd);
        Modal.Add(window);
        try
        {
            return window.ShowDialog();
        }
        finally
        {
            Modal.Remove(window);
        }
    }

    private static void SetOwner(Window window, nint ownerHwnd)
    {
        if (IsValidWindow(ownerHwnd))
        {
            new WindowInteropHelper(window).Owner = ownerHwnd;
        }
    }

    private static bool IsValidWindow(nint hwnd) => hwnd != 0 && IsWindow(hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindow(nint hwnd);

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBoxW(nint hwnd, string text, string caption, uint type);
}

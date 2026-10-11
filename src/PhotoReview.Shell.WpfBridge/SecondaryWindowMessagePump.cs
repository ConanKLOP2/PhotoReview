namespace PhotoReview.Shell.WpfBridge;

/// <summary>
/// WP-19b (thêm vào C-13b, không đổi chữ ký <see cref="ISecondaryWindowHost"/>): điểm móc vòng lặp Win32 cho cửa sổ phụ WPF
/// modeless. Message loop của shell (Shell.Win32 <c>MessageLoop</c>) gọi <see cref="PreFilterMessage"/> cho mỗi message trước
/// <c>TranslateMessage/DispatchMessage</c> khi <see cref="HasModelessWindows"/>; nếu không, WPF không bao giờ nhận phím
/// (phím đi qua <c>ComponentDispatcher</c>, không qua WndProc - như app WinForms không gọi EnableModelessKeyboardInterop).
/// Cửa sổ modal không cần: <c>ShowDialog</c> chạy vòng lặp WPF lồng riêng.
/// </summary>
public interface ISecondaryWindowMessagePump
{
    /// <summary>Có cửa sổ phụ modeless đang mở (message loop chỉ gọi <see cref="PreFilterMessage"/> khi true).</summary>
    bool HasModelessWindows { get; }

    /// <summary>True = WPF đã xử lý message (bỏ qua Translate/Dispatch). Chỉ gọi trên UI thread của shell.</summary>
    bool PreFilterMessage(nint hwnd, uint message, nint wParam, nint lParam, uint time, int pointX, int pointY);
}

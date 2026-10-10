namespace PhotoReview.TestSupport.Golden;

/// <summary>
/// Phong bì của mọi file golden <c>tests/Fixtures/golden/*.v1.json</c> (WP-10). Không thuộc hợp đồng C-17 (các kiểu Golden* khoá ở
/// <c>Approved/contracts.v1.txt</c> là phần tử <see cref="Items"/>); phong bì chỉ mang metadata để người đọc biết file ghi thế nào.
/// </summary>
/// <param name="Schema">Tên lược đồ, ví dụ "viewport-layout".</param>
/// <param name="Version">Phiên bản lược đồ (1 cho .v1.json).</param>
/// <param name="Recorder">Thành phần ghi (bản WPF thật), không có thời gian/máy để ghi lại ra cùng byte.</param>
/// <param name="Notes">Điều kiện ghi: DPI, kích thước cửa sổ, quy ước (xem docs/refactoring/decisions/NOWPF-WP10-GOLDEN-RECORDER.md).</param>
/// <param name="Items">Các ca/kịch bản/tên phím/ca menu.</param>
public sealed record GoldenDocument<T>(string Schema, int Version, string Recorder, string Notes, IReadOnlyList<T> Items);

/// <summary>
/// G-OVL: hộp bao một panel overlay (TranslatePoint + ActualWidth/Height, toạ độ DIP trong ImageScroll/cửa sổ) với chuỗi cố định.
/// Dành cho WP-23 (bố cục overlay Win32 khớp +/- 2 px). Không thuộc C-17.
/// </summary>
/// <param name="Name">Khoá ca: "w{W}x{H}-dpi{D}-font{F}-{Panel}".</param>
/// <param name="WindowWidth">Vùng nội dung cửa sổ (DIP).</param>
/// <param name="WindowHeight">Vùng nội dung cửa sổ (DIP).</param>
/// <param name="DpiScale">Hệ số DPI gốc (VisualTreeHelper.SetRootDpi).</param>
/// <param name="OverlayFontSize">Cỡ chữ overlay (InfoOverlay.FontSize) của ca.</param>
/// <param name="Panel">ToolbarPanel | StatusPanel | FolderInfoPanel | ZoomIndicatorPanel | CapturePairBadge | ComparePanel.</param>
/// <param name="X">Góc trên-trái so với phần nội dung cửa sổ (DIP).</param>
/// <param name="Y">Góc trên-trái so với phần nội dung cửa sổ (DIP).</param>
/// <param name="Width">ActualWidth (DIP).</param>
/// <param name="Height">ActualHeight (DIP).</param>
public sealed record GoldenOverlayBox(string Name, double WindowWidth, double WindowHeight, double DpiScale, double OverlayFontSize,
    string Panel, double X, double Y, double Width, double Height);

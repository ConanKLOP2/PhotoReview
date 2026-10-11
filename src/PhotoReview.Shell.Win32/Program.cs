using PhotoReview.Shell.Win32.Startup;

namespace PhotoReview.Shell.Win32;

/// <summary>
/// Điểm vào của PhotoReview.exe (shell Win32 + Direct2D, NO-WPF-EXEC-PLAN NE-2): không có WPF <c>Application</c>.
/// Toàn bộ trình tự khởi động nằm ở <see cref="ShellStartup"/>. <c>--smoke</c> chạy đúng đường thật nhưng không khoá instance,
/// không đọc/ghi placement, không reconcile journal, không lấy focus, và đóng sau khung đầu (WP-01 smoke, mã thoát 0).
/// </summary>
internal static class Program
{
    internal const string SmokeArgument = ShellLaunch.SmokeArgument;

    [STAThread]
    private static int Main(string[] args) => ShellStartup.Run(args);
}

using PhotoReview.Shell.Win32.Startup;

namespace PhotoReview.Shell.Win32;

/// <summary>
/// Điểm vào của PhotoReview.exe (shell Win32, NO-WPF-EXEC-PLAN). WP-01: chỉ hiện một cửa sổ trống;
/// <c>--smoke</c> tạo + hiện cửa sổ (không kích hoạt) rồi đóng và thoát mã 0. WP-20 thay bằng startup thật.
/// </summary>
internal static class Program
{
    internal const string SmokeArgument = "--smoke";

    [STAThread]
    private static int Main(string[] args) =>
        BootstrapWindow.Run(smoke: Array.Exists(args, a => string.Equals(a, SmokeArgument, StringComparison.Ordinal)));
}

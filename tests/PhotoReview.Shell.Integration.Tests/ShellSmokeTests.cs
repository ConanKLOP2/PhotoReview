using System.Diagnostics;

namespace PhotoReview.Shell.Integration.Tests;

/// <summary>
/// WP-01 (NO-WPF-EXEC-PLAN thẻ WP-01): PhotoReview.exe thật khởi động, tạo + hiện một cửa sổ Win32 (không kích hoạt) và
/// thoát mã 0 với --smoke. Tiến trình con thừa kế desktop của tiến trình test, nên chạy qua tools/run-tests-hidden.ps1
/// thì cửa sổ nằm trên desktop ẩn. Mã thoát khác 0 = bước hỏng (xem BootstrapWindow.Exit*).
/// </summary>
[Trait("Category", "UI")]
[Trait("Category", "Integration")]
public sealed class ShellSmokeTests
{
    private static readonly TimeSpan ExitBound = TimeSpan.FromSeconds(60);

    [Fact]
    public void Exe_WithSmokeArgument_ShowsWindowAndExitsZero()
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "PhotoReview.exe");
        Assert.True(File.Exists(exe), $"Shell exe not found next to the tests: {exe}");

        var startInfo = new ProcessStartInfo(exe, "--smoke")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        using var process = Process.Start(startInfo);
        Assert.NotNull(process);

        var exited = process.WaitForExit(ExitBound);
        if (!exited)
        {
            process.Kill(entireProcessTree: true);
        }

        Assert.True(exited, $"PhotoReview.exe --smoke did not exit within {ExitBound.TotalSeconds:0} s");
        Assert.Equal(0, process.ExitCode);
    }
}

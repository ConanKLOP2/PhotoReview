using System.IO;
using PhotoReview.Benchmark.Cli.ShellHost;

namespace PhotoReview.Integration.Tests.ShellHost;

/// <summary>
/// WP-29 (Category=Manual): chạy probe THẬT 2 vòng (không vòng khởi động) trên bản publish R2R. Cần:
/// <c>PHOTOREVIEW_PROBE_SHELL_EXE</c> (PhotoReview.exe), tùy chọn <c>PHOTOREVIEW_PROBE_WPF_EXE</c> (PhotoReview.App.exe),
/// <c>PHOTOREVIEW_PERF_FOLDER</c> (mặc định thư mục F4). Mở cửa sổ thật trên desktop tương tác; không SendInput.
/// </summary>
[Trait("Category", "Manual")]
public sealed class StartupProbeSmokeManualTests
{
    [Fact]
    public void TwoRounds_OnPublishedExes_ProduceMetricsAndReports()
    {
        var shell = Environment.GetEnvironmentVariable("PHOTOREVIEW_PROBE_SHELL_EXE");
        var wpf = Environment.GetEnvironmentVariable("PHOTOREVIEW_PROBE_WPF_EXE");
        Assert.False(string.IsNullOrEmpty(shell), "set PHOTOREVIEW_PROBE_SHELL_EXE to the published PhotoReview.exe");
        Assert.True(File.Exists(shell), shell);
        var source = Environment.GetEnvironmentVariable("PHOTOREVIEW_PERF_FOLDER") ?? @"C:\Xiuren\[[WALLPAPER]";
        Assert.True(Directory.Exists(source), "no photo folder: " + source);

        var root = Path.Combine(Path.GetTempPath(), "pr-wp29-smoke-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var args = new List<string> { "--startup-probe", "--shell", shell!, "--source", source, "--rounds", "2", "--warmup", "0", "--keys", "6", "--out", root };
            if (!string.IsNullOrEmpty(wpf))
            {
                args.AddRange(["--wpf", wpf]);
            }

            Assert.Equal(0, StartupProbeCommand.Run(args));
            Assert.True(File.Exists(Path.Combine(root, "runs.csv")));
            Assert.True(File.Exists(Path.Combine(root, "summary.json")));
            var csv = File.ReadAllLines(Path.Combine(root, "runs.csv"));
            Assert.Equal(1 + (2 * (string.IsNullOrEmpty(wpf) ? 1 : 2)), csv.Length);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }
}

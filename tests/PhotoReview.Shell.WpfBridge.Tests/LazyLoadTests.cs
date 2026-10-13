using System.IO;
using System.Diagnostics;
using System.Globalization;
using Xunit.Abstractions;

namespace PhotoReview.Shell.WpfBridge.Tests;

/// <summary>
/// WP-19b (NE-5): WPF chỉ được nạp khi mở cửa sổ phụ đầu tiên. Chạy tiến trình sạch (<c>--probe-lazy</c> của chính exe test, vì
/// tiến trình test này có thể đã nạp WPF từ test khác) dựng cổng như shell và đọc danh sách assembly WPF ở từng bước.
/// </summary>
[Trait("Category", "UI")]
[Trait("Category", "Integration")] // spawns a child process (TEST-OS)
public sealed class LazyLoadTests(ITestOutputHelper output)
{
    private static readonly string[] WpfCore = ["PresentationCore", "PresentationFramework", "WindowsBase", "System.Xaml", "PhotoReview.App.WpfWindows"];

    [Fact]
    public async Task Wpf_IsNotLoaded_UntilTheFirstSecondaryWindowOpens()
    {
        var lines = await RunProbeAsync("--probe-lazy");
        var stages = Parse(lines);

        Assert.Empty(stages["start"]);
        Assert.Empty(stages["lazy-created"]);          // chưa đọc .Value
        Assert.Empty(stages["dialog-service-created"]); // dựng Win32DialogService không chạm cầu
        // Cầu tạo host nhưng chưa mở cửa sổ: assembly cầu có thể nạp, WPF/App.WpfWindows thì không.
        Assert.DoesNotContain(stages["port-loaded"], n => WpfCore.Contains(n));
        // Sau cửa sổ đầu tiên: WPF thật đã nạp.
        Assert.Contains("PresentationFramework", stages["after-first-window"]);
        Assert.Contains("PhotoReview.App.WpfWindows", stages["after-first-window"]);
    }

    /// <summary>
    /// WP-35 GĐ1+GĐ2 (cổng chấp nhận): DI thật của shell, sáu cửa sổ Win32 gốc (Diagnostics, SkippedFiles, BatchReview, ZoomPrompt,
    /// Benchmark modeless, ActionProfiles) mở và đóng được mà KHÔNG nạp WPF (PresentationFramework/Core, WindowsBase, System.Xaml, App.WpfWindows) và cả assembly cầu.
    /// </summary>
    [Fact]
    public async Task NativeSecondaryWindows_OpenWithoutLoadingWpfOrTheBridge()
    {
        var lines = await RunProbeAsync("--probe-native");
        var stages = Parse(lines);

        foreach (var stage in new[]
                 {
                     "shell-built", "services-resolved", "after-diagnostics", "after-four-native-windows", "after-benchmark",
                     "after-action-profiles",
                 })
        {
            Assert.DoesNotContain(stages[stage], n => WpfCore.Contains(n) || n == "PhotoReview.Shell.WpfBridge");
        }

        // Benchmark opened as ONE modeless window (a second open added none); Action profiles closed as Cancel -> null.
        Assert.Contains("RESULT batch=False zoom=null benchmark=True/True actionProfiles=null", lines);
    }

    [Fact]
    [Trait("Category", "Manual")]
    public async Task Report_NativeOpenTimes()
    {
        var lines = await RunProbeAsync("--probe-native-timing");
        foreach (var line in lines.Where(l => l.StartsWith("TIMING", StringComparison.Ordinal)))
        {
            output.WriteLine(line);
        }

        Assert.Contains(lines, l => l.StartsWith("TIMING native-batch-r8", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Manual")]
    public async Task Report_FirstAndSecondOpenTimes()
    {
        var lines = await RunProbeAsync("--probe-timing");
        foreach (var line in lines.Where(l => l.StartsWith("TIMING", StringComparison.Ordinal)))
        {
            output.WriteLine(line);
        }

        Assert.Contains(lines, l => l.StartsWith("TIMING settings-2nd", StringComparison.Ordinal));
    }

    private static Dictionary<string, string[]> Parse(IEnumerable<string> lines)
    {
        var stages = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var line in lines.Where(l => l.StartsWith("STAGE ", StringComparison.Ordinal)))
        {
            var parts = line["STAGE ".Length..].Split(" | wpf=", StringSplitOptions.None);
            stages[parts[0]] = parts[1].Length == 0 ? [] : parts[1].Split(',');
        }

        return stages;
    }

    private async Task<List<string>> RunProbeAsync(string argument)
    {
        var exe = Path.ChangeExtension(typeof(LazyLoadTests).Assembly.Location, ".exe");
        Assert.True(File.Exists(exe), $"probe exe not found: {exe}");
        var start = new ProcessStartInfo(exe, argument)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("probe did not exit within 90 s:\n" + await stdout + await stderr);
        }

        var text = await stdout;
        output.WriteLine(text);
        Assert.True(process.ExitCode == 0, $"probe exit code {process.ExitCode.ToString(CultureInfo.InvariantCulture)}:\n{text}\n{await stderr}");
        return [.. text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
    }
}

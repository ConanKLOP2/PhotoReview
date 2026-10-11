using System.Diagnostics;
using System.Globalization;
using System.Text;
using PhotoReview.Shell.Rendering;
using PhotoReview.Shell.Tests.Hosting;
using PhotoReview.Shell.Win32.Hosting;
using Xunit.Abstractions;

namespace PhotoReview.Shell.Integration.Tests.Startup;

/// <summary>
/// WP-21 (Category=Manual): thăm dò vì sao <c>Present(1)</c> chặn UI thread trên GPU thật. Cửa sổ thật trên desktop tương tác;
/// mỗi cấu hình vẽ N khung trống cách nhau 16 ms và đo từng <c>EndDrawAndPresent</c> (và thời gian chờ waitable object nếu dùng).
/// </summary>
[Collection("ShellStartup")]
[Trait("Category", "Manual")]
[Trait("Category", "UI")]
public sealed class PresentProbeReportTests(ITestOutputHelper output)
{
    private sealed record Config(string Name, RenderSurfaceOptions Options, bool Immediate = false, bool WaitWaitable = false,
        int SleepMs = 16, bool Activate = false, bool Maximize = false);

    [Fact]
    public async Task Probe_PresentTimingsAcrossConfigurations()
    {
        var sparse = Environment.GetEnvironmentVariable("PHOTOREVIEW_PROBE_SPARSE") == "1";
        var configs = sparse ? new[]
        {
            new Config("sync0 spacing 16", new RenderSurfaceOptions(), Immediate: true, SleepMs: 16),
            new Config("sync0 spacing 100", new RenderSurfaceOptions(), Immediate: true, SleepMs: 100),
            new Config("sync0 spacing 450", new RenderSurfaceOptions(), Immediate: true, SleepMs: 450),
            new Config("sync0 spacing 1000", new RenderSurfaceOptions(), Immediate: true, SleepMs: 1000),
        } : new[]
        {
            new Config("GPU sync1 L1 (mặc định)", new RenderSurfaceOptions()),
            new Config("GPU sync0 (ImmediatePresent)", new RenderSurfaceOptions(), Immediate: true),
            new Config("GPU sync1 L2", new RenderSurfaceOptions(MaxFrameLatency: 2)),
            new Config("GPU sync1 L1 + chờ waitable", new RenderSurfaceOptions(), WaitWaitable: true),
            new Config("GPU sync1 L1 vòng chặt (sleep 0)", new RenderSurfaceOptions(), SleepMs: 0),
            new Config("GPU sync1 L1 kích hoạt cửa sổ", new RenderSurfaceOptions(), Activate: true),
            new Config("WARP sync1 L1", new RenderSurfaceOptions(PreferWarp: true)),
        };
        var text = new StringBuilder();
        text.AppendLine("| Cấu hình | Khung | Present P50 | P95 | Max (ms) | Chờ waitable P50 | Max (ms) | Khung chờ >= 8 ms |");
        text.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var config in configs)
        {
            await using var ui = await UiThread.StartAsync();
            var (present, wait, warp) = await ui.InvokeAsync(() => Run(config));
            text.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"| {config.Name}{(warp ? " (WARP)" : string.Empty)} [{Timing}] {EndDrawSummary} | {present.Count} | {Pct(present, 50):F2} | {Pct(present, 95):F2} | {present.Max():F2} | {(wait.Count > 0 ? Pct(wait, 50) : 0):F2} | {(wait.Count > 0 ? wait.Max() : 0):F2} | {present.Count(p => p >= 8)} |"));
        }

        output.WriteLine(text.ToString());
        if (Environment.GetEnvironmentVariable("PHOTOREVIEW_PERF_OUT") is { Length: > 0 } outPath)
        {
            File.WriteAllText(outPath, text.ToString());
        }
    }

    private static string Timing = "?";
    private static string EndDrawSummary = string.Empty;

    private static (List<double> Present, List<double> Wait, bool Warp) Run(Config config)
    {
        var clock = new TimerFrameClock();
        using var window = new ShellWindow(new ShellWindowOptions(WidthDip: 1200, HeightDip: 800), clock);
        window.Show(config.Activate);
        var (width, height) = window.ClientSizePixels;
        using var surface = D2DRenderSurface.CreateWindowSurface(window.Hwnd, config.Options);
        surface.Resize(width, height, window.DpiScale);
        surface.ImmediatePresent = config.Immediate;
        var present = new List<double>();
        var wait = new List<double>();
        var endDraw = new List<double>();
        var timing = new PhotoReview.Platform.Windows.WindowsDisplayClock().GetTiming(window.Hwnd);
        Timing = timing is { RefreshPeriod: > 0 } known ? $"{Stopwatch.Frequency / (double)known.RefreshPeriod:F1} Hz" : "?";
        for (var i = 0; i < (config.SleepMs >= 450 ? 12 : 40); i++)
        {
            if (config.WaitWaitable)
            {
                var w = Stopwatch.GetTimestamp();
                surface.WaitForNextFrame(1000);
                wait.Add((Stopwatch.GetTimestamp() - w) * 1000.0 / Stopwatch.Frequency);
            }

            var dc = surface.BeginDraw();
            dc.Clear(new ColorF((i % 8) / 8f, 0.2f, 0.4f, 1f));
            dc.FillRectangle(new PhotoReview.App.Input.RectD(10 + (i * 5), 10, 200, 200), new ColorF(1, 1, 1, 1));
            var t = Stopwatch.GetTimestamp();
            surface.EndDrawAndPresent();
            present.Add((Stopwatch.GetTimestamp() - t) * 1000.0 / Stopwatch.Frequency);
            endDraw.Add(surface.LastEndDrawTicks * 1000.0 / Stopwatch.Frequency);
            if (config.SleepMs > 0)
            {
                Thread.Sleep(config.SleepMs);
            }
        }

        EndDrawSummary = $"endDraw P50 {Pct(endDraw, 50):F2} max {endDraw.Max():F2}";
        return (present, wait, surface.IsWarp);
    }

    private static double Pct(List<double> values, double percentile)
    {
        var sorted = values.Order().ToArray();
        return sorted[Math.Clamp((int)Math.Ceiling(percentile / 100 * sorted.Length) - 1, 0, sorted.Length - 1)];
    }
}

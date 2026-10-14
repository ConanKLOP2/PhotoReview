using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using PhotoReview.Core.Model;
using PhotoReview.Shell.Win32;
using Xunit.Abstractions;

namespace PhotoReview.Shell.Integration.Tests.Startup;

/// <summary>
/// WP-21 (Category=Manual): đo thời gian vẽ khung và độ chặn của <c>Present</c> trên GPU THẬT, cửa sổ thật trên desktop tương tác
/// (không chạy trên desktop ẩn: ở đó DXGI luôn báo Occluded). Ảnh: <c>PHOTOREVIEW_PERF_FOLDER</c> (mặc định thư mục F4) - sao
/// <c>PHOTOREVIEW_PERF_COUNT</c> (mặc định 10) JPEG ở giữa danh sách vào thư mục tạm; thiếu thì dùng JPEG nhúng 320x240.
/// Các pha: điều hướng, crossfade, zoom + nâng cấp full, pan. Mỗi pha in P50/P95/max của cả khung và riêng EndDrawAndPresent,
/// số khung <c>Present</c> chặn &gt;= 8 ms, và tải CPU toàn máy lúc đo (máy có thể bận: số chỉ so trong cùng lô).
/// Đặt <c>PHOTOREVIEW_PERF_OUT</c> để ghi bảng markdown ra tệp.
/// </summary>
[Collection("ShellStartup")]
[Trait("Category", "Manual")]
[Trait("Category", "UI")]
public sealed partial class PresentationPerfReportTests(ITestOutputHelper output)
{
    private sealed record PhaseResult(string Name, long Frames, double FrameP50, double FrameP95, double FrameMax,
        double PresentP50, double PresentP95, double PresentMax, long Blocked, double CpuPercent);

    [Fact]
    public void Frames_AfterTheFirstImage_DoNotBlockTheUiThreadInPresent()
    {
        var root = ShellRun.NewRoot("perf");
        var folder = PrepareFolder(root);
        var files = Directory.GetFiles(folder).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        Assert.True(files.Length >= 3, "cần >= 3 ảnh");
        using var run = ShellRun.Start(root, [files[0]], configure: options => options with { PreferWarp = false, Activate = false });
        Assert.True(run.WaitForMain(), "cửa sổ chính không dựng xong");
        Assert.True(SpinWait.SpinUntil(() => run.Main!.FirstImagePresented, TimeSpan.FromSeconds(60)), "ảnh đầu không vẽ");
        var main = run.Main!;
        var results = new List<PhaseResult>();
        var surfaceKind = run.OnUi(() => (main.Surface!.IsWarp ? "WARP" : "GPU thật") + " - " + main.Surface.AdapterDescription);
        Pacing.PauseMs(1500); // preload + khung đầu lắng

        // Pha 1: điều hướng (không crossfade), mỗi ảnh một khung có ảnh mới.
        results.Add(Measure(run, "Điều hướng (Next, 150 ms/ảnh)", () =>
        {
            for (var i = 0; i < Math.Min(8, files.Length - 1); i++)
            {
                run.OnUi(() => { _ = main.ViewModel.NextAsync(); });
                Pacing.PauseMs(150);
            }

            Pacing.PauseMs(400);
        }));

        // Pha 2: crossfade 200 ms (QuadraticOut), mỗi lần điều hướng chạy trọn hiệu ứng.
        run.OnUi(() =>
        {
            main.ViewModel.Settings.ImageTransition = ImageTransition.Fade;
            main.ViewModel.Settings.ImageTransitionMs = 200;
        });
        results.Add(Measure(run, "Crossfade 200 ms (Previous, 450 ms/ảnh)", () =>
        {
            for (var i = 0; i < 6; i++)
            {
                run.OnUi(() => { _ = main.ViewModel.PreviousAsync(); });
                Pacing.PauseMs(450);
            }
        }));
        run.OnUi(() => main.ViewModel.Settings.ImageTransition = ImageTransition.None);

        // Pha 3: zoom (nâng cấp preview -> full ở 100 %+), rồi pan liên tục.
        results.Add(Measure(run, "Zoom in x5 + nâng cấp full", () =>
        {
            for (var i = 0; i < 5; i++)
            {
                run.OnUi(() => main.ViewModel.ZoomIn());
                Pacing.PauseMs(500);
            }

            Pacing.PauseMs(1500); // full decode + chuyển bitmap
        }));
        results.Add(Measure(run, "Pan 120 khung (~60 Hz)", () =>
        {
            var timer = Stopwatch.StartNew();
            for (var i = 0; i < 120; i++)
            {
                var step = i;
                run.OnUi(() =>
                {
                    var viewport = main.Viewport;
                    viewport.ScrollTo(viewport.HorizontalOffset + ((step % 2 == 0) ? 6 : -4), viewport.VerticalOffset + 3);
                    main.Window.Invalidate();
                });
                var due = TimeSpan.FromMilliseconds((i + 1) * 1000.0 / 60);
                Pacing.Pause(due - timer.Elapsed);
            }
        }));
        results.Add(Measure(run, "Zoom out về Fit", () =>
        {
            run.OnUi(() => main.ViewModel.ToggleFit());
            Pacing.PauseMs(800);
        }));

        var report = Format(surfaceKind, files.Length, results);
        var slow = run.OnUi(() => main.Stats.Slow.ToArray());
        report += string.Join(Environment.NewLine, slow.Select(x => string.Create(CultureInfo.InvariantCulture,
            $"slow: frame={x.FrameMs:F1} draw={x.DrawMs:F1} endDraw={x.EndDrawMs:F1} present={x.PresentMs:F1} {x.Note}")));
        output.WriteLine(report);
        if (Environment.GetEnvironmentVariable("PHOTOREVIEW_PERF_OUT") is { Length: > 0 } outPath)
        {
            File.WriteAllText(outPath, report);
        }

        run.RequestClose();
        Assert.True(run.WaitForExit(TimeSpan.FromSeconds(60)));
    }

    private static string PrepareFolder(string root)
    {
        var source = Environment.GetEnvironmentVariable("PHOTOREVIEW_PERF_FOLDER") ?? @"C:\Xiuren\[[WALLPAPER]";
        var count = int.TryParse(Environment.GetEnvironmentVariable("PHOTOREVIEW_PERF_COUNT"), out var n) ? n : 10;
        var target = Path.Combine(root, "photos");
        Directory.CreateDirectory(target);
        if (Directory.Exists(source))
        {
            var jpegs = Directory.GetFiles(source, "*.jpg").Order(StringComparer.OrdinalIgnoreCase).ToArray();
            if (jpegs.Length >= count)
            {
                var start = (jpegs.Length - count) / 2;
                for (var i = 0; i < count; i++)
                {
                    File.Copy(jpegs[start + i], Path.Combine(target, $"{i:D2}-" + Path.GetFileName(jpegs[start + i])));
                }

                return target;
            }
        }

        for (var i = 0; i < Math.Max(count, 4); i++)
        {
            TestJpeg.WriteTo(Path.Combine(target, $"p{i:D2}.jpg"));
        }

        return target;
    }

    private static PhaseResult Measure(ShellRun run, string name, Action action)
    {
        var main = run.Main!;
        run.OnUi(() => main.Stats.Reset());
        var cpuBefore = SystemCpu();
        action();
        var cpuAfter = SystemCpu();
        return run.OnUi(() =>
        {
            var stats = main.Stats;
            var busy = cpuAfter.Busy - cpuBefore.Busy;
            var total = cpuAfter.Total - cpuBefore.Total;
            return new PhaseResult(name, stats.Total, stats.FrameMs(50), stats.FrameMs(95), stats.FrameMs(100),
                stats.PresentMs(50), stats.PresentMs(95), stats.MaxPresentMs(), stats.BlockedPresents,
                total > 0 ? 100.0 * busy / total : 0);
        });
    }

    private static string Format(string surface, int images, List<PhaseResult> results)
    {
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"Bề mặt: {surface}; ảnh: {images}; CPU logic: {Environment.ProcessorCount}");
        text.AppendLine("| Pha | Khung | Khung P50 | P95 | Max (ms) | Present P50 | P95 | Max (ms) | Present >= 8 ms | CPU toàn máy |");
        text.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var r in results)
        {
            text.AppendLine(CultureInfo.InvariantCulture,
                $"| {r.Name} | {r.Frames} | {r.FrameP50:F2} | {r.FrameP95:F2} | {r.FrameMax:F2} | {r.PresentP50:F2} | {r.PresentP95:F2} | {r.PresentMax:F2} | {r.Blocked} | {r.CpuPercent:F0} % |");
        }

        return text.ToString();
    }

    private static (long Busy, long Total) SystemCpu()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
        {
            return (0, 0);
        }

        var total = kernel + user; // kernel đã gồm idle
        return (total - idle, total);
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetSystemTimes", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemTimes(out long idle, out long kernel, out long user);
}

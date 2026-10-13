using System.Diagnostics;
using System.Globalization;
using PhotoReview.Shell.Interop;
using PhotoReview.Shell.Win32.Dialogs.SettingsD2D;
using PhotoReview.TestSupport;
using Xunit.Abstractions;
using PhotoReview.Shell.Rendering;
using static PhotoReview.Shell.Tests.Dialogs.Native.NativeDialogTestKit;
using static PhotoReview.Shell.Tests.Dialogs.SettingsD2D.D2DTestKit;

namespace PhotoReview.Shell.Tests.Dialogs.SettingsD2D;

/// <summary>
/// SPIKE D2D Settings - số đo hiệu năng (Manual, không assert thời gian): mở trang Display, thời gian mỗi khung khi cuộn
/// (CPU submit và "GPU xong"), bộ nhớ/handle, GPU thật và WARP, DPI 96/144, tự vẽ (c2) và lai (c1). Ghi ra
/// <c>PHOTOREVIEW_D2D_PERF_OUT</c> (nếu có) và ITestOutputHelper. Chạy khi máy yên (ghi chú nhiễu nếu CPU bận).
/// </summary>
[Collection("GlobalState")]
[Trait("Category", "Manual")]
public sealed class SettingsD2DPerfTests(ITestOutputHelper output)
{
    private const int OpenRounds = 15;
    private const int ScrollRepeats = 20;

    private void Line(string text)
    {
        output.WriteLine(text);
        if (Environment.GetEnvironmentVariable("PHOTOREVIEW_D2D_PERF_OUT") is { Length: > 0 } path)
        {
            File.AppendAllText(path, text + Environment.NewLine);
        }
    }

    private static string Stat(List<double> values)
    {
        if (values.Count == 0)
        {
            return "n/a";
        }

        var sorted = values.OrderBy(v => v).ToArray();
        double Pct(double p) => sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(p * sorted.Length) - 1)];
        return string.Create(CultureInfo.InvariantCulture, $"P50 {Pct(0.5):F2} | P95 {Pct(0.95):F2} | max {sorted[^1]:F2} | n={sorted.Length}");
    }

    public static TheoryData<uint, bool, bool> Matrix()
    {
        var data = new TheoryData<uint, bool, bool>();
        foreach (var dpi in new uint[] { 96, 144 })
        {
            foreach (var hosted in new[] { false, true })
            {
                foreach (var warp in new[] { false, true })
                {
                    data.Add(dpi, hosted, warp);
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public void Measure_OpenAndScroll(uint dpi, bool hosted, bool warp)
    {
        using var scope = TestLocalization.Use(TestLocalization.English);
        var label = $"{dpi}dpi {(hosted ? "hybrid-c1" : "selfdrawn-c2")} {(warp ? "WARP" : "GPU")}";
        var cpuLoad = Environment.GetEnvironmentVariable("PHOTOREVIEW_D2D_NOTE") ?? string.Empty;
        Line($"== {label} {cpuLoad}");
        var options = new D2DSettingsHostOptions(Dark: true, EditMode: hosted ? D2DEditMode.HostedWin32 : D2DEditMode.SelfDrawn, DpiOverride: dpi, PreferWarp: warp);

        // ---- mở trang Display: dựng cửa sổ + trang + Load + khung đầu (tới GPU xong). Vòng đầu = lạnh (JIT của testhost).
        var construct = new List<double>();
        var firstFrame = new List<double>();
        var total = new List<double>();
        var presenter = new PhotoReview.App.Presenters.SettingsPresenter(new PhotoReview.Core.Settings.AppSettings { ToolbarAutoHide = true, ShowExifInfo = true });
        var state = presenter.LoadFields();
        OnSta(() =>
        {
            for (var round = 0; round < OpenRounds; round++)
            {
                var t0 = Stopwatch.GetTimestamp();
                using var window = new D2DSettingsHost(options, () => PhotoReview.Core.Localization.Tr.SettingsNavDisplay, v => new D2DDisplayPage(v));
                var t1 = Stopwatch.GetTimestamp();
                ((D2DDisplayPage)window.Page).Load(state);
                window.View.Layout(window.View.ClientWidth, window.View.ClientHeight, window.Scale);
                window.PushValuesToHosted();
                window.SyncHosted();
                var t2 = Stopwatch.GetTimestamp();
                window.RenderNow();
                var t3 = Stopwatch.GetTimestamp();
                if (hosted)
                {
                    window.UpdateNow(); // control con vẽ lần đầu
                    Pump();
                }

                var t4 = Stopwatch.GetTimestamp();
                construct.Add(Stopwatch.GetElapsedTime(t0, t2).TotalMilliseconds);
                firstFrame.Add(Stopwatch.GetElapsedTime(t2, t4).TotalMilliseconds);
                total.Add(Stopwatch.GetElapsedTime(t0, t4).TotalMilliseconds);
                _ = t1;
                _ = t3;
                if (round == 0)
                {
                    Line($"adapter: {window.Surface.AdapterDescription}; IsWarp={window.Surface.IsWarp}; client {window.View.ClientWidth}x{window.View.ClientHeight}px; content {window.View.ContentHeightPx}px; widgets {window.View.AllWidgets().Count()}; hosted HWNDs {window.HostedCount}");
                }
            }

            return 0;
        });
        Line($"open total   (cold {total[0]:F1} ms) warm: {Stat(total.Skip(1).ToList())}");
        Line($"  construct  (cold {construct[0]:F1} ms) warm: {Stat(construct.Skip(1).ToList())}");
        Line($"  1st frame  (cold {firstFrame[0]:F1} ms) warm: {Stat(firstFrame.Skip(1).ToList())}");

        // ---- cuộn: 15 bước x 20 lần trên một cửa sổ; mỗi bước đo thời gian xử lý thông điệp + khung (CPU submit; GPU xong).
        var cpu = new List<double>();
        var gpu = new List<double>();
        var step = new List<double>();
        long gdiBefore = 0, gdiAfter = 0, userBefore = 0, userAfter = 0;
        long privateBefore = 0, privateAfter = 0;
        OnSta(() =>
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            privateBefore = Process.GetCurrentProcess().PrivateMemorySize64;
            gdiBefore = User32.GetGuiResources(Process.GetCurrentProcess().Handle, 0);
            userBefore = User32.GetGuiResources(Process.GetCurrentProcess().Handle, 1);
            using var window = new D2DSettingsHost(options, () => PhotoReview.Core.Localization.Tr.SettingsNavDisplay, v => new D2DDisplayPage(v));
            ((D2DDisplayPage)window.Page).Load(state);
            window.View.Layout(window.View.ClientWidth, window.View.ClientHeight, window.Scale);
            window.PushValuesToHosted();
            window.SyncHosted();
            window.RenderNow();
            Pump();
            var view = window.View;
            var hwnd = window.Hwnd;
            var max = view.MaxOffsetPx;
            // Kịch bản: lăn chuột, trang, kéo thumb qua nhiều vị trí, về đầu - mỗi lệnh = một khung.
            int[] positions = [0, max / 6, max / 3, max / 2, max * 2 / 3, max * 5 / 6, max, max * 3 / 4, max / 2, max / 4, 0];
            for (var repeat = 0; repeat < ScrollRepeats + 2; repeat++)
            {
                if (repeat == 2)
                {
                    window.RecordTimings = true; // bỏ 2 lượt làm nóng
                }

                foreach (var pos in positions)
                {
                    var t0 = Stopwatch.GetTimestamp();
                    User32.SendMessage(hwnd, 0x0115 /* WM_VSCROLL */, (nint)(5 | (pos << 16)), 0);
                    window.UpdateNow();
                    if (repeat >= 2)
                    {
                        step.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
                    }
                }

                // 20 nấc lăn chuột liên tục (khung mỗi nấc).
                for (var n = 0; n < 20; n++)
                {
                    var t0 = Stopwatch.GetTimestamp();
                    Wheel(hwnd, n < 10 ? -1 : 1);
                    window.UpdateNow();
                    if (repeat >= 2)
                    {
                        step.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
                    }
                }
            }

            cpu.AddRange(window.CpuSubmitMs);
            gpu.AddRange(window.GpuDoneMs);
            gdiAfter = User32.GetGuiResources(Process.GetCurrentProcess().Handle, 0);
            userAfter = User32.GetGuiResources(Process.GetCurrentProcess().Handle, 1);
            GC.Collect();
            privateAfter = Process.GetCurrentProcess().PrivateMemorySize64;
            Line($"GPU memory (typeperf, window alive): {GpuMemoryMb()}");
            return 0;
        });
        Line($"scroll frame, real window: CPU submit (BeginDraw..Present) ms: {Stat(cpu)}");
        // GPU xong: cùng mã vẽ trên bề mặt offscreen (WaitForGpu đọc lại 1 pixel; swap chain cửa sổ không hỗ trợ).
        var done = new List<double>();
        var submit = new List<double>();
        var scale = dpi / 96.0;
        using (var offscreen = new D2DOffscreenHost((int)(640 * scale), (int)(560 * scale), scale, true, hardware: !warp))
        {
            offscreen.Render(read: false);
            offscreen.Surface.WaitForGpu();
            var max = offscreen.View.MaxOffsetPx;
            for (var i = 0; i < 400; i++)
            {
                offscreen.View.ScrollTo((int)((long)max * (i % 40) / 39));
                var t0 = Stopwatch.GetTimestamp();
                offscreen.Render(read: false);
                var t1 = Stopwatch.GetTimestamp();
                offscreen.Surface.WaitForGpu();
                var t2 = Stopwatch.GetTimestamp();
                if (i >= 20)
                {
                    submit.Add(Stopwatch.GetElapsedTime(t0, t1).TotalMilliseconds);
                    done.Add(Stopwatch.GetElapsedTime(t0, t2).TotalMilliseconds);
                }
            }

            Line($"offscreen {(offscreen.IsWarp ? "WARP" : "GPU")}: frame CPU submit ms: {Stat(submit)}");
            Line($"offscreen {(offscreen.IsWarp ? "WARP" : "GPU")}: frame to GPU done ms: {Stat(done)}");
        }

        Line($"scroll step end-to-end (message + paint + GPU done + hosted sync) ms: {Stat(step)}");
        Line($"objects: GDI {gdiBefore}->{gdiAfter} (+{gdiAfter - gdiBefore}), USER {userBefore}->{userAfter} (+{userAfter - userBefore}); private bytes {privateBefore / 1048576.0:F1}->{privateAfter / 1048576.0:F1} MB (+{(privateAfter - privateBefore) / 1048576.0:F1})");
    }

    private static string GpuMemoryMb()
    {
        try
        {
            var pid = Environment.ProcessId;
            var script = $"$s=(Get-Counter '\\GPU Process Memory(pid_{pid}*)\\Dedicated Usage','\\GPU Process Memory(pid_{pid}*)\\Shared Usage' -ErrorAction Stop).CounterSamples; " +
                         "($s | Group-Object {$_.Path -replace '.*\\\\',''} | ForEach-Object { $_.Name + '=' + [math]::Round((($_.Group | Measure-Object CookedValue -Sum).Sum)/1MB,1) + ' MB' }) -join '; '";
            var info = new ProcessStartInfo("powershell.exe", $"-NoProfile -Command \"{script}\"")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(info)!;
            if (!process.WaitForExit(20000))
            {
                process.Kill();
                return "counter query timed out";
            }

            var text = process.StandardOutput.ReadToEnd().Trim();
            return text.Length == 0 ? "counter returned nothing" : text;
        }
        catch (Exception ex)
        {
            return "counter unavailable: " + ex.GetType().Name;
        }
    }
}

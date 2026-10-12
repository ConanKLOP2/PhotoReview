using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace PhotoReview.Benchmark.Cli.ShellHost;

/// <summary>
/// Chạy MỘT tiến trình thật của bản publish (shell <c>PhotoReview.exe</c> hoặc WPF <c>PhotoReview.App.exe</c>) trên thư mục ảnh riêng,
/// data root riêng và <c>PHOTOREVIEW_PERF_TRACE</c> riêng; chờ khung đầu, gửi <c>WM_KEYDOWN/WM_KEYUP</c> (PostMessage, KHÔNG SendInput)
/// cho các phím điều hướng khi ảnh lân cận đã preload, đọc working set đỉnh, rồi đóng bằng <c>WM_CLOSE</c> (<see cref="Process.CloseMainWindow"/>).
/// Chỉ dừng tiến trình do chính nó tạo (<see cref="Process.Kill()"/> trên handle của nó, sau thời hạn đóng).
/// </summary>
internal sealed class ProcessProbeSession(string photoFolder, string runsRoot, int keyCount = 12, int keyGapMs = 250,
    int settleMs = 2500, int firstFrameTimeoutMs = 30_000, int closeTimeoutMs = 15_000) : IProbeSession
{
    private const uint WmKeyDown = 0x0100;
    private const uint WmKeyUp = 0x0101;
    private const int VkLeft = 0x25;
    private const int VkRight = 0x27;

    public ProbeRunResult Run(ProbeApp app, ProbeSlot slot)
    {
        var runDir = Path.Combine(runsRoot, $"r{slot.Round:D2}-{app.Id}");
        var traceDir = Path.Combine(runDir, "trace");
        var dataRoot = Path.Combine(runDir, "data");
        Directory.CreateDirectory(traceDir);
        Directory.CreateDirectory(dataRoot);
        var image = Directory.GetFiles(photoFolder).Order(StringComparer.OrdinalIgnoreCase).First();

        var psi = new ProcessStartInfo(app.ExePath) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(app.ExePath)! };
        psi.ArgumentList.Add(image);
        psi.Environment["PHOTOREVIEW_DATA_ROOT"] = dataRoot;
        psi.Environment["PHOTOREVIEW_ISOLATE_CONFIG"] = "1";
        psi.Environment["PHOTOREVIEW_PERF_TRACE"] = traceDir;
        psi.Environment["PHOTOREVIEW_SHELL_INSTANCE_PREFIX"] = "PRProbe" + Guid.NewGuid().ToString("N")[..8];

        Process? process = null;
        var cpuBefore = SystemCpu();
        var note = "";
        var forcedKill = false;
        double peakMb = double.NaN;
        try
        {
            process = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null");

            if (!WaitForTraceEvent(traceDir, "RenderedFrame", firstFrameTimeoutMs, process))
            {
                note = process.HasExited ? "process exited before the first frame" : "timeout waiting for the first frame";
            }
            else
            {
                Thread.Sleep(settleMs); // preload của ảnh lân cận
                var hwnd = FindWindow(process);
                if (hwnd == 0)
                {
                    note = "no main window handle for keys";
                }
                else
                {
                    for (var i = 0; i < keyCount && !process.HasExited; i++)
                    {
                        var vk = i % 2 == 0 ? VkRight : VkLeft;
                        PostMessage(hwnd, WmKeyDown, vk, MakeLParam(down: true));
                        Thread.Sleep(20);
                        PostMessage(hwnd, WmKeyUp, vk, MakeLParam(down: false));
                        Thread.Sleep(keyGapMs);
                    }

                    Thread.Sleep(300);
                }
            }

            if (!process.HasExited)
            {
                process.Refresh();
                peakMb = process.PeakWorkingSet64 / (1024.0 * 1024.0);
                _ = process.CloseMainWindow();
                if (!process.WaitForExit(closeTimeoutMs))
                {
                    forcedKill = true;
                    process.Kill(); // tiến trình do chính probe tạo
                    process.WaitForExit(5000);
                }
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            note = "run failed: " + ex.Message;
            if (process is { HasExited: false })
            {
                forcedKill = true;
                try { process.Kill(); } catch (InvalidOperationException) { }
            }
        }
        finally
        {
            process?.Dispose();
        }

        var cpuAfter = SystemCpu();
        var cpuTotal = cpuAfter.Total - cpuBefore.Total;
        var cpuPercent = cpuTotal > 0 ? 100.0 * (cpuAfter.Busy - cpuBefore.Busy) / cpuTotal : double.NaN;

        // CSV được listener flush khi Dispose lúc thoát: đọc sau khi tiến trình đã thoát.
        TraceMetrics? metrics = null;
        var csv = Directory.GetFiles(traceDir, "perf-*.csv").FirstOrDefault();
        if (csv is not null)
        {
            metrics = ProbeTrace.Extract(ProbeTrace.Parse(ReadAllLinesShared(csv)));
        }

        if (metrics is null)
        {
            return new ProbeRunResult(slot.Round, app.Id, slot.IsWarmup, false, note.Length > 0 ? note : "no first frame in trace",
                double.NaN, double.NaN, double.NaN, [], [], 0, peakMb, forcedKill, cpuPercent);
        }

        var ok = note.Length == 0;
        return new ProbeRunResult(slot.Round, app.Id, slot.IsWarmup, ok, note,
            metrics.FirstFrameMs, metrics.WindowShownMs, metrics.WindowRevealedMs,
            metrics.KeyToFrameMs, metrics.KeyToFrameCachedMs, metrics.KeysWithoutFrame, peakMb, forcedKill, cpuPercent);
    }

    private static bool WaitForTraceEvent(string traceDir, string eventName, int timeoutMs, Process process)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.ElapsedMilliseconds < timeoutMs)
        {
            foreach (var file in Directory.GetFiles(traceDir, "perf-*.csv"))
            {
                try
                {
                    foreach (var line in ReadAllLinesShared(file))
                    {
                        if (line.Contains("," + eventName + ",", StringComparison.Ordinal))
                        {
                            return true;
                        }
                    }
                }
                catch (IOException)
                {
                    // đang ghi
                }
            }

            if (process.HasExited)
            {
                return false;
            }

            Thread.Sleep(15);
        }

        return false;
    }

    private static List<string> ReadAllLinesShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            lines.Add(line);
        }

        return lines;
    }

    private static nint FindWindow(Process process)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.ElapsedMilliseconds < 5000 && !process.HasExited)
        {
            process.Refresh();
            if (process.MainWindowHandle != 0)
            {
                return process.MainWindowHandle;
            }

            Thread.Sleep(50);
        }

        return 0;
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

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    private static nint MakeLParam(bool down) => down ? 0x00000001 : unchecked((nint)0xC0000001);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint hwnd, uint message, nint wParam, nint lParam);

    internal static string Describe(ProbeRunResult r) => string.Create(CultureInfo.InvariantCulture,
        $"{r.App} round={r.Round}{(r.IsWarmup ? " (warmup)" : "")} ok={r.Ok} firstFrame={r.FirstFrameMs:F0} shown={r.WindowShownMs:F0} revealed={r.WindowRevealedMs:F0} keys={r.KeyToFrameMs.Count} cached={r.KeyToFrameCachedMs.Count} peakWS={r.PeakWorkingSetMb:F0}MB {r.Note}");
}

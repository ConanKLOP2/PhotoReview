using System.Diagnostics;
using System.Runtime.InteropServices;
using PhotoReview.Shell.Tests.Hosting;
using PhotoReview.Shell.Win32.Hosting;
using Xunit.Abstractions;

namespace PhotoReview.Shell.Integration.Tests.Hosting;

/// <summary>
/// WP-14, tiêu chí "CPU nhàn rỗi của cửa sổ trống &lt; 0,5 %": đo thời gian CPU của UI thread trong 3 s nhàn rỗi với một cửa sổ
/// trống đang hiện (không kích hoạt). Category=Manual: báo số, không chạy trong CI (đo thời gian thật). Phần chặn được trong CI là
/// <c>MessageLoopFrameClockTests.IdleLoop_BlocksInMsgWaitInsteadOfSpinning</c>.
/// </summary>
[Trait("Category", "Manual")]
[Trait("Category", "UI")]
public sealed partial class ShellIdleCpuReportTests(ITestOutputHelper output)
{
    [Fact]
    public async Task EmptyWindow_Idle_UsesLessThanHalfAPercentOfOneCore()
    {
        await using var ui = await UiThread.StartAsync();
        var window = await ui.InvokeAsync(() =>
        {
            var clock = new TimerFrameClock();
            ui.Loop.AddFrameClock(clock);
            var w = new ShellWindow(new ShellWindowOptions(), clock);
            w.Show(activate: false);
            return w;
        });
        await ui.InvokeAsync(() => { }); // khung đầu + WM_PAINT đã xử lý

        var (cpuBefore, waitsBefore) = await ui.InvokeAsync(() => (ThreadCpuTicks(), ui.Loop.WaitCount));
        var wall = Stopwatch.StartNew();
        using (var neverSet = new ManualResetEventSlim())
        {
            // Cửa sổ đo cố định (test Manual báo số, không đồng bộ hoá gì): không ai đánh thức UI thread trong 3 s.
            await Task.Run(() => neverSet.Wait(TimeSpan.FromSeconds(3)));
        }
        var (cpuAfter, waitsAfter) = await ui.InvokeAsync(() => (ThreadCpuTicks(), ui.Loop.WaitCount));
        wall.Stop();

        var cpuPercent = (cpuAfter - cpuBefore) / 10_000.0 / wall.Elapsed.TotalMilliseconds * 100;
        output.WriteLine($"UI thread CPU {cpuPercent:0.000} % over {wall.Elapsed.TotalMilliseconds:0} ms, loop waits {waitsAfter - waitsBefore}");
        Assert.True(cpuPercent < 0.5, $"idle UI thread used {cpuPercent:0.000} % CPU");
        await ui.InvokeAsync(window.Dispose);
    }

    /// <summary>Thời gian CPU (kernel + user, đơn vị 100 ns) của thread đang gọi.</summary>
    private static long ThreadCpuTicks()
    {
        Assert.True(GetThreadTimes(GetCurrentThread(), out _, out _, out var kernel, out var user));
        return kernel + user;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetCurrentThread")]
    private static partial nint GetCurrentThread();

    [LibraryImport("kernel32.dll", EntryPoint = "GetThreadTimes", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetThreadTimes(nint thread, out long creation, out long exit, out long kernel, out long user);
}

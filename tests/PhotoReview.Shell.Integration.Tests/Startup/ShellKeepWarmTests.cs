using System.Diagnostics;
using PhotoReview.Shell.Rendering;
using PhotoReview.Shell.Win32.Hosting.PendingInterop;
using PhotoReview.Shell.Win32.Startup;
using PhotoReview.Shell.Win32.Viewing;

namespace PhotoReview.Shell.Integration.Tests.Startup;

/// <summary>
/// WP21-STALL: giữ GPU ấm nối vào shell THẬT (HWND, ShellStartup, WARP, desktop ẩn) với việc giả thay thiết bị GPU: tương tác của cửa sổ
/// ĐANG ACTIVE thì luồng giữ ấm chạy rồi tự dừng; cửa sổ không active/tắt công tắc/WARP thì không có gì; đóng cửa sổ thì trả thiết bị.
/// (Số đo thật nằm ở perf/2026-10-12-wp21-stall.md.)
/// </summary>
[Collection("ShellStartup")]
[Trait("Category", "UI")]
[Trait("Category", "Integration")]
public sealed class ShellKeepWarmTests
{
    private const uint WmActivate = 0x0006;
    private const uint WmKeyDown = 0x0100;
    private const uint WmMouseMove = 0x0200;
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Đồng hồ tiêm được của chính sách giữ ấm + luồng nền (ShellStartupOptions.KeepWarmTimestamp): throttle 100 ms và cửa sổ giữ ấm
    /// chỉ tiến khi test <see cref="AdvanceMs"/>, không phụ thuộc lịch luồng/tải của runner (trước đây: Pacing.PauseMs 900/250/300 ms
    /// và hai tin nhắn liên tiếp có thể cách nhau &gt; ThrottleMs trên CI chậm => kích hoạt lần 2 ngoài ý muốn).
    /// </summary>
    private sealed class FakeClock
    {
        private long _ticks = Stopwatch.Frequency; // khác 0 để không trùng giá trị mặc định

        public long Now() => Interlocked.Read(ref _ticks);

        public void AdvanceMs(double milliseconds) => Interlocked.Add(ref _ticks, (long)(milliseconds * Stopwatch.Frequency / 1000.0));
    }

    private sealed class FakeWork : IGpuKeepAliveWork
    {
        private int _touches;
        private int _disposed;

        public int Touches => Volatile.Read(ref _touches);

        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        public void Touch() => Interlocked.Increment(ref _touches);

        public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
    }

    private static ShellRun StartShell(Func<ShellStartupOptions, ShellStartupOptions> configure, out string[] files)
    {
        var root = ShellRun.NewRoot("keepwarm");
        files = [TestJpeg.WriteTo(Path.Combine(root, "photos", "p00.jpg")), TestJpeg.WriteTo(Path.Combine(root, "photos", "p01.jpg"))];
        var run = ShellRun.Start(root, [files[0]], configure: configure);
        Assert.True(run.WaitForMain(), "cửa sổ chính không dựng xong: " + run.Diagnostics);
        Assert.True(SpinWait.SpinUntil(() => run.Main!.FirstImagePresented, Bound), "ảnh đầu không được vẽ");
        return run;
    }

    private static void Stop(ShellRun run)
    {
        run.RequestClose();
        Assert.True(run.WaitForExit(Bound), "shell không thoát sau WM_CLOSE: " + run.Diagnostics);
        Assert.Null(run.Error);
    }

    private static void Send(ShellRun run, uint message, nint wParam = 0, nint lParam = 0) =>
        PendingUser32.SendMessage(run.Main!.Hwnd, message, wParam, lParam);

    [Fact]
    public void ActiveWindowInteraction_WarmsTheGpuThenStops_AndClosingReturnsTheDevice()
    {
        var clock = new FakeClock();
        var work = new FakeWork();
        using var run = StartShell(o => o with
        {
            KeepWarm = new KeepWarmOptions(Enabled: true, WindowMs: 400, IntervalMs: 5),
            KeepWarmWork = () => work,
            KeepWarmTimestamp = clock.Now,
        }, out _);
        var main = run.Main!;
        Assert.NotNull(main.KeepAlive);
        Assert.Equal(0, work.Touches); // chưa tương tác = chưa mở thiết bị, chưa chạy gì

        Send(run, WmActivate, 1);  // cửa sổ active (desktop ẩn không tự cấp focus)
        Send(run, WmKeyDown, 0x41); // 'A': phím không có lệnh

        Assert.True(SpinWait.SpinUntil(() => work.Touches >= 3, Bound), "tương tác của cửa sổ active không kích hoạt giữ ấm");
        Assert.True(SpinWait.SpinUntil(() => main.KeepWarm!.Activations >= 1, Bound));
        var keepAlive = main.KeepAlive!;
        clock.AdvanceMs(401); // > WindowMs (400): hạn chót đã qua theo đồng hồ tiêm
        // Luồng nền thoát vòng chạm và quay lại ngủ trên event (điều kiện, không phải thời gian cố định); từ đó không còn gì chạy.
        Assert.True(SpinWait.SpinUntil(() => keepAlive.IdleWaits >= 2 && keepAlive.IsBlocked, Bound), "hết khoảng giữ ấm mà luồng nền vẫn chạm");
        var settled = work.Touches;
        Assert.Equal(settled, work.Touches); // hết khoảng giữ ấm: im lặng

        Stop(run);

        Assert.True(work.IsDisposed, "đóng cửa sổ phải trả thiết bị giữ ấm");
    }

    [Fact]
    public void InteractionWhileTheWindowIsNotActive_DoesNotWarm()
    {
        var work = new FakeWork();
        using var run = StartShell(o => o with
        {
            KeepWarm = new KeepWarmOptions(true, 2000, 5),
            KeepWarmWork = () => work,
            Activate = false,
        }, out _);
        var main = run.Main!;

        Send(run, WmActivate, 0); // WA_INACTIVE
        Send(run, WmMouseMove, 0, 0);
        Send(run, WmKeyDown, 0x41); // SendMessage đồng bộ: chuỗi handler đã chạy xong, không cần chờ

        Assert.Equal(0, main.KeepWarm!.Activations);
        Assert.Equal(0, work.Touches);
        Assert.False(main.KeepAlive!.HasThread, "không tương tác hợp lệ thì không được tạo luồng");

        Stop(run);
    }

    [Fact]
    public void Deactivation_StopsFurtherExtensions()
    {
        var clock = new FakeClock();
        var work = new FakeWork();
        using var run = StartShell(o => o with
        {
            KeepWarm = new KeepWarmOptions(true, 1000, 5),
            KeepWarmWork = () => work,
            KeepWarmTimestamp = clock.Now,
        }, out _);
        var main = run.Main!;
        Send(run, WmActivate, 1);
        Send(run, WmKeyDown, 0x41); // cùng tick giả với WmActivate: bị throttle chặn, Activations không tăng
        Assert.Equal(1, main.KeepWarm!.Activations);

        Send(run, WmActivate, 0);
        clock.AdvanceMs(KeepWarmController.ThrottleMs + 50); // qua ngưỡng throttle: nếu không chặn theo active thì sẽ kích hoạt lần 2
        Send(run, WmKeyDown, 0x41);

        Assert.Equal(1, main.KeepWarm!.Activations);

        Stop(run);
    }

    [Fact]
    public void FrameLog_WritesFrames_AndClosingTheWindowReleasesTheFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "framelog-" + Guid.NewGuid().ToString("N") + ".log");
        try
        {
            using var run = StartShell(o => o with { KeepWarm = new KeepWarmOptions(Enabled: false), FrameLogPath = path }, out _);

            Stop(run);

            var lines = File.ReadAllLines(path); // ném IOException nếu cửa sổ đóng mà không trả tệp
            Assert.NotEmpty(lines);
            Assert.StartsWith("F gap=", lines[0]);
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public void SwitchOff_CreatesNoKeepAliveAtAll()
    {
        using var run = StartShell(o => o with
        {
            KeepWarm = new KeepWarmOptions(Enabled: false),
            KeepWarmWork = () => new FakeWork(),
        }, out _);

        Assert.Null(run.Main!.KeepAlive);
        Assert.Null(run.Main.KeepWarm);

        Stop(run);
    }

    [Fact]
    public void WarpSurface_WithTheDefaultWork_CreatesNoKeepAlive()
    {
        using var run = StartShell(o => o with { KeepWarm = new KeepWarmOptions(Enabled: true) }, out _); // ShellRun: PreferWarp = true

        Assert.Null(run.Main!.KeepAlive); // CPU raster: không có GPU để giữ ấm, không luồng/thiết bị

        Stop(run);
    }
}

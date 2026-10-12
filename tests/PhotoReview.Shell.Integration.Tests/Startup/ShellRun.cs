using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using PhotoReview.App;
using PhotoReview.Core;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Shell.Interop;
using PhotoReview.Shell.Win32;
using PhotoReview.Shell.Win32.Startup;

namespace PhotoReview.Shell.Integration.Tests.Startup;

/// <summary>
/// WP-20: chạy <see cref="ShellStartup"/> THẬT trong tiến trình test, trên một thread STA riêng (nó sở hữu dispatcher + vòng thông điệp
/// + cửa sổ), với dữ liệu trong thư mục tạm, log riêng (không đụng <c>FileLog.Default</c>), WARP, prefix instance duy nhất và mốc
/// perf thu về danh sách. Cửa sổ nằm trên desktop của tiến trình test (desktop ẩn khi chạy qua tools/run-tests-hidden.ps1);
/// không SetForegroundWindow/SendInput (N-8).
/// </summary>
internal sealed class ShellRun : IDisposable
{
    private static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(30);

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _mainReady = new();
    private readonly ManualResetEventSlim _exited = new();
    private readonly object _gate = new();
    private readonly List<(string Name, long Timestamp)> _marks = [];
    private readonly FileLog _log;
    private FileLog? _previousLog;

    private ShellRun(string root, string[] args, string prefix, Func<ShellStartupOptions, ShellStartupOptions>? configure)
    {
        Root = root;
        Prefix = prefix;
        DataRoot = Path.Combine(root, "data");
        Directory.CreateDirectory(DataRoot);
        Paths = new AppPaths(root, DataRoot, isolateConfig: true);
        _log = new FileLog(Path.Combine(root, "test-log", "app.log")); // nunca bật: INV-10 (không I/O khi tắt)
        _thread = new Thread(() => Body(args, configure)) { IsBackground = true, Name = "ShellRun" };
        _thread.SetApartmentState(ApartmentState.STA);
    }

    public string Root { get; }

    public string Prefix { get; }

    public string DataRoot { get; }

    public AppPaths Paths { get; }

    public ShellMainWindow? Main { get; private set; }

    /// <summary>Mã thoát của <see cref="ShellStartup.Run"/>; <see cref="int.MinValue"/> khi chưa xong.</summary>
    public int ExitCode { get; private set; } = int.MinValue;

    public Exception? Error { get; private set; }

    public bool HasExited => _exited.IsSet;

    public IReadOnlyList<string> MarkNames
    {
        get
        {
            lock (_gate)
            {
                return [.. _marks.Select(m => m.Name)];
            }
        }
    }

    public static string NewRoot(string label)
    {
        var root = Path.Combine(Path.GetTempPath(), "pr-wp20-" + label + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        return root;
    }

    /// <summary>
    /// Mô tả của lần chạy đã bị bỏ rơi (thread shell không thoát sau khi đóng); null = chưa có. Một thread shell kẹt giữ tài nguyên
    /// toàn tiến trình (D3D/WARP, OLE): mọi lần chạy sau đó chỉ treo thêm <see cref="DefaultWait"/> mỗi test rồi đỏ cùng một
    /// nguyên nhân. Thay vào đó mỗi test sau đỏ NGAY, kèm tên lần chạy kẹt đầu tiên, nên một lỗi không kéo cả dây chuyền chờ.
    /// </summary>
    private static volatile string? s_abandoned;

    public static ShellRun Start(string root, string[] args, string? prefix = null,
        Func<ShellStartupOptions, ShellStartupOptions>? configure = null)
    {
        if (s_abandoned is { } abandoned)
        {
            throw new InvalidOperationException(
                "Một lần chạy shell trước đó không thoát (thread còn sống, giữ tài nguyên toàn tiến trình) nên lần chạy này không được thử: "
                + abandoned);
        }

        var run = new ShellRun(root, args, prefix ?? "PRShellTest" + Guid.NewGuid().ToString("N")[..10], configure);
        run._thread.Start();
        return run;
    }

    public bool WaitForMark(string name, TimeSpan? timeout = null)
    {
        SpinWait.SpinUntil(() => MarkNames.Contains(name) || _exited.IsSet, timeout ?? DefaultWait);
        return MarkNames.Contains(name);
    }

    public bool WaitForMain(TimeSpan? timeout = null) => CaptureOnTimeout(_mainReady.Wait(timeout ?? DefaultWait));

    public bool WaitForExit(TimeSpan? timeout = null) => CaptureOnTimeout(_exited.Wait(timeout ?? DefaultWait));

    /// <summary>
    /// Bảng thread + stack managed của tiến trình test, chụp NGAY khi một lần chờ hết hạn (lúc shell còn treo, trước khi dọn);
    /// rỗng nếu chưa có lần chờ nào hết hạn. Thông báo lỗi của test nên kèm nó (<see cref="Diagnostics"/>).
    /// </summary>
    public string HangReport { get; private set; } = "";

    /// <summary>Trạng thái chẩn đoán dùng chung cho thông báo lỗi: mốc, lỗi, mã thoát, khung đầu và báo cáo treo (nếu có).</summary>
    public string Diagnostics =>
        $"marks=[{string.Join(", ", MarkNames)}] error={Error} exit={ExitCode} firstPresent={Main?.FirstPresentDone}"
        + (HangReport.Length == 0 ? "" : Environment.NewLine + HangReport);

    private bool CaptureOnTimeout(bool signaled)
    {
        if (!signaled && HangReport.Length == 0)
        {
            HangReport = HangDiagnostics.Capture();
        }

        return signaled;
    }

    /// <summary>Thời điểm (Stopwatch) của mốc đầu tiên tên <paramref name="name"/>, hoặc null.</summary>
    public long? TimestampOf(string name)
    {
        lock (_gate)
        {
            foreach (var (markName, timestamp) in _marks)
            {
                if (markName == name)
                {
                    return timestamp;
                }
            }
        }

        return null;
    }

    /// <summary>Đóng cửa sổ như nút X (WM_CLOSE từ thread khác, an toàn).</summary>
    public void RequestClose()
    {
        if (Main is { } main && main.Hwnd != 0)
        {
            User32.PostMessage(main.Hwnd, User32.WmClose, 0, 0);
        }
    }

    /// <summary>Chạy <paramref name="action"/> trên UI thread của shell và chờ xong.</summary>
    public void OnUi(Action action)
    {
        var main = Main ?? throw new InvalidOperationException("The main window is not ready.");
        main.Dispatcher.InvokeAsync(action).GetAwaiter().GetResult();
    }

    public T OnUi<T>(Func<T> func)
    {
        T result = default!;
        OnUi((Action)(() => result = func()));
        return result;
    }

    private void Body(string[] args, Func<ShellStartupOptions, ShellStartupOptions>? configure)
    {
        _previousLog = AppLog.Instance;
        AppLog.Instance = _log;
        try
        {
            var options = new ShellStartupOptions
            {
                InstanceNamePrefix = Prefix,
                ServiceOverrides = services =>
                {
                    services.AddSingleton<IAppPaths>(Paths);
                    services.AddSingleton<ILog>(_log);
                },
                PreferWarp = true,
                Activate = false,
                WarmGraphics = false,
                InstallProcessHooks = false,
                StartPerfListener = false,
            };
            if (configure is not null)
            {
                options = configure(options);
            }

            // Móc của harness bọc (không thay) móc do test cấu hình.
            var userMark = options.Mark;
            var userReady = options.OnMainWindowReady;
            options = options with
            {
                Mark = name =>
                {
                    RecordMark(name);
                    userMark?.Invoke(name);
                },
                OnMainWindowReady = main =>
                {
                    Main = main;
                    _mainReady.Set();
                    userReady?.Invoke(main);
                },
            };

            ExitCode = ShellStartup.Run(args, options);
        }
        catch (Exception ex)
        {
            Error = ex;
        }
        finally
        {
            AppLog.Instance = _previousLog ?? FileLog.Default;
            _exited.Set();
        }
    }

    private void RecordMark(string name)
    {
        var timestamp = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            _marks.Add((name, timestamp));
        }
    }

    public void Dispose()
    {
        if (!_exited.IsSet)
        {
            RequestClose();
            if (!_exited.Wait(TimeSpan.FromSeconds(15)))
            {
                // Không thoát được: không để thread (và cửa sổ) treo vào các test sau.
                if (Main is { } main && main.Hwnd != 0)
                {
                    User32.PostMessage(main.Hwnd, User32.WmDestroy, 0, 0);
                }
            }
        }

        if (!_thread.Join(TimeSpan.FromSeconds(5)) && !_exited.IsSet)
        {
            s_abandoned = $"root={Root} marks=[{string.Join(", ", MarkNames)}]";
        }

        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // Một handle còn mở (cache, log): thư mục tạm, hệ điều hành dọn sau.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

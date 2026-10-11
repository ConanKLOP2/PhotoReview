using System.Diagnostics;

namespace PhotoReview.Shell.Integration.Tests.Startup;

/// <summary>
/// WP-20: PhotoReview.exe THẬT (Program.Main, manifest, vòng thông điệp, thoát tiến trình) với một JPEG: ảnh đầu được vẽ (mốc perf
/// <c>RenderedFrame</c> trong CSV), đóng bằng WM_CLOSE thoát mã 0, bản thứ hai forward đường dẫn rồi thoát 0. Tiến trình con thừa
/// kế desktop của tiến trình test (desktop ẩn khi chạy qua tools/run-tests-hidden.ps1). Dữ liệu, prefix instance và WARP đều
/// riêng cho từng test: không đụng cửa sổ/dữ liệu đang dùng thật.
/// </summary>
[Collection("ShellStartup")]
[Trait("Category", "UI")]
[Trait("Category", "Integration")]
public sealed class ShellProcessTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(60);

    private sealed class ShellProcess : IDisposable
    {
        private readonly Process _process;

        public ShellProcess(string root, string prefix, string[] args)
        {
            Root = root;
            DataRoot = Path.Combine(root, "data");
            TraceDir = Path.Combine(root, "perf");
            var exe = Path.Combine(AppContext.BaseDirectory, "PhotoReview.exe");
            Assert.True(File.Exists(exe), "PhotoReview.exe not found next to the tests: " + exe);
            var info = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = AppContext.BaseDirectory,
            };
            foreach (var arg in args)
            {
                info.ArgumentList.Add(arg);
            }

            info.Environment["PHOTOREVIEW_DATA_ROOT"] = DataRoot;
            info.Environment["PHOTOREVIEW_ISOLATE_CONFIG"] = "1";
            info.Environment["PHOTOREVIEW_PERF_TRACE"] = TraceDir;
            info.Environment["PHOTOREVIEW_SHELL_INSTANCE_PREFIX"] = prefix;
            info.Environment["PHOTOREVIEW_SHELL_WARP"] = "1";
            info.Environment.Remove("PHOTOREVIEW_DIAG_FORCE_LOG");
            _process = Process.Start(info) ?? throw new InvalidOperationException("Process.Start returned null");
        }

        public string Root { get; }

        public string DataRoot { get; }

        public string TraceDir { get; }

        public int ExitCode => _process.ExitCode;

        public bool HasExited => _process.HasExited;

        /// <summary>Số sự kiện tên <paramref name="name"/> trong CSV perf (thư mục có thể chưa có file).</summary>
        public int CountEvents(string name)
        {
            if (!Directory.Exists(TraceDir))
            {
                return 0;
            }

            foreach (var file in Directory.EnumerateFiles(TraceDir))
            {
                try
                {
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(stream);
                    var text = reader.ReadToEnd();
                    var count = 0;
                    var needle = "," + name + ",";
                    for (var index = text.IndexOf(needle, StringComparison.Ordinal); index >= 0;
                        index = text.IndexOf(needle, index + needle.Length, StringComparison.Ordinal))
                    {
                        count++;
                    }

                    return count;
                }
                catch (IOException)
                {
                    return 0; // writer đang giữ: lần thăm dò sau
                }
            }

            return 0;
        }

        public bool WaitForEvents(string name, int atLeast, TimeSpan timeout) =>
            SpinWait.SpinUntil(() => CountEvents(name) >= atLeast || _process.HasExited, timeout) && CountEvents(name) >= atLeast;

        public bool WaitForExit(TimeSpan timeout) => _process.WaitForExit(timeout);

        public bool CloseWindow() => SpinWait.SpinUntil(() => _process.HasExited || _process.CloseMainWindow(), TimeSpan.FromSeconds(15));

        public void Dispose()
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(TimeSpan.FromSeconds(10));
            }

            _process.Dispose();
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    [Fact]
    public void Exe_WithJpeg_DrawsTheFirstImageAndExitsZeroOnClose()
    {
        var root = ShellRun.NewRoot("exe");
        var jpeg = TestJpeg.WriteTo(Path.Combine(root, "photos", "a.jpg"));
        using var shell = new ShellProcess(root, "PRProc" + Guid.NewGuid().ToString("N")[..10], [jpeg]);

        Assert.True(shell.WaitForEvents("RenderedFrame", 1, Bound), "ảnh đầu không được vẽ (không có RenderedFrame trong CSV)");
        Assert.Equal(1, shell.CountEvents("Assign"));
        Assert.True(shell.CloseWindow(), "không đóng được cửa sổ chính bằng WM_CLOSE");
        Assert.True(shell.WaitForExit(Bound), "tiến trình không thoát sau khi đóng cửa sổ");
        Assert.Equal(0, shell.ExitCode);
        Assert.False(File.Exists(Path.Combine(shell.DataRoot, "logs", "app.log")), "log tắt (INV-10): không được tạo app.log");
        Assert.True(File.Exists(Path.Combine(shell.DataRoot, "window-placement.json")), "đóng phải lưu placement");
    }

    [Fact]
    public void SecondExe_ForwardsItsFileToTheRunningOneAndExitsZero()
    {
        var root = ShellRun.NewRoot("exe-fwd");
        var prefix = "PRProc" + Guid.NewGuid().ToString("N")[..10];
        var first = TestJpeg.WriteTo(Path.Combine(root, "photos", "a.jpg"));
        var second = TestJpeg.WriteTo(Path.Combine(root, "photos", "b.jpg"));
        using var one = new ShellProcess(root, prefix, [first]);
        Assert.True(one.WaitForEvents("RenderedFrame", 1, Bound), "bản đầu không vẽ được ảnh đầu");

        var rootTwo = ShellRun.NewRoot("exe-fwd2");
        using var two = new ShellProcess(rootTwo, prefix, [second]);

        Assert.True(two.WaitForExit(Bound), "bản thứ hai không thoát");
        Assert.Equal(0, two.ExitCode);
        Assert.True(one.WaitForEvents("Assign", 2, Bound), "bản đầu không mở file được forward (không có Assign thứ hai)");
        Assert.False(one.HasExited);
        Assert.True(one.CloseWindow());
        Assert.True(one.WaitForExit(Bound));
        Assert.Equal(0, one.ExitCode);
    }
}

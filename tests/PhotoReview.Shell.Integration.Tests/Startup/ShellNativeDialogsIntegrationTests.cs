using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.FileActions;
using PhotoReview.Core.IO;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Model;
using PhotoReview.Shell.Interop;
using PhotoReview.Shell.Win32.Dialogs;

namespace PhotoReview.Shell.Integration.Tests.Startup;

/// <summary>
/// WP-35 GĐ1: shell THẬT (DI thật, desktop ẩn) mở Diagnostics, SkippedFiles, BatchReview và hộp zoom bằng hộp thoại Win32 GỐC
/// (lớp <c>#32770</c>, owner = HWND shell, modal) mà KHÔNG cần cầu WPF (cổng cầu mô phỏng vắng như bản AOT), và đóng shell khi một
/// hộp thoại còn mở: thoát mã 0, hộp modal trả về, không treo.
/// </summary>
[Collection("ShellStartup")]
[Trait("Category", "UI")]
[Trait("Category", "Integration")]
public sealed class ShellNativeDialogsIntegrationTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(60);

    private sealed class NoLauncher : IShellLauncher
    {
        public bool TryOpenFile(string path, string verb) => true;

        public bool TryRun(string fileName, string arguments) => false;
    }

    [Fact]
    public void NativeDialogs_OpenOwnedAndModal_WithoutTheBridge_AndTheShellClosesCleanlyWithOneOpen()
    {
        var root = ShellRun.NewRoot("nativedlg");
        var jpeg = TestJpeg.WriteTo(Path.Combine(root, "photos", "a.jpg"));
        using var run = ShellRun.Start(root, [jpeg], configure: options => options with
        {
            ServiceOverrides = services =>
            {
                options.ServiceOverrides?.Invoke(services);
                services.AddSingleton<IShellLauncher>(new NoLauncher());
            },
        });
        Assert.True(run.WaitForMain(), "cửa sổ chính không dựng xong: " + run.Diagnostics);
        Assert.True(SpinWait.SpinUntil(() => run.Main!.FirstImagePresented, Bound), "ảnh đầu không được vẽ: " + run.Diagnostics);
        var main = run.Main!;
        var uiThread = run.OnUi(() => GetCurrentThreadId());
        var viewModel = main.ViewModel;

        // 1. Diagnostics: hộp thoại Win32 gốc, modal; đóng bằng nút Đóng (IDCANCEL).
        var diagnostics = Open(run, () => viewModel.ShowDiagnostics());
        var dialog = WaitOwned(run, uiThread, "diagnostics");
        Assert.Equal("#32770", ClassName(dialog));
        Assert.False(IsWindowEnabled(main.Hwnd), "modal phải vô hiệu hoá cửa sổ chủ");
        Assert.Contains(Children(dialog), IsPushButton);
        run.OnUi(() => User32.SendMessage(dialog, WindowMessages.WmCommand, WindowMessages.IdCancel, 0));
        Finished(diagnostics, "Diagnostics không đóng");
        WaitNoOwned(run, uiThread, "diagnostics closed");
        Assert.True(IsWindowEnabled(main.Hwnd));

        // 2. Hộp zoom: Huỷ -> null, rồi OK 150.
        var zoomCancelled = Open(run, () => _ = main.OpenClickZoomCustomDialogAsync());
        PostClose(WaitOwned(run, uiThread, "zoom prompt"));
        Finished(zoomCancelled, "hộp zoom không đóng");
        WaitNoOwned(run, uiThread, "zoom prompt closed");

        var zoomOk = Open(run, () => _ = main.OpenClickZoomCustomDialogAsync());
        var zoom = WaitOwned(run, uiThread, "zoom prompt again");
        run.OnUi(() =>
        {
            Assert.True(User32.SetWindowText(User32.GetDlgItem(zoom, 101), "150"));
            Assert.True(User32.PostMessage(zoom, WindowMessages.WmCommand, WindowMessages.IdOk, 0));
        });
        Finished(zoomOk, "hộp zoom không đóng sau OK");
        WaitNoOwned(run, uiThread, "zoom prompt closed after OK");

        // 3. Đóng SHELL khi một hộp thoại Win32 còn mở: thoát mã 0, hộp modal trả về (không treo vòng lồng).
        var modal = Open(run, () => viewModel.ShowDiagnostics());
        WaitOwned(run, uiThread, "diagnostics before shutdown");

        run.RequestClose();

        Assert.True(run.WaitForExit(Bound), "shell không thoát khi hộp thoại Win32 còn mở: " + run.Diagnostics);
        Assert.Null(run.Error);
        Assert.Equal(0, run.ExitCode);
        Finished(modal, "hộp modal phải trả về khi shell thoát");
    }

    /// <summary>
    /// WP-35 GĐ2: Benchmark MODELESS trong shell thật (cầu WPF vắng như bản AOT): cửa sổ mở với owner = HWND shell, shell vẫn dùng
    /// được (không bị vô hiệu hoá), mở lần hai không tạo cửa sổ thứ hai, chạy THẬT nhiều profile trên ảnh JPEG, và đóng SHELL giữa lúc
    /// đang đo: thoát mã 0, không treo, cửa sổ biến mất (lượt đo bị huỷ qua presenter).
    /// </summary>
    [Fact]
    public void Benchmark_OpensModeless_RunsRealProfiles_AndTheShellClosesCleanlyWhileItRuns()
    {
        var root = ShellRun.NewRoot("nativebench");
        var photos = Path.Combine(root, "photos");
        var jpeg = TestJpeg.WriteTo(Path.Combine(photos, "a.jpg"));
        using var run = ShellRun.Start(root, [jpeg], configure: options => options with
        {
            ServiceOverrides = services =>
            {
                options.ServiceOverrides?.Invoke(services);
                services.AddSingleton<IShellLauncher>(new NoLauncher());
            },
        });
        Assert.True(run.WaitForMain(), "cửa sổ chính không dựng xong: " + run.Diagnostics);
        Assert.True(SpinWait.SpinUntil(() => run.Main!.FirstImagePresented, Bound), "ảnh đầu không được vẽ: " + run.Diagnostics);
        var main = run.Main!;
        var uiThread = run.OnUi(() => GetCurrentThreadId());

        run.OnUi(() => main.ViewModel.ShowBenchmark()); // modeless: trả về ngay
        var benchmark = WaitOwned(run, uiThread, "benchmark");
        Assert.True(IsWindowEnabled(main.Hwnd), "modeless: cửa sổ chính không bị vô hiệu hoá");
        run.OnUi(() => main.ViewModel.ShowBenchmark()); // lần hai: đưa lên trước, không tạo cửa sổ mới
        Assert.Single(OwnedWindows(uiThread, main.Hwnd));

        run.OnUi(() =>
        {
            Assert.True(User32.SetWindowText(User32.GetDlgItem(benchmark, 102), photos));
            Assert.True(User32.SetWindowText(User32.GetDlgItem(benchmark, 105), "1"));
            User32.SendMessage(benchmark, WindowMessages.WmCommand, 107 /* Select all */, 0);
            User32.SendMessage(benchmark, WindowMessages.WmCommand, 110 /* Run */, 0);
        });
        var cancel = run.OnUi(() => User32.GetDlgItem(benchmark, 112));
        Assert.True(SpinWait.SpinUntil(() => run.OnUi(() => IsWindowEnabled(cancel)), Bound), "lượt đo không bắt đầu: " + run.Diagnostics);

        run.RequestClose(); // đóng shell khi đang đo

        Assert.True(run.WaitForExit(Bound), "shell không thoát khi Benchmark đang chạy: " + run.Diagnostics);
        Assert.Null(run.Error);
        Assert.Equal(0, run.ExitCode);
    }

    /// <summary>
    /// WP-35 GĐ4: Recovery MODAL trong shell thật (cầu WPF vắng như bản AOT): hộp thoại Win32 gốc owner = HWND shell, đọc journal
    /// của thư mục dữ liệu cô lập, kiểm tra nền chạy qua bối cảnh đồng bộ thật (nút Retry bật khi phán quyết CanRetry về), KHÔNG đổi
    /// tệp nào / dòng journal nào, và đóng SHELL khi Recovery còn mở: thoát mã 0, hộp modal trả về, không treo.
    /// </summary>
    [Fact]
    public void Recovery_OpensOwnedAndModal_ChecksInTheBackground_ChangesNoData_AndTheShellClosesCleanlyWithItOpen()
    {
        var root = ShellRun.NewRoot("nativerecovery");
        var jpeg = TestJpeg.WriteTo(Path.Combine(root, "photos", "a.jpg"));
        using var run = ShellRun.Start(root, [jpeg], configure: options => options with
        {
            ServiceOverrides = services =>
            {
                options.ServiceOverrides?.Invoke(services);
                services.AddSingleton<IShellLauncher>(new NoLauncher());
            },
        });
        Assert.True(run.WaitForMain(), "cửa sổ chính không dựng xong: " + run.Diagnostics);
        Assert.True(SpinWait.SpinUntil(() => run.Main!.FirstImagePresented, Bound), "ảnh đầu không được vẽ: " + run.Diagnostics);
        var main = run.Main!;
        var uiThread = run.OnUi(() => GetCurrentThreadId());

        // Một "bản sao journal": mục Failed Move nằm trong thư mục tạm (nguồn còn, đích chưa có -> CanRetry).
        var work = Path.Combine(root, "work");
        Directory.CreateDirectory(work);
        var source = Path.Combine(work, "m.jpg");
        File.WriteAllText(source, "photo-bytes");
        var info = new FileInfo(source);
        var entry = new JournalEntry("it-1", FileOperationType.Move, JournalState.Failed, source, Path.Combine(work, "out", "m.jpg"),
            info.Length, info.LastWriteTimeUtc, DateTime.UtcNow, "disk error");
        new OperationJournal(run.Paths, new PhysicalFileSystem(), new SystemClock()).Append(entry);
        var journalBefore = File.ReadAllText(run.Paths.JournalFile);
        var filesBefore = string.Join("|", Directory.EnumerateFiles(work, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal));

        var recovery = Open(run, () => main.ViewModel.ShowRecovery());
        var dialog = WaitOwned(run, uiThread, "recovery");
        Assert.Equal("#32770", ClassName(dialog));
        Assert.False(IsWindowEnabled(main.Hwnd), "modal phải vô hiệu hoá cửa sổ chủ");
        Assert.Equal(Tr.RecoveryTitle, run.OnUi(() => WindowText(dialog)));
        var list = run.OnUi(() => User32.GetDlgItem(dialog, 106));
        Assert.Equal(1, run.OnUi(() => (int)User32.SendMessage(list, 0x018B /* LB_GETCOUNT */, 0, 0)));

        // Chọn hàng đó như người dùng; kết quả kiểm tra nền (CanRetry) về qua bối cảnh đồng bộ nên Retry bật.
        run.OnUi(() =>
        {
            User32.SendMessage(list, 0x0185 /* LB_SETSEL */, 1, 0);
            User32.SendMessage(dialog, WindowMessages.WmCommand, (1 << 16) | 106, 0); // LBN_SELCHANGE
        });
        var retry = run.OnUi(() => User32.GetDlgItem(dialog, 132));
        Assert.True(SpinWait.SpinUntil(() => run.OnUi(() => IsWindowEnabled(retry)), Bound),
            "kiểm tra nền không trả CanRetry (Retry không bật): " + run.Diagnostics);

        run.RequestClose(); // đóng SHELL khi Recovery còn mở

        Assert.True(run.WaitForExit(Bound), "shell không thoát khi Recovery còn mở: " + run.Diagnostics);
        Assert.Null(run.Error);
        Assert.Equal(0, run.ExitCode);
        Finished(recovery, "hộp Recovery phải trả về khi shell thoát");
        Assert.Equal(journalBefore, File.ReadAllText(run.Paths.JournalFile));
        Assert.Equal(filesBefore, string.Join("|", Directory.EnumerateFiles(work, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)));
        Assert.Equal("photo-bytes", File.ReadAllText(source));
    }

    private static void Finished(Task task, string what) => Assert.True(task.Wait(Bound), what);

    private static Task Open(ShellRun run, Action action) => run.Main!.Dispatcher.InvokeAsync(action);

    private static nint WaitOwned(ShellRun run, uint uiThread, string what)
    {
        nint found = 0;
        Assert.True(SpinWait.SpinUntil(() => (found = OwnedWindows(uiThread, run.Main!.Hwnd).FirstOrDefault()) != 0, Bound),
            $"hộp thoại '{what}' không hiện: " + run.Diagnostics);
        return found;
    }

    private static void WaitNoOwned(ShellRun run, uint uiThread, string what)
        => Assert.True(SpinWait.SpinUntil(() => OwnedWindows(uiThread, run.Main!.Hwnd).Count == 0, Bound), $"hộp thoại còn mở: {what}");

    private static void PostClose(nint hwnd) => Assert.True(User32.PostMessage(hwnd, WindowMessages.WmClose, 0, 0));

    private static string WindowText(nint hwnd)
    {
        var buffer = new char[256];
        var length = GetWindowText(hwnd, buffer, buffer.Length);
        return new string(buffer, 0, Math.Max(0, length));
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint hwnd, char[] text, int maxCount);

    private static string ClassName(nint hwnd)
    {
        var buffer = new char[64];
        var length = GetClassName(hwnd, buffer, buffer.Length);
        return new string(buffer, 0, length);
    }

    private static List<nint> Children(nint parent)
    {
        var found = new List<nint>();
        EnumChildWindows(parent, (hwnd, _) =>
        {
            found.Add(hwnd);
            return true;
        }, 0);
        return found;
    }

    private static bool IsPushButton(nint hwnd) => ClassName(hwnd) == "Button";

    private static List<nint> OwnedWindows(uint uiThread, nint owner)
    {
        var found = new List<nint>();
        EnumThreadWindows(uiThread, (hwnd, _) =>
        {
            if (hwnd != owner && IsWindowVisible(hwnd) && GetWindow(hwnd, 4 /* GW_OWNER */) == owner)
            {
                found.Add(hwnd);
            }

            return true;
        }, 0);
        return found;
    }

    private delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool EnumThreadWindows(uint threadId, EnumWindowsProc callback, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(nint parent, EnumWindowsProc callback, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowEnabled(nint hwnd);

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint hwnd, uint command);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint hwnd, char[] name, int maxCount);
}

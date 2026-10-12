using System.ComponentModel;
using System.Diagnostics;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Localization;

namespace PhotoReview.Shell.Win32.Dialogs;

/// <summary>Seam cho việc mở tệp/chương trình qua shell (test thay bằng fake, không mở tiến trình thật).</summary>
internal interface IShellLauncher
{
    /// <summary><c>ShellExecute</c> tệp <paramref name="path"/> với động từ <paramref name="verb"/>; false nếu không có chương trình gắn với nó hoặc lỗi.</summary>
    bool TryOpenFile(string path, string verb);

    /// <summary>Chạy <paramref name="fileName"/> với <paramref name="arguments"/> (dự phòng notepad); false nếu lỗi.</summary>
    bool TryRun(string fileName, string arguments);
}

/// <summary><see cref="IShellLauncher"/> thật: <see cref="Process.Start(ProcessStartInfo)"/> với <c>UseShellExecute</c>.</summary>
internal sealed class ProcessShellLauncher : IShellLauncher
{
    public static ProcessShellLauncher Instance { get; } = new();

    public bool TryOpenFile(string path, string verb) => Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = verb });

    public bool TryRun(string fileName, string arguments)
        => Start(new ProcessStartInfo(fileName) { Arguments = arguments, UseShellExecute = true });

    private static bool Start(ProcessStartInfo info)
    {
        try
        {
            using var process = Process.Start(info);
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException or PlatformNotSupportedException)
        {
            return false;
        }
    }
}

/// <summary>
/// "Cổng chưa có" tối thiểu (NOWPF-AOT-SETTINGS-FALLBACK): khi cầu WPF không khả dụng (bản Native AOT, hoặc nạp cầu thất
/// bại) lệnh Settings mở <c>config.json</c> bằng trình soạn thảo mặc định, các cửa sổ phụ khác chỉ báo "không có trong bản
/// này" (không chặn, không ném, không đổi dữ liệu). KHÔNG bao giờ ghi/sửa config.json: chỉ mở tệp.
/// </summary>
internal sealed class SecondaryWindowFallback
{
    /// <summary>Động từ ShellExecute dùng để mở config.json (an toàn: "open", không "runas").</summary>
    internal const string OpenVerb = "open";

    /// <summary>Chương trình dự phòng khi .json không có chương trình gắn kèm.</summary>
    internal const string FallbackEditor = "notepad.exe";

    private readonly IAppPaths _paths;
    private readonly IShellLauncher _launcher;
    private readonly Action<string> _notify;

    /// <param name="notify">Thông báo không chặn (dòng trạng thái); gọi trên UI thread.</param>
    public SecondaryWindowFallback(IAppPaths paths, IShellLauncher launcher, Action<string> notify)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _notify = notify ?? throw new ArgumentNullException(nameof(notify));
    }

    /// <summary>Settings: mở config.json; luôn false (không có thay đổi nào được Lưu từ cửa sổ).</summary>
    public bool OpenSettings()
    {
        var path = _paths.ConfigFile;
        var opened = Safe(() => _launcher.TryOpenFile(path, OpenVerb))
            || Safe(() => _launcher.TryRun(FallbackEditor, Quote(path)));
        Notify(opened ? Tr.StatusSecondaryUnavailableSettings : Tr.StatusSecondaryUnavailableSettingsFailed(path));
        return false;
    }

    public void Recovery() => Notify(Tr.StatusSecondaryUnavailableRecovery);

    public void Diagnostics() => Notify(Tr.StatusSecondaryUnavailableDiagnostics);

    public void SkippedFiles() => Notify(Tr.StatusSecondaryUnavailableSkippedFiles);

    public void Benchmark() => Notify(Tr.StatusSecondaryUnavailableBenchmark);

    /// <summary>Duyệt hàng loạt: false (lô không chạy, không thay đổi gì).</summary>
    public bool BatchReview()
    {
        Notify(Tr.StatusSecondaryUnavailableBatchReview);
        return false;
    }

    private static string Quote(string path) => "\"" + path + "\"";

    private static bool Safe(Func<bool> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
    }

    private void Notify(string message)
    {
        try
        {
            _notify(message);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Thông báo chỉ là phần thưởng: lỗi hiển thị không được làm hỏng lệnh.
        }
    }
}

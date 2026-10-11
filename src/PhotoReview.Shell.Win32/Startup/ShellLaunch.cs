using PhotoReview.App;

namespace PhotoReview.Shell.Win32.Startup;

/// <summary>
/// NE-6 (NO-WPF-EXEC-PLAN mục 7.1, beta): bản Win32 dùng prefix mutex/pipe RIÊNG, nên bản WPF (prefix <c>PhotoReview</c>) và bản
/// Win32 không forward nhầm cho nhau trong lúc chạy song song. Hợp nhất ở P6 = đổi một hằng này (CHƯA XÁC NHẬN, xem
/// NOWPF-UNCONFIRMED-DECISIONS-20261010). KHÔNG đổi prefix của <c>WindowsLiveOperationRegistry</c>: marker thao tác đang chạy
/// phải chung để reconcile journal của bên này bỏ qua thao tác còn chạy ở bên kia (Q-R27).
/// </summary>
internal static class ShellInstanceNames
{
    public const string Prefix = "PhotoReviewShell";
}

/// <summary>
/// Đối số dòng lệnh đã chuẩn hoá của shell (cùng luật với <c>App.StartupCoreAsync</c>): thư mục được đưa về dạng chuẩn
/// (<see cref="LaunchArguments"/>), file đầu tiên tồn tại là ảnh mở đầu, không có file thì thư mục đầu tiên; thư mục chứa ảnh
/// là thư mục khoá single-instance (R2-F-08: đối số file tương đối cho tên thư mục rỗng nên phải qua <see cref="Path.GetFullPath(string)"/>).
/// </summary>
/// <param name="Arguments">Đối số sau khi bỏ cờ shell (<c>--smoke</c>) và chuẩn hoá thư mục.</param>
/// <param name="InitialFile">File đầu tiên tồn tại, hoặc null.</param>
/// <param name="InitialFolder">Thư mục đầu tiên tồn tại, hoặc null.</param>
/// <param name="LaunchFolder">Thư mục của <see cref="InitialFile"/>, nếu không có thì <see cref="InitialFolder"/>.</param>
/// <param name="Smoke">Chạy thử: không khoá instance, không đọc/ghi placement, không reconcile journal, không kích hoạt; thoát sau khung đầu.</param>
internal sealed record ShellLaunch(IReadOnlyList<string> Arguments, string? InitialFile, string? InitialFolder, string? LaunchFolder, bool Smoke)
{
    internal const string SmokeArgument = "--smoke";

    /// <summary>Đường dẫn mở lúc khởi động: file ưu tiên hơn thư mục.</summary>
    public string? InitialPath => InitialFile ?? InitialFolder;

    /// <summary>Đường dẫn (đầy đủ) mà bản thứ hai chuyển cho bản đang chạy: chỉ file/thư mục tồn tại.</summary>
    public IReadOnlyList<string> ExistingPaths { get; } = [.. Arguments
        .Where(a => File.Exists(a) || Directory.Exists(a))
        .Select(Path.GetFullPath)];

    public static ShellLaunch Parse(IEnumerable<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var all = args.ToArray();
        var smoke = all.Any(a => string.Equals(a, SmokeArgument, StringComparison.Ordinal));
        var normalized = LaunchArguments.Normalize(all.Where(a => !string.Equals(a, SmokeArgument, StringComparison.Ordinal)));
        var initial = normalized.FirstOrDefault(File.Exists);
        var initialFolder = normalized.FirstOrDefault(Directory.Exists);
        var launchFolder = initial is not null ? Path.GetDirectoryName(Path.GetFullPath(initial)) : initialFolder;
        return new ShellLaunch(normalized, initial, initialFolder, launchFolder, smoke);
    }
}

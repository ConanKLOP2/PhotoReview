using PhotoReview.Core.Abstractions;

namespace PhotoReview.Shell.WpfBridge;

/// <summary>
/// C-13 (NO-WPF-EXEC-PLAN mục 5): cửa sổ phụ WPF mở từ shell Win32 (NE-5 = a: cùng tiến trình, nạp WPF muộn qua
/// PhotoReview.App.WpfWindows). Thực thi ở WP-19b. Mọi hàm gọi trên UI thread của shell.
/// </summary>
public interface ISecondaryWindowHost
{
    bool ShowSettings(nint ownerHwnd, SettingsTarget target);

    void ShowRecovery(nint ownerHwnd);

    void ShowDiagnostics(nint ownerHwnd);

    /// <summary>Modeless như bản WPF.</summary>
    void ShowBenchmark(nint ownerHwnd, string? folder);

    void ShowSkippedFiles(nint ownerHwnd, IReadOnlyList<SkippedEntry> entries);

    bool ShowBatchReview(nint ownerHwnd, IReadOnlyList<BatchReviewItem> items);

    int? PromptCustomZoom(nint ownerHwnd, int currentPercent);
}

public static class SecondaryWindowHostFactory
{
    /// <summary>WP-19b thực thi.</summary>
    public static ISecondaryWindowHost Create(IServiceProvider services) => throw new NotImplementedException();
}

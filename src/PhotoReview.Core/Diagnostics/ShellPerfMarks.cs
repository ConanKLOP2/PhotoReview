namespace PhotoReview.Core.Diagnostics;

/// <summary>
/// C-18 (NO-WPF-EXEC-PLAN mục 5): mốc perf chỉ có ở shell Win32. Shell phát thêm cùng tên mốc với bản WPF
/// (appStartup, servicesBuilt, ..., RenderedFrame) để dùng chung probe. Dùng từ WP-20, đo ở WP-29.
/// </summary>
public static class ShellPerfMarks
{
    public const string D3dDeviceReady = "d3dDeviceReady";
    public const string SwapChainReady = "swapChainReady";
    public const string FirstPresent = "firstPresent";
}

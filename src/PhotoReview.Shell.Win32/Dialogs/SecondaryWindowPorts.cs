using PhotoReview.Shell.Win32.Hosting;

namespace PhotoReview.Shell.Win32.Dialogs;

/// <summary>
/// WP-19b: cổng nạp lười tới cửa sổ phụ WPF cho <see cref="Win32DialogService"/> / <see cref="Win32ZoomPromptService"/>. Không có kiểu của
/// WpfBridge ở đây (L-SHELL): chỉ <see cref="WpfBridgeLoader"/> chạm tới nó, và chỉ khi <c>.Value</c> được đọc lần đầu.
/// </summary>
internal static class SecondaryWindowPorts
{
    /// <param name="services">DI của shell (SettingsStore, journal, ...).</param>
    /// <param name="loop">Vòng lặp của UI thread (đăng ký bộ lọc phím cho cửa sổ modeless); null = không (test).</param>
    public static Lazy<ISecondaryWindowPort?> CreateLazy(IServiceProvider services, MessageLoop? loop)
    {
        ArgumentNullException.ThrowIfNull(services);
        return new Lazy<ISecondaryWindowPort?>(() => TryLoad(services, loop), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>
    /// Host WPF thật, hoặc null khi assembly cầu / App.WpfWindows vắng (phát hành thiếu file): cửa sổ phụ khi đó không mở
    /// (false/no-op). Lỗi nạp assembly xảy ra lúc JIT <c>WpfBridgeLoader.Create</c> nên bắt ở đây.
    /// </summary>
    internal static ISecondaryWindowPort? TryLoad(IServiceProvider services, MessageLoop? loop)
    {
        try
        {
            return WpfBridgeLoader.Create(services, loop);
        }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
        {
            return null;
        }
    }
}

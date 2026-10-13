using Microsoft.Extensions.DependencyInjection;
using PhotoReview.App.Composition;
using PhotoReview.App.Coordinators;
using PhotoReview.App.Services;
using PhotoReview.Core.Abstractions;
using PhotoReview.Imaging.Decoding;
using PhotoReview.Shell.Win32.Dialogs;
using PhotoReview.Shell.Win32.Dialogs.Native;
using PhotoReview.Shell.Win32.Hosting;
using PhotoReview.Shell.Win32.Viewing;

namespace PhotoReview.Shell.Win32.Startup;

/// <summary>Những gì host Win32 cấp cho đồ thị dịch vụ dùng chung (C-16): không có mặc định ẩn, thiếu thì ném to.</summary>
/// <param name="Dispatcher">Dispatcher của UI thread (đăng ký làm <see cref="IUiScheduler"/>).</param>
/// <param name="OwnerWindow">HWND chủ của hộp thoại; 0 khi chưa có cửa sổ chính.</param>
/// <param name="Loop">Vòng thông điệp của UI thread (cầu cửa sổ phụ gắn bộ lọc phím cho cửa sổ modeless); null = không (test).</param>
internal sealed record ShellHostServices(Win32UiDispatcher Dispatcher, Func<nint> OwnerWindow, MessageLoop? Loop = null);

/// <summary>
/// C-16 phần shell (NO-WPF-EXEC-PLAN mục 5, WP-20): đồ thị DI của bản Win32 = <c>AddPhotoReviewShared</c> (WP-09, codec =
/// <see cref="PixelBufferImageCodec"/>, không decoder WPF: fallback theo NE-8) + dịch vụ riêng của shell. Gói tính năng sau
/// (WP-21..26) thêm đăng ký bằng MỘT dòng cuối <see cref="RegisterFeatures"/>; đăng ký sau thắng đăng ký trước.
/// </summary>
internal static class ShellCompositionRoot
{
    public static ServiceProvider Build(ShellHostServices host, ShellPresentationHub hub, Action<IServiceCollection>? overrides = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(hub);
        var services = new ServiceCollection();
        ConfigureServices(services, host, hub);
        overrides?.Invoke(services);
        return services.BuildServiceProvider();
    }

    internal static void ConfigureServices(IServiceCollection services, ShellHostServices host, ShellPresentationHub hub)
    {
        // 1. Dịch vụ do host Win32 cấp (đồ thị dùng chung resolve lười, thiếu thì ném).
        services.AddSingleton(hub);
        services.AddSingleton<ShellNoticeSink>();
        services.AddSingleton<IUiScheduler>(host.Dispatcher);
        services.AddSingleton<IUiDispatcher>(host.Dispatcher);
        services.AddSingleton<IClipboardService>(_ => new Win32ClipboardService(host.OwnerWindow));
        services.AddSingleton(sp => new FolderPicker(sp.GetService<IShellFolderDialog>() ?? UnwiredShellFolderDialog.Instance, host.OwnerWindow));
        services.AddSingleton<IFolderPicker, ShellFolderPickerAdapter>();
        // WP-35 GĐ1: cổng cửa sổ phụ = SecondaryWindowRouter. Cửa sổ đã có hộp thoại Win32 gốc (Diagnostics, SkippedFiles, BatchReview,
        // ZoomPrompt) mở bằng Win32SecondaryWindowPort (không WPF, chạy cả ở bản Native AOT); cửa sổ chưa có (Settings, Recovery,
        // Benchmark) rơi về cầu WPF nạp lười (WpfBridgeLoader qua SecondaryWindowPorts) và, khi cầu vắng (bản AOT / nạp lỗi), về
        // cổng tạm SecondaryWindowFallback. ISecondaryWindowPort đăng ký sẵn (test) thắng tất cả. Chủ sở hữu duy nhất của cổng =
        // SecondaryWindowPortHost (Dispose đóng hộp thoại Win32 + cửa sổ WPF còn mở).
        services.AddSingleton<SecondaryWindowPortLoader>(SecondaryWindowPorts.TryLoad);
        // Cổng vắng (bản Native AOT / nạp cầu lỗi): Settings mở config.json, cửa sổ khác báo "không có trong bản này"
        // qua dòng trạng thái của view model thật (ShellNoticeSink: MainViewModel là transient, không resolve lại được).
        services.AddSingleton(sp => new SecondaryWindowFallback(sp.GetRequiredService<IAppPaths>(),
            sp.GetService<IShellLauncher>() ?? ProcessShellLauncher.Instance, sp.GetRequiredService<ShellNoticeSink>().Show));
        services.AddSingleton<IShellFileDialog, NativeShellFileDialog>();
        services.AddSingleton(sp => new SecondaryWindowPortHost(new Lazy<ISecondaryWindowPort?>(
            () => sp.GetService<ISecondaryWindowPort>() ?? new SecondaryWindowRouter(
                new Win32SecondaryWindowPort(sp),
                new Lazy<ISecondaryWindowPort?>(
                    () => sp.GetRequiredService<SecondaryWindowPortLoader>()(sp, host.Loop),
                    LazyThreadSafetyMode.ExecutionAndPublication),
                sp.GetRequiredService<SecondaryWindowFallback>()),
            LazyThreadSafetyMode.ExecutionAndPublication)));
        services.AddSingleton<IZoomPromptService>(sp =>
            new Win32ZoomPromptService(host.OwnerWindow, sp.GetRequiredService<SecondaryWindowPortHost>().Port));
        services.AddSingleton<IDialogService>(sp => new Win32DialogService(
            host.OwnerWindow,
            sp.GetRequiredService<FolderPicker>(),
            sp.GetRequiredService<SecondaryWindowPortHost>().Port,
            sp.GetRequiredService<SecondaryWindowFallback>()));

        // 2. Đồ thị dùng chung: codec nền tảng = PixelBuffer, không decoder WPF (INV-12 theo NE-8: WicDirect). ThumbnailDecoder
        // là trường dự trữ (v1.3 bỏ): từ WP-06 ThumbnailCache nhận codec, nên chỉ cần một decoder hợp lệ để qua kiểm null.
        services.AddPhotoReviewShared(new SharedServiceOptions(
            PixelBufferImageCodec.Instance,
            WpfFallbackDecoder: null,
            ThumbnailDecoder: _ => new PhotoReview.Imaging.Decoding.Wic.WicDirectDecoder(PixelBufferImageCodec.Instance)));

        // 3. Tính năng của các gói sau.
        RegisterFeatures(services);
    }

    /// <summary>
    /// Điểm cắm của WP-21..26: mỗi gói thêm đúng MỘT dòng ở CUỐI hàm này (<c>services.AddShellXxx();</c>) để không xung đột
    /// khi merge.
    /// </summary>
    private static void RegisterFeatures(IServiceCollection services)
    {
        services.AddShellViewing(); // WP-21: trình bày ảnh, compare, crossfade
        services.AddShellOverlay(); // WP-23
    }
}

/// <summary><see cref="IFolderPicker"/> (C-13) trên <see cref="FolderPicker"/> của shell.</summary>
internal sealed class ShellFolderPickerAdapter(FolderPicker picker) : IFolderPicker
{
    public string? PickFolder(string title, string? initialFolder) => picker.Pick(title, initialFolder);
}

/// <summary>
/// Thông báo không chặn của shell cho các lệnh không có cửa sổ (<c>SecondaryWindowFallback</c>): ghi vào dòng trạng thái của
/// view model thật. <c>ShellStartup</c> gắn <see cref="Attach"/> sau khi dựng cửa sổ chính; trước đó thông báo bị bỏ.
/// </summary>
internal sealed class ShellNoticeSink
{
    private Action<string>? _show;

    internal void Attach(Action<string>? show) => _show = show;

    internal void Show(string message) => _show?.Invoke(message);
}

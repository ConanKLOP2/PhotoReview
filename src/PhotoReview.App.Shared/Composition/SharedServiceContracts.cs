using Microsoft.Extensions.DependencyInjection;
using PhotoReview.App.Coordinators;
using PhotoReview.Imaging.Decoding;

namespace PhotoReview.App.Composition;

// C-16 (NO-WPF-EXEC-PLAN mục 5): composition dùng chung giữa App WPF và shell. Thực thi ở WP-09 (shared) / WP-20 (shell).

/// <param name="Codec">Codec nền tảng (C-02): App WPF = WpfBitmapSourceCodec.Instance; Shell = PixelBufferImageCodec.Instance.</param>
/// <param name="WpfFallbackDecoder">Decoder fallback WPF (INV-12) của bản WPF; null ở bản Win32 (fallback theo NE-8).</param>
/// <param name="ThumbnailDecoder">Decoder cho ThumbnailCache (bản WPF: WPF; bản Win32: WIC).</param>
public sealed record SharedServiceOptions(IPlatformImageCodec Codec, Func<IServiceProvider, IImageDecoder>? WpfFallbackDecoder,
    Func<IServiceProvider, IImageDecoder> ThumbnailDecoder);

public static class SharedServiceRegistration
{
    /// <summary>
    /// Mọi đăng ký hiện ở App.ConfigureServices KHÔNG phụ thuộc WPF (paths, file system, settings, session, journal, file actions,
    /// recycle bin, decoders, caches, preload, ViewModels, coordinators). App WPF và Shell gọi hàm này rồi thêm phần riêng.
    /// WP-09 thực thi.
    /// </summary>
    public static IServiceCollection AddPhotoReviewShared(this IServiceCollection services, SharedServiceOptions options) =>
        throw new NotImplementedException();
}

/// <summary>WPF: WpfPresentationSink; Win32: Win32PresentationSink.</summary>
public interface IPresentationSinkFactory
{
    IPresentationSink Create(MainViewModelSinkCallbacks callbacks);
}

public sealed record MainViewModelSinkCallbacks(Action<object?, bool> SetCurrentImage, Action<string> SetStatusText,
    Action ApplyInitialViewMode, Action<string> OnPresented);

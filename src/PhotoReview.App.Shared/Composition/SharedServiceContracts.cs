using PhotoReview.App.Coordinators;
using PhotoReview.Imaging.Decoding;

namespace PhotoReview.App.Composition;

// C-16 (NO-WPF-EXEC-PLAN mục 5): composition dùng chung giữa App WPF và shell. Thực thi: WP-09 (shared, SharedServiceRegistration.cs)
// / WP-20 (shell).

/// <param name="Codec">Codec nền tảng (C-02): App WPF = WpfBitmapSourceCodec.Instance; Shell = PixelBufferImageCodec.Instance.</param>
/// <param name="WpfFallbackDecoder">Decoder fallback WPF (INV-12) của bản WPF; null ở bản Win32 (fallback theo NE-8).</param>
/// <param name="ThumbnailDecoder">Decoder cho ThumbnailCache (bản WPF: WPF; bản Win32: WIC). Dự trữ: từ WP-06 <c>ThumbnailCache</c>
/// giải mã thumbnail đĩa bằng WIC qua <paramref name="Codec"/>, nên WP-09 chỉ kiểm không null (xem NOWPF-WP09).</param>
public sealed record SharedServiceOptions(IPlatformImageCodec Codec, Func<IServiceProvider, IImageDecoder>? WpfFallbackDecoder,
    Func<IServiceProvider, IImageDecoder> ThumbnailDecoder);

/// <summary>WPF: WpfPresentationSink; Win32: Win32PresentationSink.</summary>
public interface IPresentationSinkFactory
{
    IPresentationSink Create(MainViewModelSinkCallbacks callbacks);
}

public sealed record MainViewModelSinkCallbacks(Action<object?, bool> SetCurrentImage, Action<string> SetStatusText,
    Action ApplyInitialViewMode, Action<string> OnPresented);

using System.IO;
using System.Runtime.InteropServices;
using PhotoReview.Imaging.Decoding.Wic;
using PhotoReview.Imaging.Pixels;

namespace PhotoReview.Imaging.Caching;

/// <summary>Kích thước khung đã giải mã của một entry cache (trước khi thu nhỏ) và việc có thu nhỏ hay không.</summary>
internal readonly record struct WicCacheImageInfo(int SourceWidth, int SourceHeight, bool Downscaled);

/// <summary>
/// WP-04: giải mã payload cache (JPEG của <see cref="PreviewCacheFile"/>, PNG của <see cref="ThumbnailCache"/>) đang nằm trong
/// bộ nhớ thẳng vào một <see cref="PixelBuffer"/> mới bằng WIC - thay <c>BitmapImage</c> của WPF. Cùng bộ giải mã WIC mà
/// BitmapImage dùng bên dưới và cùng phép chuyển sang 32bppBGR/PBGRA, nên pixel giống hệt đường WPF cũ (test so từng byte).
/// </summary>
/// <remarks>
/// Một lần đọc: dữ liệu đã nạp sẵn (không mở file lần hai), WIC đọc qua <c>IWICStream.InitializeFromMemory</c> (không chép sang
/// MemoryStream như đường cũ) và <c>CopyPixels</c> ghi thẳng vào buffer đích. Lỗi giải mã của WIC nổi lên dưới dạng
/// <see cref="COMException"/>, kích thước vô lý dưới dạng <see cref="InvalidDataException"/> - cả hai là "entry hỏng" với
/// <see cref="DiskCacheStore.IsCacheEntryFailure"/>.
/// </remarks>
internal static class WicCacheImageReader
{
    /// <summary>Entry cache lớn hơn mức này (byte pixel) không thể là preview/thumbnail hợp lệ: không cấp phát cho nó.</summary>
    internal const long MaxDecodedBytes = int.MaxValue;

    /// <param name="encoded">Ảnh đã mã hoá (JPEG/PNG...).</param>
    /// <param name="layout">Layout đích; null = theo định dạng khung (đục -&gt; Bgr32, có thể có alpha -&gt; Pbgra32).</param>
    /// <param name="box">Hộp thu nhỏ (không phóng to); <see cref="DecodeBox.Unbounded"/> = giữ nguyên kích thước.</param>
    /// <param name="expectedWidth">&gt; 0: kích thước khung bắt buộc (header của entry), kiểm tra TRƯỚC khi cấp phát.</param>
    /// <param name="expectedHeight">Như <paramref name="expectedWidth"/>.</param>
    /// <param name="info">Kích thước khung nguồn và việc có thu nhỏ.</param>
    /// <returns>Buffer mới; người gọi sở hữu (Dispose hoặc chuyển cho <c>IPlatformImageCodec.FromPixels</c>).</returns>
    internal static unsafe PixelBuffer Decode(ReadOnlySpan<byte> encoded, PixelLayout? layout, DecodeBox box, int expectedWidth, int expectedHeight,
        out WicCacheImageInfo info)
    {
        if (encoded.IsEmpty) throw new InvalidDataException("Cache entry payload is empty.");

        IWICImagingFactory? factory = null;
        IWICStream? stream = null;
        IWICBitmapDecoder? decoder = null;
        IWICBitmapFrameDecode? frame = null;
        IWICBitmapScaler? scaler = null;
        IWICFormatConverter? converter = null;
        PixelBuffer? pixels = null;
        fixed (byte* data = encoded)
        {
            try
            {
                factory = WicImageEncoder.CreateFactory();
                factory.CreateStream(out stream);
                stream.InitializeFromMemory((nint)data, (uint)encoded.Length);
                factory.CreateDecoderFromStream(stream, IntPtr.Zero, WICDecodeOptions.WICDecodeMetadataCacheOnDemand, out decoder);
                decoder.GetFrame(0, out frame);
                frame.GetSize(out var rawWidth, out var rawHeight);
                if (rawWidth == 0 || rawHeight == 0 || rawWidth > int.MaxValue || rawHeight > int.MaxValue)
                    throw new InvalidDataException("Cache entry payload has invalid dimensions.");
                var sourceWidth = (int)rawWidth;
                var sourceHeight = (int)rawHeight;
                if (expectedWidth > 0 && (sourceWidth != expectedWidth || sourceHeight != expectedHeight))
                    throw new InvalidDataException("Cache entry payload dimensions do not match its header.");

                frame.GetPixelFormat(out var frameFormat);
                var targetLayout = layout ?? (IsOpaqueFormat(frameFormat) ? PixelLayout.Bgr32 : PixelLayout.Pbgra32);

                IWICBitmapSource current = (IWICBitmapSource)frame;
                var (width, height) = box.IsUnbounded ? (sourceWidth, sourceHeight) : box.FitStored(sourceWidth, sourceHeight, transposed: false);
                var downscaled = width < sourceWidth || height < sourceHeight;
                if (downscaled)
                {
                    factory.CreateBitmapScaler(out scaler);
                    scaler.Initialize(current, (uint)width, (uint)height, WICBitmapInterpolationMode.Fant);
                    current = (IWICBitmapSource)scaler;
                }
                else
                {
                    (width, height) = (sourceWidth, sourceHeight);
                }

                if ((long)width * height * 4 > MaxDecodedBytes)
                    throw new InvalidDataException("Cache entry payload is implausibly large.");

                factory.CreateFormatConverter(out converter);
                var targetFormat = WicImageEncoder.SourceFormat(targetLayout);
                converter.Initialize(current, ref targetFormat, WICBitmapDitherType.None, IntPtr.Zero, 0.0, WICBitmapPaletteType.Custom);

                pixels = PixelBuffer.Allocate(width, height, targetLayout);
                ((IWICBitmapSource)converter).CopyPixels(IntPtr.Zero, (uint)pixels.Stride, (uint)pixels.ByteCount, pixels.Address);
                info = new WicCacheImageInfo(sourceWidth, sourceHeight, downscaled);
                var result = pixels;
                pixels = null;
                return result;
            }
            finally
            {
                pixels?.Dispose();
                WicImageEncoder.Release(converter);
                WicImageEncoder.Release(scaler);
                WicImageEncoder.Release(frame);
                WicImageEncoder.Release(decoder);
                WicImageEncoder.Release(stream);
                WicImageEncoder.Release(factory);
            }
        }
    }

    // Định dạng khung WIC không có kênh alpha (như danh sách của WicDirectDecoder); mọi định dạng khác (BGRA, indexed có thể
    // có palette trong suốt, ...) giải mã ra Pbgra32 để không làm phẳng độ trong suốt.
    private static readonly HashSet<Guid> OpaqueFormats =
    [
        WicGuids.GUID_WICPixelFormatBlackWhite,
        WicGuids.GUID_WICPixelFormat2bppGray,
        WicGuids.GUID_WICPixelFormat4bppGray,
        WicGuids.GUID_WICPixelFormat8bppGray,
        WicGuids.GUID_WICPixelFormat16bppGray,
        WicGuids.GUID_WICPixelFormat16bppBGR555,
        WicGuids.GUID_WICPixelFormat16bppBGR565,
        WicGuids.GUID_WICPixelFormat24bppBGR,
        WicGuids.GUID_WICPixelFormat24bppRGB,
        WicGuids.GUID_WICPixelFormat32bppBGR,
        WicGuids.GUID_WICPixelFormat48bppRGB,
        WicGuids.GUID_WICPixelFormat48bppBGR,
        WicGuids.GUID_WICPixelFormat32bppCMYK,
        WicGuids.GUID_WICPixelFormat64bppCMYK,
    ];

    internal static bool IsOpaqueFormat(Guid format) => OpaqueFormats.Contains(format);
}

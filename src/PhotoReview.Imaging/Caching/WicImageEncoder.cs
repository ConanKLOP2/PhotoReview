using System.Buffers;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Intrinsics;
using PhotoReview.Imaging.Decoding.Wic;
using PhotoReview.Imaging.Pixels;

namespace PhotoReview.Imaging.Caching;

/// <summary>
/// WP-04: mã hoá <see cref="PixelBuffer"/> thành JPEG/PNG bằng WIC (thay <c>JpegBitmapEncoder</c>/<c>PngBitmapEncoder</c> của WPF)
/// cho cache đĩa. Cùng bộ mã hoá WIC của Microsoft mà WPF gọi bên dưới, cùng tham số WPF đặt (vendor Microsoft, không cache,
/// 96 dpi, JPEG chỉ có <c>ImageQuality</c> = chất lượng/100), nên byte payload giống hệt bản WPF (test
/// <c>PreviewCacheCrossVersionTests</c> so từng byte) - định dạng file cache không đổi.
/// </summary>
/// <remarks>
/// Pixel đi thẳng từ buffer vào <c>IWICBitmapFrameEncode::WritePixels</c>: không có bản sao toàn ảnh. Khi bộ mã hoá chỉ nhận
/// 24bppBGR (JPEG, PNG đục), pixel được đổi BGRX -&gt; BGR theo từng dải dòng qua một bộ đệm nhỏ thuê từ pool; định dạng khác
/// (PNG có alpha: 32bppBGRA, cần bỏ premultiply) đi qua <c>WriteSource</c> trên một IWICBitmap tạm để WIC tự chuyển đúng như WPF.
/// Gọi trên luồng persist/nền; mỗi lần gọi tạo factory riêng như <see cref="WicDirectDecoder"/> (không chia sẻ RCW giữa luồng).
/// </remarks>
internal static class WicImageEncoder
{
    private static readonly Guid ContainerFormatJpeg = WicGuids.GUID_ContainerFormatJpeg;
    private static readonly Guid ContainerFormatPng = new("1b7cfaf4-713f-473c-bbcd-6137425faeaf");
    private static readonly Guid VendorMicrosoft = new("f0e749ca-edef-4589-a73a-ee0e626a2a2b"); // WPF BitmapEncoder dùng đúng vendor này
    private static readonly Guid PixelFormat24bppBgr = WicGuids.GUID_WICPixelFormat24bppBGR;

    private const uint WicBitmapEncoderNoCache = 2;   // WICBitmapEncoderNoCache, như BitmapEncoder.Save của WPF
    private const double Dpi = 96;                    // DPI mọi BitmapSource của decoder; JFIF/pHYs ghi đúng giá trị WPF đã ghi
    private const int BandBytes = 256 * 1024;         // dải chuyển BGRX -> BGR: vừa L2, không lên LOH

    /// <summary>JPEG baseline của WIC với <c>ImageQuality = quality / 100</c> (1..100). Ghi vào <paramref name="output"/> tại vị trí hiện tại.</summary>
    internal static void EncodeJpeg(PixelBuffer pixels, Stream output, int quality)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(quality, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(quality, 100);
        Encode(pixels, output, ContainerFormatJpeg, quality);
    }

    /// <summary>PNG của WIC với tham số mặc định (như <c>PngBitmapEncoder</c> không đặt Interlace).</summary>
    internal static void EncodePng(PixelBuffer pixels, Stream output) => Encode(pixels, output, ContainerFormatPng, quality: 0);

    private static void Encode(PixelBuffer pixels, Stream output, Guid container, int quality)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        ArgumentNullException.ThrowIfNull(output);
        ObjectDisposedException.ThrowIf(pixels.IsDisposed, pixels);
        if (pixels.ByteCount > uint.MaxValue) throw new ArgumentException("Pixel buffer is too large to encode.", nameof(pixels));

        IWICImagingFactory? factory = null;
        IWicBitmapEncoder? encoder = null;
        IWicBitmapFrameEncode? frame = null;
        IWicPropertyBag2? options = null;
        var stream = new ManagedIStream(output);
        try
        {
            factory = CreateFactory();
            var containerFormat = container;
            var vendor = VendorMicrosoft;
            factory.CreateEncoder(ref containerFormat, ref vendor, out var encoderPointer);
            encoder = WicCom.Wrap<IWicBitmapEncoder>(encoderPointer);
            encoder.Initialize(stream, WicBitmapEncoderNoCache);
            encoder.CreateNewFrame(out var framePointer, out var optionsPointer);
            frame = WicCom.Wrap<IWicBitmapFrameEncode>(framePointer);
            options = WicCom.Wrap<IWicPropertyBag2>(optionsPointer);
            if (quality > 0) WriteImageQuality(options, quality / 100f);
            frame.Initialize(options);
            frame.SetSize((uint)pixels.Width, (uint)pixels.Height);
            frame.SetResolution(Dpi, Dpi);

            var sourceFormat = SourceFormat(pixels.Layout);
            var acceptedFormat = sourceFormat;
            frame.SetPixelFormat(ref acceptedFormat);
            if (acceptedFormat == sourceFormat)
                frame.WritePixels((uint)pixels.Height, (uint)pixels.Stride, (uint)pixels.ByteCount, pixels.Address);
            else if (acceptedFormat == PixelFormat24bppBgr)
                WriteAsBgr24(frame, pixels);
            else
                WriteThroughWicConversion(factory, frame, pixels, sourceFormat);

            frame.Commit();
            encoder.Commit();
            GC.KeepAlive(pixels);
        }
        finally
        {
            Release(options);
            Release(frame);
            Release(encoder);
            Release(factory);
            stream.Dispose();
        }
    }

    /// <summary>Pixel layout của C-01 -&gt; GUID WIC (Bgr32 = 32bppBGR, Pbgra32 = 32bppPBGRA).</summary>
    internal static Guid SourceFormat(PixelLayout layout) => layout == PixelLayout.Pbgra32
        ? WicGuids.GUID_WICPixelFormat32bppPBGRA
        : WicGuids.GUID_WICPixelFormat32bppBGR;

    // Bộ mã hoá nhận 24bppBGR: bỏ byte thứ 4 (X, hoặc A khi Pbgra32 đã được xác nhận đục - premultiplied với A=255 bằng màu
    // thẳng) theo dải dòng. Đây đúng là phép WIC FormatConverter 32bppBGR/PBGRA -> 24bppBGR mà WPF chạy trong WriteSource.
    private static unsafe void WriteAsBgr24(IWicBitmapFrameEncode frame, PixelBuffer pixels)
    {
        var width = pixels.Width;
        var rowBytes = checked(width * 3);
        var rowsPerBand = Math.Clamp(BandBytes / rowBytes, 1, pixels.Height);
        // +16: bộ chuyển vector ghi 16 byte mỗi bước cho 12 byte có ích, nên đuôi dòng cuối được phép tràn vào phần dư.
        var band = ArrayPool<byte>.Shared.Rent(checked((rowsPerBand * rowBytes) + 16));
        try
        {
            fixed (byte* bandPointer = band)
            {
                for (var y = 0; y < pixels.Height; y += rowsPerBand)
                {
                    var rows = Math.Min(rowsPerBand, pixels.Height - y);
                    for (var r = 0; r < rows; r++)
                        Bgrx32ToBgr24(pixels.GetRow(y + r), new Span<byte>(bandPointer + ((long)r * rowBytes), rowBytes + 16));
                    frame.WritePixels((uint)rows, (uint)rowBytes, (uint)(rows * rowBytes), (nint)bandPointer);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(band);
        }
    }

    /// <summary>
    /// BGRX (4 byte/pixel) -&gt; BGR (3 byte/pixel). <paramref name="destination"/> cần dài ít nhất <c>3 * pixel + 16</c> khi
    /// dùng nhánh vector (ghi 16 byte, có ích 12); phần vượt quá <c>3 * pixel</c> bị dòng sau ghi đè hoặc bỏ qua.
    /// </summary>
    internal static void Bgrx32ToBgr24(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        var pixelCount = source.Length / 4;
        if (destination.Length < pixelCount * 3) throw new ArgumentException("Destination is too short.", nameof(destination));
        var i = 0;
        if (Vector128.IsHardwareAccelerated)
        {
            var shuffle = Vector128.Create((byte)0, 1, 2, 4, 5, 6, 8, 9, 10, 12, 13, 14, 0x80, 0x80, 0x80, 0x80);
            // Mỗi bước đọc 4 pixel (16 byte) và ghi 16 byte tại 3*i: cần 3*i + 16 <= destination.Length.
            for (; i + 4 <= pixelCount && (i * 3) + 16 <= destination.Length; i += 4)
            {
                var packed = Vector128.Shuffle(Vector128.Create(source.Slice(i * 4, 16)), shuffle);
                packed.CopyTo(destination.Slice(i * 3, 16));
            }
        }

        for (; i < pixelCount; i++)
        {
            destination[i * 3] = source[i * 4];
            destination[(i * 3) + 1] = source[(i * 4) + 1];
            destination[(i * 3) + 2] = source[(i * 4) + 2];
        }
    }

    // Định dạng khác (PNG + alpha -> 32bppBGRA): IWICBitmap tạm (một bản sao) rồi WriteSource để WIC tự chuyển, đúng đường WPF.
    private static void WriteThroughWicConversion(IWICImagingFactory factory, IWicBitmapFrameEncode frame, PixelBuffer pixels, Guid sourceFormat)
    {
        IWICBitmapSource? bitmap = null;
        try
        {
            factory.CreateBitmapFromMemory((uint)pixels.Width, (uint)pixels.Height, ref sourceFormat, (uint)pixels.Stride,
                (uint)pixels.ByteCount, pixels.Address, out var bitmapPointer);
            bitmap = WicCom.Wrap<IWICBitmapSource>(bitmapPointer);
            frame.WriteSource(bitmap, IntPtr.Zero);
        }
        finally
        {
            Release(bitmap);
        }
    }

    private static unsafe void WriteImageQuality(IWicPropertyBag2 options, float quality)
    {
        var name = Marshal.StringToCoTaskMemUni("ImageQuality");
        try
        {
            var bag = new PropBag2 { DwType = 1 /* PROPBAG2_TYPE_DATA */, Vt = VtR4, PstrName = name };
            // VARIANT: vt ở byte 0, giá trị VT_R4 ở byte 8; 24 byte đủ cho x64 (16 trên x86).
            var variant = stackalloc byte[24];
            new Span<byte>(variant, 24).Clear();
            *(ushort*)variant = VtR4;
            *(float*)(variant + 8) = quality;
            Marshal.ThrowExceptionForHR(options.Write(1, (nint)(&bag), (nint)variant));
        }
        finally
        {
            Marshal.FreeCoTaskMem(name);
        }
    }

    private const ushort VtR4 = 4;
    private const int EFail = unchecked((int)0x80004005);

    internal static IWICImagingFactory CreateFactory()
    {
        var hr = WicNativeMethods.WICCreateImagingFactory_Proxy(WicNativeMethods.WINCODEC_SDK_VERSION1, out var raw);
        try
        {
            Marshal.ThrowExceptionForHR(hr, new IntPtr(-1));
            if (raw == 0) throw Marshal.GetExceptionForHR(EFail, new IntPtr(-1))!;
        }
        catch
        {
            if (raw != 0) Marshal.Release(raw);
            throw;
        }

        return WicCom.Wrap<IWICImagingFactory>(raw);
    }

    /// <summary>Trả COM ref của wrapper WIC ngay (<see cref="WicCom.Release"/>); null/đối tượng managed là no-op.</summary>
    internal static void Release(object? comObject) => WicCom.Release(comObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct PropBag2
    {
        public uint DwType;
        public ushort Vt;
        public ushort CfType;
        public uint DwHint;
        public nint PstrName;
        public Guid Clsid;
    }
}

// COM interop cho phía mã hoá của WIC (vtable theo wincodec.h / ocidl.h). Phía giải mã nằm ở Decoding/Wic/WicInterop.cs (WP-03);
// WP-12: cả hai phía là [GeneratedComInterface] (quy ước con trỏ ra thô + WicCom.Wrap ở WicInterop.cs).

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("00000103-a8f2-4877-ba0a-fd2b6645fb94")]
internal partial interface IWicBitmapEncoder
{
    void Initialize(IComStream pIStream, uint cacheOption);                                    // [3]
    void GetContainerFormat(out Guid pguidContainerFormat);                                    // [4]
    void GetEncoderInfo(out nint ppIEncoderInfo);                                              // [5]
    void SetColorContexts(uint cCount, nint ppIColorContext);                                  // [6]
    void SetPalette(nint pIPalette);                                                           // [7]
    void SetThumbnail(nint pIThumbnail);                                                       // [8]
    void SetPreview(nint pIPreview);                                                           // [9]
    // Đối tượng ra là con trỏ thô (quy ước ở WicInterop.cs): bọc bằng WicCom.Wrap.
    void CreateNewFrame(out nint ppIFrameEncode, out nint ppIEncoderOptions);                  // [10]
    void Commit();                                                                             // [11]
    void GetMetadataQueryWriter(out nint ppIMetadataQueryWriter);                              // [12]
}

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("00000105-a8f2-4877-ba0a-fd2b6645fb94")]
internal partial interface IWicBitmapFrameEncode
{
    void Initialize(IWicPropertyBag2? pIEncoderOptions);                                       // [3]
    void SetSize(uint uiWidth, uint uiHeight);                                                 // [4]
    void SetResolution(double dpiX, double dpiY);                                              // [5]
    void SetPixelFormat(ref Guid pPixelFormat);                                                // [6]
    void SetColorContexts(uint cCount, nint ppIColorContext);                                  // [7]
    void SetPalette(nint pIPalette);                                                           // [8]
    void SetThumbnail(nint pIThumbnail);                                                       // [9]
    void WritePixels(uint lineCount, uint cbStride, uint cbBufferSize, nint pbPixels);         // [10]
    void WriteSource(IWICBitmapSource pIBitmapSource, nint prc);                               // [11]
    void Commit();                                                                             // [12]
    void GetMetadataQueryWriter(out nint ppIMetadataQueryWriter);                              // [13]
}

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("22F55882-280B-11d0-A8A9-00A0C90C2004")]
internal partial interface IWicPropertyBag2
{
    [PreserveSig]
    int Read(uint cProperties, nint pPropBag, nint pErrLog, nint pvarValue, nint phrError);    // [3]

    [PreserveSig]
    int Write(uint cProperties, nint pPropBag, nint pvarValue);                                // [4]

    void CountProperties(out uint pcProperties);                                               // [5]
    void GetPropertyInfo(uint iProperty, uint cProperties, nint pPropBag, out uint pcProperties); // [6]
    void LoadObject(string pstrName, uint dwHint, nint pUnkObject, nint pErrLog);              // [7]
}

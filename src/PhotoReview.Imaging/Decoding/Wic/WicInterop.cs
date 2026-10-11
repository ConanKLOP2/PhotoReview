using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace PhotoReview.Imaging.Decoding.Wic;

#pragma warning disable CA1712 // Do not prefix enum values with type name
#pragma warning disable CA1069 // Enums values should not be duplicated

internal static class WicGuids
{
    public static readonly Guid GUID_WICPixelFormat32bppBGRA = new("6fddc324-4e03-4bfe-b185-3d77768dc90f");
    public static readonly Guid GUID_WICPixelFormat32bppBGR = new("6fddc324-4e03-4bfe-b185-3d77768dc90e");
    public static readonly Guid GUID_WICPixelFormat32bppPBGRA = new("6fddc324-4e03-4bfe-b185-3d77768dc910");
    public static readonly Guid GUID_ContainerFormatJpeg = new("19e4a5aa-5662-4fc5-a0c0-1758028e1057");
    public static readonly Guid GUID_ContainerFormatTiff = new("163bcc30-e2e9-4f0b-961d-a3e9fdb788a3");

    // Pixel formats WIC defines without an alpha channel. Anything not listed is treated as
    // potentially transparent and decoded to premultiplied BGRA (see WicDirectDecoder).
    public static readonly Guid GUID_WICPixelFormatBlackWhite = new("6fddc324-4e03-4bfe-b185-3d77768dc905");
    public static readonly Guid GUID_WICPixelFormat2bppGray = new("6fddc324-4e03-4bfe-b185-3d77768dc906");
    public static readonly Guid GUID_WICPixelFormat4bppGray = new("6fddc324-4e03-4bfe-b185-3d77768dc907");
    public static readonly Guid GUID_WICPixelFormat8bppGray = new("6fddc324-4e03-4bfe-b185-3d77768dc908");
    public static readonly Guid GUID_WICPixelFormat16bppGray = new("6fddc324-4e03-4bfe-b185-3d77768dc90b");
    public static readonly Guid GUID_WICPixelFormat16bppBGR555 = new("6fddc324-4e03-4bfe-b185-3d77768dc909");
    public static readonly Guid GUID_WICPixelFormat16bppBGR565 = new("6fddc324-4e03-4bfe-b185-3d77768dc90a");
    public static readonly Guid GUID_WICPixelFormat24bppBGR = new("6fddc324-4e03-4bfe-b185-3d77768dc90c");
    public static readonly Guid GUID_WICPixelFormat24bppRGB = new("6fddc324-4e03-4bfe-b185-3d77768dc90d");
    public static readonly Guid GUID_WICPixelFormat48bppRGB = new("6fddc324-4e03-4bfe-b185-3d77768dc915");
    public static readonly Guid GUID_WICPixelFormat48bppBGR = new("e605a384-b468-46ce-bb2e-36f180e64313");
    public static readonly Guid GUID_WICPixelFormat32bppCMYK = new("6fddc324-4e03-4bfe-b185-3d77768dc91c");
    public static readonly Guid GUID_WICPixelFormat64bppCMYK = new("6fddc324-4e03-4bfe-b185-3d77768dc91f");
}

internal enum WICColorContextType : uint
{
    Uninitialized = 0,
    Profile = 1,
    ExifColorSpace = 2
}

internal enum WICDecodeOptions : uint
{
    WICDecodeMetadataCacheOnDemand = 0x00000000,
    WICDecodeMetadataCacheOnLoad = 0x00000001
}

internal enum WICBitmapDitherType : uint
{
    None = 0
}

internal enum WICBitmapPaletteType : uint
{
    Custom = 0
}

internal enum WICBitmapInterpolationMode : uint
{
    NearestNeighbor = 0x0,
    Linear = 0x1,
    Cubic = 0x2,
    Fant = 0x3,
    HighQualityCubic = 0x4
}

[Flags]
internal enum WICBitmapTransformOptions : uint
{
    Rotate0 = 0,
    Rotate90 = 1,
    Rotate180 = 2,
    Rotate270 = 3,
    FlipHorizontal = 8,
    FlipVertical = 16
}

// WP-12: WIC khai báo bằng [GeneratedComInterface] (source-generated, không ComImport/RCW runtime - thân thiện trim/Native AOT).
// Quy ước (như Shell.Interop/ComInterop, WP-13b): tham số RA kiểu interface khai báo `out nint` và bọc bằng WicCom.Wrap<T> (hoặc
// các hàm mở rộng ở WicComExtensions cùng chữ ký cũ `out IFoo`) vì chỉ wrapper UniqueInstance mới trả được COM ref ngay
// (FinalRelease); tham số VÀO kiểu interface dùng kiểu interface. Thứ tự method = vtable wincodec.h (slot ghi ở comment);
// interface dẫn xuất kế thừa IWICBitmapSource để slot 3-7 là của nguồn bitmap.

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("00000120-a8f2-4877-ba0a-fd2b6645fb94")]
internal partial interface IWICBitmapSource
{
    void GetSize(out uint puiWidth, out uint puiHeight);                                          // [3]
    void GetPixelFormat(out Guid pPixelFormat);                                                   // [4]
    void GetResolution(out double pDpiX, out double pDpiY);                                       // [5]
    void CopyPalette(nint pIPalette);                                                             // [6]
    void CopyPixels(nint prc, uint cbStride, uint cbBufferSize, nint pbBuffer);                   // [7]
}

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("3B16811B-6A43-4EC9-B713-3D5A0C13B940")]
internal partial interface IWICBitmapSourceTransform
{
    // vtable wincodec.h: CopyPixels, GetClosestSize, GetClosestPixelFormat, DoesSupportTransform.
    void CopyPixels(nint prcDst, uint uiWidth, uint uiHeight, ref Guid pguidDstFormat, uint dstTransform, uint nStride, uint cbBufferSize, nint pbBuffer); // [3]
    void GetClosestSize(ref uint puiWidth, ref uint puiHeight);                                   // [4]
    void GetClosestPixelFormat(ref Guid pguidDstFormat);                                          // [5]
    void DoesSupportTransform(uint dstTransform, out int pfIsSupported);                          // [6]
}

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("3B16811B-6A43-4EC9-A813-3D930C13B940")]
internal partial interface IWICBitmapFrameDecode : IWICBitmapSource
{
    void GetMetadataQueryReader(out nint ppIMetadataQueryReader);                                 // [8]
    // ppIColorContexts: mảng cCount con trỏ IWICColorContext do người gọi tạo (CreateColorContext); 0 khi chỉ hỏi số lượng.
    void GetColorContexts(uint cCount, nint ppIColorContexts, out uint pcActualCount);            // [9]
    void GetThumbnail(out nint ppIThumbnail);                                                     // [10]
}

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("3C613A02-34B2-44EA-9A7C-45AEA9C6FD6D")]
internal partial interface IWICColorContext
{
    void InitializeFromFilename(string wzFilename);                                               // [3]
    void InitializeFromMemory(nint pbBuffer, uint cbBufferSize);                                  // [4]
    void InitializeFromExifColorSpace(uint value);                                                // [5]
    // IWICColorContext::GetType (đổi tên để khỏi lẫn object.GetType; slot không đổi).
    void GetContextType(out WICColorContextType pType);                                           // [6]
    void GetProfileBytes(uint cbBuffer, nint pbBuffer, out uint pcbActual);                       // [7]
    void GetExifColorSpace(out uint pValue);                                                      // [8]
}

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("B66F034F-D0E2-40AB-B436-6DE39E321A94")]
internal partial interface IWICColorTransform : IWICBitmapSource
{
    void Initialize(IWICBitmapSource pIBitmapSource, IWICColorContext pIContextSource, IWICColorContext pIContextDest,
        ref Guid pixelFmtDest);                                                                   // [8]
}

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("9EDDE9E7-8DEE-47EA-99DF-E6FAF2ED44BF")]
internal partial interface IWICBitmapDecoder
{
    void QueryCapability(IComStream pIStream, out uint pdwCapability);                            // [3]
    void Initialize(IComStream pIStream, WICDecodeOptions cacheOptions);                          // [4]
    void GetContainerFormat(out Guid pguidContainerFormat);                                       // [5]
    void GetDecoderInfo(out nint ppIDecoderInfo);                                                 // [6]
    void CopyPalette(nint pIPalette);                                                             // [7]
    void GetMetadataQueryReader(out nint ppIMetadataQueryReader);                                 // [8]
    void GetPreview(out nint ppIPreview);                                                         // [9]
    void GetColorContexts(uint cCount, nint ppIColorContexts, out uint pcActualCount);            // [10]
    void GetThumbnail(out nint ppIThumbnail);                                                     // [11]
    void GetFrameCount(out uint pCount);                                                          // [12]
    void GetFrame(uint index, out nint ppIBitmapFrame);                                           // [13]
}

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("00000301-a8f2-4877-ba0a-fd2b6645fb94")]
internal partial interface IWICFormatConverter : IWICBitmapSource
{
    void Initialize(IWICBitmapSource pISource, ref Guid dstFormat, WICBitmapDitherType dither, nint pIPalette,
        double alphaThresholdPercent, WICBitmapPaletteType paletteTranslate);                     // [8]
    void CanConvert(ref Guid srcPixelFormat, ref Guid dstPixelFormat, out int pfCanConvert);      // [9]
}

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("00000302-a8f2-4877-ba0a-fd2b6645fb94")]
internal partial interface IWICBitmapScaler : IWICBitmapSource
{
    void Initialize(IWICBitmapSource pISource, uint uiWidth, uint uiHeight, WICBitmapInterpolationMode mode); // [8]
}

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("5009834F-2D6A-41CE-9E1B-17C5AFF7A782")]
internal partial interface IWICBitmapFlipRotator : IWICBitmapSource
{
    void Initialize(IWICBitmapSource pISource, WICBitmapTransformOptions options);                // [8]
}

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("30989668-E1C9-4597-B395-458EEDB808DF")]
internal partial interface IWICMetadataQueryReader
{
    void GetContainerFormat(out Guid pguidContainerFormat);                                       // [3]
    void GetLocation(uint cchMaxLength, nint wzNamespace, out uint pcchActualLength);             // [4]
    // PreserveSig: thiếu tag (WINCODEC_ERR_PROPERTYNOTFOUND) là chuyện thường khi đọc nhiều tag EXIF; trả HRESULT để khỏi ném
    // một COMException first-chance cho mỗi tag vắng trên đường decode.
    [PreserveSig]
    int GetMetadataByName(string wzName, nint pvarValue);                                         // [5]
    void GetEnumerator(out nint ppIEnumString);                                                   // [6]
}

/// <summary>COM <c>IStream</c> (IID 0000000c-0000-0000-C000-000000000046): ISequentialStream (Read, Write) + IStream.</summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("0000000c-0000-0000-C000-000000000046")]
internal unsafe partial interface IComStream
{
    void Read(byte* pv, int cb, nint pcbRead);                                                    // [3]
    void Write(byte* pv, int cb, nint pcbWritten);                                                // [4]
    void Seek(long dlibMove, int dwOrigin, nint plibNewPosition);                                 // [5]
    void SetSize(long libNewSize);                                                                // [6]
    void CopyTo(nint pstm, long cb, nint pcbRead, nint pcbWritten);                               // [7]
    void Commit(int grfCommitFlags);                                                              // [8]
    void Revert();                                                                                // [9]
    void LockRegion(long libOffset, long cb, int dwLockType);                                     // [10]
    void UnlockRegion(long libOffset, long cb, int dwLockType);                                   // [11]
    void Stat(out StatStgNative pstatstg, int grfStatFlag);                                       // [12]
    void Clone(out nint ppstm);                                                                   // [13]
}

/// <summary>STATSTG (objidl.h) với con trỏ tên thô (luôn null: STATFLAG_NONAME); 80 byte trên x64.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct StatStgNative
{
    public nint pwcsName;
    public int type;
    public long cbSize;
    public long mtime;
    public long ctime;
    public long atime;
    public int grfMode;
    public int grfLocksSupported;
    public Guid clsid;
    public int grfStateBits;
    public int reserved;
}

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("135FF860-22B7-4DDF-B0F6-218F4F299A43")]
internal unsafe partial interface IWICStream : IComStream
{
    void InitializeFromIStream(IComStream pIStream);                                              // [14]
    void InitializeFromFilename(string wzFileName, uint dwDesiredAccess);                         // [15]
    void InitializeFromMemory(nint pbBuffer, uint cbBufferSize);                                  // [16]
    void InitializeFromIStreamRegion(IComStream pIStream, ulong ulOffset, ulong ulMaxSize);       // [17]
}

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("ec5ec8a9-c395-4314-9c77-54d7a935ff70")]
internal partial interface IWICImagingFactory
{
    void CreateDecoderFromFilename(string wzFilename, nint pguidVendor, uint dwDesiredAccess, WICDecodeOptions metadataOptions,
        out nint ppIDecoder);                                                                     // [3]
    void CreateDecoderFromStream(IComStream pIStream, nint pguidVendor, WICDecodeOptions metadataOptions,
        out nint ppIDecoder);                                                                     // [4]
    void CreateDecoderFromFileHandle(nint hFile, nint pguidVendor, WICDecodeOptions metadataOptions, out nint ppIDecoder); // [5]
    void CreateComponentInfo(ref Guid clsidComponent, out nint ppIInfo);                          // [6]
    void CreateDecoder(ref Guid guidContainerFormat, ref Guid pguidVendor, out nint ppIDecoder);  // [7]
    void CreateEncoder(ref Guid guidContainerFormat, ref Guid pguidVendor, out nint ppIEncoder);  // [8]
    void CreatePalette(out nint ppIPalette);                                                      // [9]
    void CreateFormatConverter(out nint ppIFormatConverter);                                      // [10]
    void CreateBitmapScaler(out nint ppIBitmapScaler);                                            // [11]
    void CreateBitmapClipper(out nint ppIBitmapClipper);                                          // [12]
    void CreateBitmapFlipRotator(out nint ppIBitmapFlipRotator);                                  // [13]
    void CreateStream(out nint ppIWICStream);                                                     // [14]
    void CreateColorContext(out nint ppIColorContext);                                            // [15]
    void CreateColorTransformer(out nint ppIColorTransform);                                      // [16]
    void CreateBitmap(uint uiWidth, uint uiHeight, ref Guid pixelFormat, uint option, out nint ppIBitmap); // [17]
    void CreateBitmapFromSource(IWICBitmapSource pIBitmapSource, uint option, out nint ppIBitmap); // [18]
    void CreateBitmapFromSourceRect(IWICBitmapSource pIBitmapSource, uint x, uint y, uint width, uint height, out nint ppIBitmap); // [19]
    void CreateBitmapFromMemory(uint uiWidth, uint uiHeight, ref Guid pixelFormat, uint cbStride, uint cbBufferSize, nint pbBuffer,
        out nint ppIBitmap);                                                                      // [20]
    void CreateBitmapFromHBITMAP(nint hBitmap, nint hPalette, uint options, out nint ppIBitmap);  // [21]
    void CreateBitmapFromHICON(nint hIcon, out nint ppIBitmap);                                   // [22]
    void CreateComponentEnumerator(uint componentTypes, uint options, out nint ppIEnumUnknown);   // [23]
    void CreateFastMetadataEncoderFromDecoder(IWICBitmapDecoder pIDecoder, out nint ppIFastEncoder); // [24]
    void CreateFastMetadataEncoderFromFrameDecode(IWICBitmapFrameDecode pIFrameDecoder, out nint ppIFastEncoder); // [25]
    void CreateQueryWriter(ref Guid guidMetadataFormat, ref Guid pguidVendor, out nint ppIQueryWriter); // [26]
    void CreateQueryWriterFromReader(IWICMetadataQueryReader pIQueryReader, ref Guid pguidVendor, out nint ppIQueryWriter); // [27]
}

/// <summary>
/// Biên giữa con trỏ COM thô và đối tượng <c>[GeneratedComInterface]</c>: bọc con trỏ vừa nhận (out nint) và trả COM ref NGAY
/// (không đợi GC) - thay cho <c>Marshal.ReleaseComObject</c> của RCW cũ (vô tác dụng với wrapper của ComWrappers).
/// </summary>
internal static class WicCom
{
    private static readonly StrategyBasedComWrappers Wrappers = new();
    private const int EFail = unchecked((int)0x80004005);

    /// <summary>
    /// Wrapper RIÊNG (UniqueInstance - cache chung không trả ref khi <see cref="Release"/>) cho con trỏ COM vừa nhận; CHIẾM 1 ref
    /// của <paramref name="unknown"/> (wrapper giữ ref riêng, ref truyền vào được trả lại ở đây, cả khi bọc thất bại).
    /// Con trỏ 0 ném COMException E_FAIL (một đối tượng ra bắt buộc mà WIC trả S_OK + null; E_FAIL luôn ánh xạ sang COMException,
    /// CA2201 cấm tự dựng), cùng loại lỗi backend mà fallback WPF xử lý.
    /// </summary>
    internal static T Wrap<T>(nint unknown) where T : class
    {
        if (unknown == 0) throw Marshal.GetExceptionForHR(EFail, new IntPtr(-1))!;
        object? wrapper = null;
        try
        {
            wrapper = Wrappers.GetOrCreateObjectForComInstance(unknown, CreateObjectFlags.UniqueInstance);
            var typed = (T)wrapper; // QueryInterface for T's IID; a pointer that is not a T throws here
            t_outstanding++;
            return typed;
        }
        catch
        {
            (wrapper as ComObject)?.FinalRelease(); // not handed out: give its reference back now instead of at finalization
            throw;
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    // Diagnostics for the lifetime tests (per thread: every WIC call chain runs on one thread, so tests running in parallel do
    // not see each other): wrappers handed out minus wrappers released. Back to its starting value after a complete call chain.
    [ThreadStatic]
    private static int t_outstanding;

    /// <summary>Wrappers created by <see cref="Wrap{T}"/> on this thread and not yet <see cref="Release"/>d.</summary>
    internal static int OutstandingOnThisThread => t_outstanding;

    /// <summary>Như <see cref="Wrap{T}"/> nhưng con trỏ 0 cho null (đối tượng ra tuỳ chọn, vd. thumbnail).</summary>
    internal static T? WrapOrNull<T>(nint unknown) where T : class => unknown == 0 ? null : Wrap<T>(unknown);

    /// <summary>Trả COM ref của wrapper ngay; null, đối tượng managed (fake test) hay lần gọi thứ hai là no-op.</summary>
    internal static void Release(object? comObject)
    {
        if (comObject is not ComObject wrapper) return;
        wrapper.FinalRelease();
        t_outstanding--;
    }
}

/// <summary>
/// Dạng gọi tiện lợi `out IFoo` của các method có đối tượng ra (interface sinh dùng `out nint`, xem quy ước ở đầu file): cùng chữ ký
/// như bản ComImport cũ nên chỗ gọi không đổi; mỗi đối tượng ra được bọc ngay thành wrapper có thể <see cref="WicCom.Release"/>.
/// </summary>
internal static class WicComExtensions
{
    public static void CreateDecoderFromStream(this IWICImagingFactory f, IComStream stream, nint vendor, WICDecodeOptions options,
        out IWICBitmapDecoder decoder)
    {
        f.CreateDecoderFromStream(stream, vendor, options, out nint p);
        decoder = WicCom.Wrap<IWICBitmapDecoder>(p);
    }

    public static void CreateFormatConverter(this IWICImagingFactory f, out IWICFormatConverter converter)
    {
        f.CreateFormatConverter(out nint p);
        converter = WicCom.Wrap<IWICFormatConverter>(p);
    }

    public static void CreateBitmapScaler(this IWICImagingFactory f, out IWICBitmapScaler scaler)
    {
        f.CreateBitmapScaler(out nint p);
        scaler = WicCom.Wrap<IWICBitmapScaler>(p);
    }

    public static void CreateBitmapFlipRotator(this IWICImagingFactory f, out IWICBitmapFlipRotator rotator)
    {
        f.CreateBitmapFlipRotator(out nint p);
        rotator = WicCom.Wrap<IWICBitmapFlipRotator>(p);
    }

    public static void CreateStream(this IWICImagingFactory f, out IWICStream stream)
    {
        f.CreateStream(out nint p);
        stream = WicCom.Wrap<IWICStream>(p);
    }

    public static void CreateColorContext(this IWICImagingFactory f, out IWICColorContext context)
    {
        f.CreateColorContext(out nint p);
        context = WicCom.Wrap<IWICColorContext>(p);
    }

    public static void CreateColorTransformer(this IWICImagingFactory f, out IWICColorTransform transform)
    {
        f.CreateColorTransformer(out nint p);
        transform = WicCom.Wrap<IWICColorTransform>(p);
    }

    public static void GetFrame(this IWICBitmapDecoder d, uint index, out IWICBitmapFrameDecode frame)
    {
        d.GetFrame(index, out nint p);
        frame = WicCom.Wrap<IWICBitmapFrameDecode>(p);
    }

    public static void GetMetadataQueryReader(this IWICBitmapFrameDecode f, out IWICMetadataQueryReader reader)
    {
        f.GetMetadataQueryReader(out nint p);
        reader = WicCom.Wrap<IWICMetadataQueryReader>(p);
    }

    /// <summary>Thumbnail nhúng; null khi codec trả S_OK mà không có đối tượng.</summary>
    public static void GetThumbnail(this IWICBitmapFrameDecode f, out IWICBitmapSource? thumbnail)
    {
        f.GetThumbnail(out nint p);
        thumbnail = WicCom.WrapOrNull<IWICBitmapSource>(p);
    }
}

/// <summary>
/// Bọc .NET <see cref="Stream"/> thành COM <c>IStream</c> (CCW của [GeneratedComClass]).
/// Cho phép đưa FileStream với chia sẻ native ReadWrite | Delete vào WIC mà không cần khoá riêng.
/// </summary>
[GeneratedComClass]
internal sealed unsafe partial class ManagedIStream : IComStream, IDisposable
{
    private Stream? _stream;

    public ManagedIStream(Stream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    }

    public void Read(byte* pv, int cb, nint pcbRead)
    {
        ObjectDisposedException.ThrowIf(_stream is null, this);
        var bytesRead = _stream.Read(new Span<byte>(pv, cb));
        if (pcbRead != IntPtr.Zero)
        {
            Marshal.WriteInt32(pcbRead, bytesRead);
        }
    }

    public void Write(byte* pv, int cb, nint pcbWritten)
    {
        ObjectDisposedException.ThrowIf(_stream is null, this);
        _stream.Write(new ReadOnlySpan<byte>(pv, cb));
        if (pcbWritten != IntPtr.Zero)
        {
            Marshal.WriteInt32(pcbWritten, cb);
        }
    }

    public void Seek(long dlibMove, int dwOrigin, nint plibNewPosition)
    {
        ObjectDisposedException.ThrowIf(_stream is null, this);
        var origin = (SeekOrigin)dwOrigin;
        var newPos = _stream.Seek(dlibMove, origin);
        if (plibNewPosition != IntPtr.Zero)
        {
            Marshal.WriteInt64(plibNewPosition, newPos);
        }
    }

    public void SetSize(long libNewSize)
    {
        ObjectDisposedException.ThrowIf(_stream is null, this);
        _stream.SetLength(libNewSize);
    }

    public void CopyTo(nint pstm, long cb, nint pcbRead, nint pcbWritten)
    {
        throw new NotSupportedException();
    }

    public void Commit(int grfCommitFlags)
    {
        _stream?.Flush();
    }

    public void Revert()
    {
    }

    public void LockRegion(long libOffset, long cb, int dwLockType)
    {
    }

    public void UnlockRegion(long libOffset, long cb, int dwLockType)
    {
    }

    public void Stat(out StatStgNative pstatstg, int grfStatFlag)
    {
        ObjectDisposedException.ThrowIf(_stream is null, this);
        pstatstg = new StatStgNative
        {
            type = 2, // STGTY_STREAM
            cbSize = _stream.Length,
            grfMode = 0 // STGM_READ
        };
    }

    public void Clone(out nint ppstm)
    {
        throw new NotSupportedException();
    }

    public void Dispose()
    {
        _stream = null;
    }
}

internal static partial class WicNativeMethods
{
    public const uint WINCODEC_SDK_VERSION1 = 0x0236;

    [LibraryImport("WindowsCodecs.dll", EntryPoint = "WICCreateImagingFactory_Proxy")]
    public static partial int WICCreateImagingFactory_Proxy(uint sdkVersion, out nint ppIImagingFactory);
}

#pragma warning restore CA1712
#pragma warning restore CA1069

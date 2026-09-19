using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace PhotoReview.Imaging.Decoding.Wic;

#pragma warning disable CA1712 // Do not prefix enum values with type name
#pragma warning disable CA1069 // Enums values should not be duplicated

internal static class WicGuids
{
    public static readonly Guid CLSID_WICImagingFactory = new("cac5261a-05e2-4928-9d9d-a30f36abf114");
    public static readonly Guid CLSID_WICImagingFactory2 = new("31741610-e414-49f2-b690-acf12f15ecab");
    public static readonly Guid GUID_WICPixelFormat32bppBGRA = new("6fddc324-4e03-4bfe-b185-3d77768dc90f");
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

[ComImport]
[Guid("00000120-a8f2-4877-ba0a-fd2b6645fb94")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IWICBitmapSource
{
    void GetSize(out uint puiWidth, out uint puiHeight);
    void GetPixelFormat(out Guid pPixelFormat);
    void GetResolution(out double pDpiX, out double pDpiY);
    void CopyPalette(IntPtr pIPalette);
    void CopyPixels(IntPtr prc, uint cbStride, uint cbBufferSize, IntPtr pbBuffer);
}

[ComImport]
[Guid("3B16811B-6A43-4EC9-B713-3D5A0C13B940")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IWICBitmapSourceTransform
{
    void DoesSupportTransform(uint dstTransform, out int pfIsSupported);
    void GetClosestSize(ref uint puiWidth, ref uint puiHeight);
    void GetClosestPixelFormat(ref Guid pguidDstFormat);
    void CopyPixels(IntPtr prcDst, uint uiWidth, uint uiHeight, ref Guid pguidDstFormat, uint dstTransform, uint nStride, uint cbBufferSize, IntPtr pbBuffer);
}

[ComImport]
[Guid("3B16811B-6A43-4EC9-A813-3D930C13B940")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IWICBitmapFrameDecode
{
    // IWICBitmapSource methods
    void GetSize(out uint puiWidth, out uint puiHeight);
    void GetPixelFormat(out Guid pPixelFormat);
    void GetResolution(out double pDpiX, out double pDpiY);
    void CopyPalette(IntPtr pIPalette);
    void CopyPixels(IntPtr prc, uint cbStride, uint cbBufferSize, IntPtr pbBuffer);

    // IWICBitmapFrameDecode methods
    void GetMetadataQueryReader(out IWICMetadataQueryReader ppIMetadataQueryReader);
    void GetColorContexts(uint cCount, IntPtr ppIColorContexts, out uint pcActualCount);
    void GetThumbnail(out IWICBitmapSource ppIThumbnail);
}

[ComImport]
[Guid("9EDDE9E7-8DEE-47EA-99DF-E6FAF2ED44BF")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IWICBitmapDecoder
{
    void QueryCapability(IStream pIStream, out uint pdwCapability);
    void Initialize(IStream pIStream, WICDecodeOptions cacheOptions);
    void GetContainerFormat(out Guid pguidContainerFormat);
    void GetDecoderInfo(out IntPtr ppIDecoderInfo);
    void CopyPalette(IntPtr pIPalette);
    void GetMetadataQueryReader(out IWICMetadataQueryReader ppIMetadataQueryReader);
    void GetPreview(out IWICBitmapSource ppIPreview);
    void GetColorContexts(uint cCount, IntPtr ppIColorContexts, out uint pcActualCount);
    void GetThumbnail(out IWICBitmapSource ppIThumbnail);
    void GetFrameCount(out uint pCount);
    void GetFrame(uint index, out IWICBitmapFrameDecode ppIBitmapFrame);
}

[ComImport]
[Guid("00000301-a8f2-4877-ba0a-fd2b6645fb94")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IWICFormatConverter
{
    // IWICBitmapSource methods
    void GetSize(out uint puiWidth, out uint puiHeight);
    void GetPixelFormat(out Guid pPixelFormat);
    void GetResolution(out double pDpiX, out double pDpiY);
    void CopyPalette(IntPtr pIPalette);
    void CopyPixels(IntPtr prc, uint cbStride, uint cbBufferSize, IntPtr pbBuffer);

    // IWICFormatConverter methods
    void Initialize(
        IWICBitmapSource pISource,
        [In] ref Guid dstFormat,
        WICBitmapDitherType dither,
        IntPtr pIPalette,
        double alphaThresholdPercent,
        WICBitmapPaletteType paletteTranslate);
    void CanConvert(ref Guid srcPixelFormat, ref Guid dstPixelFormat, out int pfCanConvert);
}

[ComImport]
[Guid("00000302-a8f2-4877-ba0a-fd2b6645fb94")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IWICBitmapScaler
{
    // IWICBitmapSource methods
    void GetSize(out uint puiWidth, out uint puiHeight);
    void GetPixelFormat(out Guid pPixelFormat);
    void GetResolution(out double pDpiX, out double pDpiY);
    void CopyPalette(IntPtr pIPalette);
    void CopyPixels(IntPtr prc, uint cbStride, uint cbBufferSize, IntPtr pbBuffer);

    // IWICBitmapScaler methods
    void Initialize(IWICBitmapSource pISource, uint uiWidth, uint uiHeight, WICBitmapInterpolationMode mode);
}

[ComImport]
[Guid("5009834F-2D6A-41CE-9E1B-17C5AFF7A782")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IWICBitmapFlipRotator
{
    // IWICBitmapSource methods
    void GetSize(out uint puiWidth, out uint puiHeight);
    void GetPixelFormat(out Guid pPixelFormat);
    void GetResolution(out double pDpiX, out double pDpiY);
    void CopyPalette(IntPtr pIPalette);
    void CopyPixels(IntPtr prc, uint cbStride, uint cbBufferSize, IntPtr pbBuffer);

    // IWICBitmapFlipRotator methods
    void Initialize(IWICBitmapSource pISource, WICBitmapTransformOptions options);
}

[ComImport]
[Guid("30989668-E1C9-4597-B395-458EEDB808DF")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IWICMetadataQueryReader
{
    void GetContainerFormat(out Guid pguidContainerFormat);
    void GetLocation(uint cchMaxLength, [Out] char[] wzNamespace, out uint pcchActualLength);
    void GetMetadataByName(string wzName, IntPtr pvarValue);
    void GetEnumerator(out IntPtr ppIEnumString);
}

[ComImport]
[Guid("135FF860-22B7-4DDF-B0F6-218F4F299A43")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IWICStream : IStream
{
    // IStream methods
    new void Read([Out] byte[] pv, int cb, IntPtr pcbRead);
    new void Write([In] byte[] pv, int cb, IntPtr pcbWritten);
    new void Seek(long dlibMove, int dwOrigin, IntPtr plibNewPosition);
    new void SetSize(long libNewSize);
    new void CopyTo(IStream pstm, long cb, IntPtr pcbRead, IntPtr pcbWritten);
    new void Commit(int grfCommitFlags);
    new void Revert();
    new void LockRegion(long libOffset, long cb, int dwLockType);
    new void UnlockRegion(long libOffset, long cb, int dwLockType);
    new void Stat(out STATSTG pstatstg, int grfStatFlag);
    new void Clone(out IStream ppstm);

    // IWICStream methods
    void InitializeFromIStream(IStream pIStream);
    void InitializeFromFile(string wzFileName, uint dwDesiredAccess);
    void InitializeFromMemory(IntPtr pbBuffer, uint cbBufferSize);
    void InitializeFromMemoryEx(IntPtr pbBuffer, ulong cbBufferSize);
}

[ComImport]
[Guid("ec5ec8a9-c395-4314-9c77-54d7a935ff70")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IWICImagingFactory
{
    void CreateDecoderFromFilename(
        [MarshalAs(UnmanagedType.LPWStr)] string wzFilename,
        IntPtr pguidVendor,
        uint dwDesiredAccess,
        WICDecodeOptions metadataOptions,
        out IWICBitmapDecoder ppIDecoder);

    void CreateDecoderFromStream(
        IStream pIStream,
        IntPtr pguidVendor,
        WICDecodeOptions metadataOptions,
        out IWICBitmapDecoder ppIDecoder);

    void CreateDecoderFromFileHandle(
        IntPtr hFile,
        IntPtr pguidVendor,
        WICDecodeOptions metadataOptions,
        out IWICBitmapDecoder ppIDecoder);

    void CreateComponentInfo(ref Guid clsidComponent, out IntPtr ppIInfo);
    void CreateDecoder(ref Guid guidContainerFormat, ref Guid pguidVendor, out IWICBitmapDecoder ppIDecoder);
    void CreateEncoder(ref Guid guidContainerFormat, ref Guid pguidVendor, out IntPtr ppIEncoder);
    void CreatePalette(out IntPtr ppIPalette);
    void CreateFormatConverter(out IWICFormatConverter ppIFormatConverter);
    void CreateBitmapScaler(out IWICBitmapScaler ppIBitmapScaler);
    void CreateBitmapClipper(out IntPtr ppIBitmapClipper);
    void CreateBitmapFlipRotator(out IWICBitmapFlipRotator ppIBitmapFlipRotator);
    void CreateStream(out IWICStream ppIWICStream);
    void CreateColorContext(out IntPtr ppIColorContext);
    void CreateColorTransformer(out IntPtr ppIColorTransform);
    void CreateBitmap(uint uiWidth, uint uiHeight, ref Guid pixelFormat, uint option, out IntPtr ppIBitmap);
    void CreateBitmapFromSource(IWICBitmapSource pIBitmapSource, uint option, out IntPtr ppIBitmap);
    void CreateBitmapFromSourceRect(IWICBitmapSource pIBitmapSource, uint x, uint y, uint width, uint height, out IntPtr ppIBitmap);
    void CreateBitmapFromMemory(uint uiWidth, uint uiHeight, ref Guid pixelFormat, uint cbStride, uint cbBufferSize, IntPtr pbBuffer, out IntPtr ppIBitmap);
    void CreateBitmapFromHBITMAP(IntPtr hBitmap, IntPtr hPalette, uint options, out IntPtr ppIBitmap);
    void CreateBitmapFromHICON(IntPtr hIcon, out IntPtr ppIBitmap);
    void CreateComponentEnumerator(uint componentTypes, uint options, out IntPtr ppIEnumUnknown);
    void CreateFastMetadataEncoderFromDecoder(IWICBitmapDecoder pIDecoder, out IntPtr ppIFastEncoder);
    void CreateFastMetadataEncoderFromFrameDecode(IWICBitmapFrameDecode pIFrameDecoder, out IntPtr ppIFastEncoder);
    void CreateQueryWriter(ref Guid guidMetadataFormat, ref Guid pguidVendor, out IntPtr ppIQueryWriter);
    void CreateQueryWriterFromReader(IWICMetadataQueryReader pIQueryReader, ref Guid pguidVendor, out IntPtr ppIQueryWriter);
}

/// <summary>
/// Managed wrapper around a .NET <see cref="Stream"/> providing the COM <see cref="IStream"/> interface.
/// Enables passing FileStream with native ReadWrite | Delete sharing to WIC without proprietary lock.
/// </summary>
internal sealed class ManagedIStream : IStream, IDisposable
{
    private Stream? _stream;

    public ManagedIStream(Stream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    }

    public void Read(byte[] pv, int cb, IntPtr pcbRead)
    {
        ObjectDisposedException.ThrowIf(_stream is null, this);
        var bytesRead = _stream.Read(pv, 0, cb);
        if (pcbRead != IntPtr.Zero)
        {
            Marshal.WriteInt32(pcbRead, bytesRead);
        }
    }

    public void Write(byte[] pv, int cb, IntPtr pcbWritten)
    {
        ObjectDisposedException.ThrowIf(_stream is null, this);
        _stream.Write(pv, 0, cb);
        if (pcbWritten != IntPtr.Zero)
        {
            Marshal.WriteInt32(pcbWritten, cb);
        }
    }

    public void Seek(long dlibMove, int dwOrigin, IntPtr plibNewPosition)
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

    public void CopyTo(IStream pstm, long cb, IntPtr pcbRead, IntPtr pcbWritten)
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

    public void Stat(out STATSTG pstatstg, int grfStatFlag)
    {
        ObjectDisposedException.ThrowIf(_stream is null, this);
        pstatstg = new STATSTG
        {
            type = 2, // STGTY_STREAM
            cbSize = _stream.Length,
            grfMode = 0 // STGM_READ
        };
    }

    public void Clone(out IStream ppstm)
    {
        throw new NotSupportedException();
    }

    public void Dispose()
    {
        _stream = null;
    }
}

internal static class WicNativeMethods
{
    public const uint WINCODEC_SDK_VERSION1 = 0x0236;
    public const uint WINCODEC_SDK_VERSION2 = 0x0237;

    [DllImport("WindowsCodecs.dll", EntryPoint = "WICCreateImagingFactory_Proxy", ExactSpelling = true)]
    public static extern int WICCreateImagingFactory_Proxy(uint sdkVersion, out IWICImagingFactory ppIImagingFactory);
}

#pragma warning restore CA1712
#pragma warning restore CA1069


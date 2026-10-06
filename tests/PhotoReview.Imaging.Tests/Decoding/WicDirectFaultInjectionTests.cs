using IStream = System.Runtime.InteropServices.ComTypes.IStream;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using PhotoReview.Core.Abstractions;
using PhotoReview.Imaging.Decoding.Wic;
using PhotoReview.Imaging.Tests.Fixtures;

namespace PhotoReview.Imaging.Tests.Decoding;

/// <summary>
/// Mutation-gap tests for <see cref="WicDirectDecoder"/> failure paths: the catch filters around metadata, colour-context and
/// container-format reads, driven with managed fakes of the WIC interfaces, and the E_INVALIDARG mapping through real decodes.
/// </summary>
public sealed class WicDirectFaultInjectionTests : IDisposable
{
    private readonly TempRoot _root = new("wic-fault");

    public void Dispose() => _root.Dispose();

    private static MethodInfo Private(string name) =>
        typeof(WicDirectDecoder).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("WicDirectDecoder." + name + " was renamed or removed; update this test.");

    private static T Unwrap<T>(Func<T> call)
    {
        try { return call(); }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    private static COMException ComFault(string message = "injected") =>
#pragma warning disable CA2201
        new COMException(message, unchecked((int)0x88982F50));
#pragma warning restore CA2201

    private sealed class FakeFrame(Func<IWICMetadataQueryReader>? metadata = null, Action<IWICColorContext[]?, uint>? colorContexts = null) : IWICBitmapFrameDecode
    {
        public int ColorContextCalls;
        public void GetSize(out uint puiWidth, out uint puiHeight) => throw new NotImplementedException();
        public void GetPixelFormat(out Guid pPixelFormat) => throw new NotImplementedException();
        public void GetResolution(out double pDpiX, out double pDpiY) => throw new NotImplementedException();
        public void CopyPalette(IntPtr pIPalette) => throw new NotImplementedException();
        public void CopyPixels(IntPtr prc, uint cbStride, uint cbBufferSize, IntPtr pbBuffer) => throw new NotImplementedException();
        public void GetMetadataQueryReader(out IWICMetadataQueryReader ppIMetadataQueryReader) =>
            ppIMetadataQueryReader = (metadata ?? throw new NotImplementedException())();
        public void GetColorContexts(uint cCount, IWICColorContext[]? ppIColorContexts, out uint pcActualCount)
        {
            ColorContextCalls++;
            if (colorContexts is null) throw new NotImplementedException();
            colorContexts(ppIColorContexts, cCount);
            pcActualCount = cCount == 0 ? 1u : cCount;
        }
        public void GetThumbnail(out IWICBitmapSource ppIThumbnail) => throw new NotImplementedException();
    }

    private sealed class FakeContext : IWICColorContext
    {
        public void InitializeFromFilename(string wzFilename) => throw new NotImplementedException();
        public void InitializeFromMemory(IntPtr pbBuffer, uint cbBufferSize) => throw new NotImplementedException();
        public void InitializeFromExifColorSpace(uint value) => throw new NotImplementedException();
        public void GetContextType(out WICColorContextType pType) => pType = WICColorContextType.Profile;
        public void GetProfileBytes(uint cbBuffer, IntPtr pbBuffer, out uint pcbActual) => throw new NotImplementedException();
        public void GetExifColorSpace(out uint pValue) => throw new NotImplementedException();
    }

    private sealed class FakeFactory(Func<IWICColorContext> createContext) : IWICImagingFactory
    {
        public void CreateColorContext(out IWICColorContext ppIColorContext) => ppIColorContext = createContext();
        public void CreateDecoderFromFilename(string wzFilename, IntPtr pguidVendor, uint dwDesiredAccess, WICDecodeOptions metadataOptions, out IWICBitmapDecoder ppIDecoder) => throw new NotImplementedException();
        public void CreateDecoderFromStream(IStream pIStream, IntPtr pguidVendor, WICDecodeOptions metadataOptions, out IWICBitmapDecoder ppIDecoder) => throw new NotImplementedException();
        public void CreateDecoderFromFileHandle(IntPtr hFile, IntPtr pguidVendor, WICDecodeOptions metadataOptions, out IWICBitmapDecoder ppIDecoder) => throw new NotImplementedException();
        public void CreateComponentInfo(ref Guid clsidComponent, out IntPtr ppIInfo) => throw new NotImplementedException();
        public void CreateDecoder(ref Guid guidContainerFormat, ref Guid pguidVendor, out IWICBitmapDecoder ppIDecoder) => throw new NotImplementedException();
        public void CreateEncoder(ref Guid guidContainerFormat, ref Guid pguidVendor, out IntPtr ppIEncoder) => throw new NotImplementedException();
        public void CreatePalette(out IntPtr ppIPalette) => throw new NotImplementedException();
        public void CreateFormatConverter(out IWICFormatConverter ppIFormatConverter) => throw new NotImplementedException();
        public void CreateBitmapScaler(out IWICBitmapScaler ppIBitmapScaler) => throw new NotImplementedException();
        public void CreateBitmapClipper(out IntPtr ppIBitmapClipper) => throw new NotImplementedException();
        public void CreateBitmapFlipRotator(out IWICBitmapFlipRotator ppIBitmapFlipRotator) => throw new NotImplementedException();
        public void CreateStream(out IWICStream ppIWICStream) => throw new NotImplementedException();
        public void CreateColorTransformer(out IWICColorTransform ppIColorTransform) => throw new NotImplementedException();
        public void CreateBitmap(uint uiWidth, uint uiHeight, ref Guid pixelFormat, uint option, out IntPtr ppIBitmap) => throw new NotImplementedException();
        public void CreateBitmapFromSource(IWICBitmapSource pIBitmapSource, uint option, out IntPtr ppIBitmap) => throw new NotImplementedException();
        public void CreateBitmapFromSourceRect(IWICBitmapSource pIBitmapSource, uint x, uint y, uint width, uint height, out IntPtr ppIBitmap) => throw new NotImplementedException();
        public void CreateBitmapFromMemory(uint uiWidth, uint uiHeight, ref Guid pixelFormat, uint cbStride, uint cbBufferSize, IntPtr pbBuffer, out IntPtr ppIBitmap) => throw new NotImplementedException();
        public void CreateBitmapFromHBITMAP(IntPtr hBitmap, IntPtr hPalette, uint options, out IntPtr ppIBitmap) => throw new NotImplementedException();
        public void CreateBitmapFromHICON(IntPtr hIcon, out IntPtr ppIBitmap) => throw new NotImplementedException();
        public void CreateComponentEnumerator(uint componentTypes, uint options, out IntPtr ppIEnumUnknown) => throw new NotImplementedException();
        public void CreateFastMetadataEncoderFromDecoder(IWICBitmapDecoder pIDecoder, out IntPtr ppIFastEncoder) => throw new NotImplementedException();
        public void CreateFastMetadataEncoderFromFrameDecode(IWICBitmapFrameDecode pIFrameDecoder, out IntPtr ppIFastEncoder) => throw new NotImplementedException();
        public void CreateQueryWriter(ref Guid guidMetadataFormat, ref Guid pguidVendor, out IntPtr ppIQueryWriter) => throw new NotImplementedException();
        public void CreateQueryWriterFromReader(IWICMetadataQueryReader pIQueryReader, ref Guid pguidVendor, out IntPtr ppIQueryWriter) => throw new NotImplementedException();
    }

    private sealed class FakeDecoder(Action containerFormat) : IWICBitmapDecoder
    {
        public void GetContainerFormat(out Guid pguidContainerFormat) { containerFormat(); pguidContainerFormat = Guid.Empty; }
        public void QueryCapability(IStream pIStream, out uint pdwCapability) => throw new NotImplementedException();
        public void Initialize(IStream pIStream, WICDecodeOptions cacheOptions) => throw new NotImplementedException();
        public void GetDecoderInfo(out IntPtr ppIDecoderInfo) => throw new NotImplementedException();
        public void CopyPalette(IntPtr pIPalette) => throw new NotImplementedException();
        public void GetMetadataQueryReader(out IWICMetadataQueryReader ppIMetadataQueryReader) => throw new NotImplementedException();
        public void GetPreview(out IWICBitmapSource ppIPreview) => throw new NotImplementedException();
        public void GetColorContexts(uint cCount, IntPtr ppIColorContexts, out uint pcActualCount) => throw new NotImplementedException();
        public void GetThumbnail(out IWICBitmapSource ppIThumbnail) => throw new NotImplementedException();
        public void GetFrameCount(out uint pCount) => throw new NotImplementedException();
        public void GetFrame(uint index, out IWICBitmapFrameDecode ppIBitmapFrame) => throw new NotImplementedException();
    }

    // ---- ReadFrameMetadata: a frame without a metadata reader reads as unrotated / no EXIF ----

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReadFrameMetadata_FrameWithoutMetadataReader_IsOrientationOneAndNoExif(bool readOrientation)
    {
        var frame = new FakeFrame(metadata: () => throw ComFault());
        var args = new object?[] { frame, readOrientation, "/app1/ifd", null };

        var orientation = Unwrap(() => (int)Private("ReadFrameMetadata").Invoke(null, args)!);

        Assert.Equal(1, orientation);
        Assert.Null(args[3]);
    }

    [Fact]
    public void ReadFrameMetadata_OutOfMemory_IsNotSwallowed()
    {
        #pragma warning disable CA2201
        var frame = new FakeFrame(metadata: () => throw new OutOfMemoryException());
#pragma warning restore CA2201
        var args = new object?[] { frame, true, null, null };

        Assert.Throws<OutOfMemoryException>(() => Unwrap(() => Private("ReadFrameMetadata").Invoke(null, args)));
    }

    // ---- ExifIfdRootOf ----

    [Fact]
    public void ExifIfdRootOf_ContainerFormatFails_IsNull()
    {
        var decoder = new FakeDecoder(() => throw ComFault());

        Assert.Null(Unwrap(() => Private("ExifIfdRootOf").Invoke(null, [decoder])));
    }

    [Fact]
    public void ExifIfdRootOf_UnknownContainer_IsNull() =>
        Assert.Null(Unwrap(() => Private("ExifIfdRootOf").Invoke(null, [new FakeDecoder(() => { })])));

    // ---- ReadColorContexts ----

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReadColorContexts_CountQueryFails_IsUntaggedSrgb(bool argumentFault)
    {
        var frame = new FakeFrame(colorContexts: (_, _) => throw (argumentFault ? new ArgumentException("damaged TIFF header") : ComFault()));

        var result = Unwrap(() => Private("ReadColorContexts").Invoke(null, [new FakeFactory(() => new FakeContext()), frame]));

        Assert.Null(result);
        Assert.Equal(1, frame.ColorContextCalls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ReadColorContexts_SecondQueryOrContextCreationFails_IsUntaggedSrgb(int failure)
    {
        // failure 0: CreateColorContext throws COM; 1: CreateColorContext throws ArgumentException; 2: the filling GetColorContexts throws COM.
        var frame = new FakeFrame(colorContexts: (_, count) => { if (count > 0 && failure == 2) throw ComFault(); });
        IWICColorContext Create() => failure switch
        {
            0 => throw ComFault(),
            1 => throw new ArgumentException("bad"),
            _ => new FakeContext(),
        };

        var result = Unwrap(() => Private("ReadColorContexts").Invoke(null, [new FakeFactory(Create), frame]));

        Assert.Null(result);
    }

    [Fact]
    public void ReadColorContexts_UnexpectedFault_Propagates()
    {
        var frame = new FakeFrame(colorContexts: (_, _) => { });
        var factory = new FakeFactory(() => throw new InvalidOperationException("not a WIC data fault"));

        Assert.Throws<InvalidOperationException>(() => Unwrap(() => Private("ReadColorContexts").Invoke(null, [factory, frame])));
    }

    [Fact]
    public void ReadColorContexts_Succeeds_ReturnsTheContexts()
    {
        var frame = new FakeFrame(colorContexts: (_, _) => { });

        var result = (IWICColorContext[]?)Unwrap(() => Private("ReadColorContexts").Invoke(null, [new FakeFactory(() => new FakeContext()), frame]));

        Assert.NotNull(result);
        Assert.Single(result);
    }

    // ---- ColorTransformChain.Build ----

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ColorTransformChain_WicFault_IsReportedAsIccTransformFailure(int fault)
    {
        var chainType = typeof(WicDirectDecoder).GetNestedType("ColorTransformChain", BindingFlags.NonPublic)!;
        var chain = Activator.CreateInstance(chainType)!;
        var build = chainType.GetMethod("Build")!;
        IWICColorContext Create() => fault switch
        {
            0 => throw ComFault(),
            1 => throw new InvalidCastException("bad cast"),
            _ => throw new ArgumentException("bad"),
        };

        var ex = Assert.Throws<NotSupportedException>(() => Unwrap(() => build.Invoke(chain, [new FakeFactory(Create), null!, new FakeContext(), true])));

        Assert.Contains("ICC", ex.Message);
        Assert.False((bool)chainType.GetProperty("IsActive")!.GetValue(chain)!);
    }

    // ---- the E_INVALIDARG mapping of Decode / ReadInfo ----

    private sealed class ThrowOnReadStream(Stream inner, Func<Exception> fault) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw fault();
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        // IMG-R04: the wrapper owns the file stream it was given; leaking it keeps a handle on the fixture file open.
        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class FaultReader(Func<Exception> fault) : ISourceReader
    {
        public Stream OpenSource(string path, SourceReadPriority priority, int bufferSize = 1024 * 1024) =>
            new ThrowOnReadStream(File.OpenRead(path), fault);
    }

    [Fact]
    public void Decode_StreamReportsInvalidArgument_IsInvalidDataNotArgumentException()
    {
        var path = FixtureGenerator.GenerateGradientJpeg(_root.Combine("a.jpg"), 64, 48);
        var decoder = new WicDirectDecoder(new FaultReader(() => new ArgumentException("E_INVALIDARG from stream")));

        var ex = Record.Exception(() => decoder.Decode(new DecodeRequest(path, 0)));

        Assert.IsType<InvalidDataException>(ex);
    }

    [Fact]
    public void ReadInfo_StreamReportsInvalidArgument_IsInvalidDataNotArgumentException()
    {
        var path = FixtureGenerator.GenerateGradientJpeg(_root.Combine("b.jpg"), 64, 48);
        var decoder = new WicDirectDecoder(new FaultReader(() => new ArgumentException("E_INVALIDARG from stream")));

        var ex = Record.Exception(() => decoder.ReadInfo(path));

        Assert.IsType<InvalidDataException>(ex);
    }
}

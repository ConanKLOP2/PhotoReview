using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using PhotoReview.Imaging.Decoding.Wic;
using PhotoReview.Imaging.Tests.Fixtures;

namespace PhotoReview.Imaging.Tests.Decoding;

/// <summary>
/// WP-12: the <c>[GeneratedComInterface]</c> boundary of WIC itself - COM reference ownership of <see cref="WicCom"/>, the pointer
/// seam of the colour-context read, the native struct layout and the QueryInterface-backed casts of source-generated wrappers.
/// Real WIC objects are used where the behaviour under test is the reference count (a managed fake cannot show a leak).
/// </summary>
public sealed partial class WicGeneratedComTests : IDisposable
{
    private readonly TempRoot _root = new("wic-gencom");

    public void Dispose() => _root.Dispose();

    private static readonly StrategyBasedComWrappers Wrappers = new();

#pragma warning disable CA2201 // COMException simulates the WIC codec failing
    private static COMException Fault() => new("injected", unchecked((int)0x88982F50));
#pragma warning restore CA2201

    private static nint NewRawScaler()
    {
        var factory = WicDirectDecoder.CreateFactory();
        try
        {
            factory.CreateBitmapScaler(out nint raw);
            return raw;
        }
        finally
        {
            WicCom.Release(factory);
        }
    }

    [Fact(DisplayName = "Release gives the wrapper's COM reference back at once: the native object is destroyed")]
    public void Release_DropsTheWrappersReferenceImmediately()
    {
        var raw = NewRawScaler();
        Marshal.AddRef(raw); // our own reference, to observe the count after the wrapper let go
        var wrapper = WicCom.Wrap<IWICBitmapScaler>(raw);
        var whileWrapped = Marshal.AddRef(raw);
        Marshal.Release(raw);
        Assert.True(whileWrapped >= 3, $"the wrapper must hold its own reference (count was {whileWrapped})");

        WicCom.Release(wrapper);

        Assert.Equal(0u, (uint)Marshal.Release(raw)); // only our reference was left; it was the last one
    }

    [Fact(DisplayName = "Release is idempotent and tolerates null and managed objects")]
    public void Release_NullManagedAndRepeated_AreNoOps()
    {
        var wrapper = WicCom.Wrap<IWICBitmapScaler>(NewRawScaler());
        WicCom.Release(wrapper);
        WicCom.Release(wrapper);
        WicCom.Release(null);
        WicCom.Release(new object());
    }

    [Fact(DisplayName = "Wrap rejects a null pointer; WrapOrNull maps it to null")]
    public void NullPointer_WrapThrows_WrapOrNullIsNull()
    {
        Assert.Throws<COMException>(() => WicCom.Wrap<IWICBitmapScaler>(0));
        Assert.Null(WicCom.WrapOrNull<IWICBitmapScaler>(0));
    }

    [Fact(DisplayName = "Casting a generated wrapper is a QueryInterface: supported interfaces cast, unsupported ones are 'is' false")]
    public void Cast_IsQueryInterface()
    {
        var path = FixtureGenerator.GenerateGradientJpeg(_root.Combine("a.jpg"), 64, 48);
        var factory = WicDirectDecoder.CreateFactory();
        using var file = File.OpenRead(path);
        using var stream = new ManagedIStream(file);
        IWICBitmapDecoder? decoder = null;
        IWICBitmapFrameDecode? frame = null;
        try
        {
            factory.CreateDecoderFromStream(stream, 0, WICDecodeOptions.WICDecodeMetadataCacheOnDemand, out decoder);
            decoder.GetFrame(0, out frame);

            Assert.True(frame is IWICBitmapSourceTransform); // a JPEG frame supports DCT-scaled reads
            Assert.False(frame is IWICStream);               // E_NOINTERFACE is "false", never an exception
            IWICBitmapSource source = frame;
            source.GetSize(out var width, out var height);
            Assert.Equal((64u, 48u), (width, height));
        }
        finally
        {
            WicCom.Release(frame);
            WicCom.Release(decoder);
            WicCom.Release(factory);
        }
    }

    [Fact(DisplayName = "STATSTG native struct: x64 layout of objidl.h (80 bytes, fixed offsets)")]
    public void StatStgNative_MatchesTheNativeLayout()
    {
        if (IntPtr.Size != 8) return;

        Assert.Equal(80, Marshal.SizeOf<StatStgNative>());
        Assert.Equal(8, (int)Marshal.OffsetOf<StatStgNative>(nameof(StatStgNative.type)));
        Assert.Equal(16, (int)Marshal.OffsetOf<StatStgNative>(nameof(StatStgNative.cbSize)));
        Assert.Equal(48, (int)Marshal.OffsetOf<StatStgNative>(nameof(StatStgNative.grfMode)));
        Assert.Equal(56, (int)Marshal.OffsetOf<StatStgNative>(nameof(StatStgNative.clsid)));
        Assert.Equal(72, (int)Marshal.OffsetOf<StatStgNative>(nameof(StatStgNative.grfStateBits)));
    }

    // ---- ReadColorContexts: the raw pointers it creates are released on every failure path ----

    [GeneratedComClass]
    private sealed partial class Context : IWICColorContext
    {
        public void InitializeFromFilename(string wzFilename) => throw new NotImplementedException();
        public void InitializeFromMemory(nint pbBuffer, uint cbBufferSize) => throw new NotImplementedException();
        public void InitializeFromExifColorSpace(uint value) => throw new NotImplementedException();
        public void GetContextType(out WICColorContextType pType) => pType = WICColorContextType.Profile;
        public void GetProfileBytes(uint cbBuffer, nint pbBuffer, out uint pcbActual) => throw new NotImplementedException();
        public void GetExifColorSpace(out uint pValue) => throw new NotImplementedException();
    }

    [GeneratedComClass]
    private sealed partial class Frame(Action<nint, uint> colorContexts) : IWICBitmapFrameDecode
    {
        public void GetSize(out uint puiWidth, out uint puiHeight) => throw new NotImplementedException();
        public void GetPixelFormat(out Guid pPixelFormat) => throw new NotImplementedException();
        public void GetResolution(out double pDpiX, out double pDpiY) => throw new NotImplementedException();
        public void CopyPalette(nint pIPalette) => throw new NotImplementedException();
        public void CopyPixels(nint prc, uint cbStride, uint cbBufferSize, nint pbBuffer) => throw new NotImplementedException();
        public void GetMetadataQueryReader(out nint ppIMetadataQueryReader) => throw new NotImplementedException();
        public void GetColorContexts(uint cCount, nint ppIColorContexts, out uint pcActualCount)
        {
            colorContexts(ppIColorContexts, cCount);
            pcActualCount = cCount == 0 ? 2u : cCount;
        }

        public void GetThumbnail(out nint ppIThumbnail) => throw new NotImplementedException();
    }

    /// <summary>A real factory is not needed: only CreateColorContext is called, handing out CCWs of <see cref="Context"/> that
    /// the test also holds one reference on (so a leak is visible as a count above what the test itself holds).</summary>
    [GeneratedComClass]
    private sealed partial class Factory(List<nint> created, Func<int, bool> failAt) : IWICImagingFactory
    {
        public void CreateColorContext(out nint ppIColorContext)
        {
            if (failAt(created.Count)) throw Fault();
            var pointer = (nint)Wrappers.GetOrCreateComInterfaceForObject(new Context(), CreateComInterfaceFlags.None);
            Marshal.AddRef(pointer); // the test's own reference
            created.Add(pointer);
            ppIColorContext = pointer;
        }

        public void CreateDecoderFromFilename(string wzFilename, nint pguidVendor, uint dwDesiredAccess, WICDecodeOptions metadataOptions, out nint ppIDecoder) => throw new NotImplementedException();
        public void CreateDecoderFromStream(IComStream pIStream, nint pguidVendor, WICDecodeOptions metadataOptions, out nint ppIDecoder) => throw new NotImplementedException();
        public void CreateDecoderFromFileHandle(nint hFile, nint pguidVendor, WICDecodeOptions metadataOptions, out nint ppIDecoder) => throw new NotImplementedException();
        public void CreateComponentInfo(ref Guid clsidComponent, out nint ppIInfo) => throw new NotImplementedException();
        public void CreateDecoder(ref Guid guidContainerFormat, ref Guid pguidVendor, out nint ppIDecoder) => throw new NotImplementedException();
        public void CreateEncoder(ref Guid guidContainerFormat, ref Guid pguidVendor, out nint ppIEncoder) => throw new NotImplementedException();
        public void CreatePalette(out nint ppIPalette) => throw new NotImplementedException();
        public void CreateFormatConverter(out nint ppIFormatConverter) => throw new NotImplementedException();
        public void CreateBitmapScaler(out nint ppIBitmapScaler) => throw new NotImplementedException();
        public void CreateBitmapClipper(out nint ppIBitmapClipper) => throw new NotImplementedException();
        public void CreateBitmapFlipRotator(out nint ppIBitmapFlipRotator) => throw new NotImplementedException();
        public void CreateStream(out nint ppIWICStream) => throw new NotImplementedException();
        public void CreateColorTransformer(out nint ppIColorTransform) => throw new NotImplementedException();
        public void CreateBitmap(uint uiWidth, uint uiHeight, ref Guid pixelFormat, uint option, out nint ppIBitmap) => throw new NotImplementedException();
        public void CreateBitmapFromSource(IWICBitmapSource pIBitmapSource, uint option, out nint ppIBitmap) => throw new NotImplementedException();
        public void CreateBitmapFromSourceRect(IWICBitmapSource pIBitmapSource, uint x, uint y, uint width, uint height, out nint ppIBitmap) => throw new NotImplementedException();
        public void CreateBitmapFromMemory(uint uiWidth, uint uiHeight, ref Guid pixelFormat, uint cbStride, uint cbBufferSize, nint pbBuffer, out nint ppIBitmap) => throw new NotImplementedException();
        public void CreateBitmapFromHBITMAP(nint hBitmap, nint hPalette, uint options, out nint ppIBitmap) => throw new NotImplementedException();
        public void CreateBitmapFromHICON(nint hIcon, out nint ppIBitmap) => throw new NotImplementedException();
        public void CreateComponentEnumerator(uint componentTypes, uint options, out nint ppIEnumUnknown) => throw new NotImplementedException();
        public void CreateFastMetadataEncoderFromDecoder(IWICBitmapDecoder pIDecoder, out nint ppIFastEncoder) => throw new NotImplementedException();
        public void CreateFastMetadataEncoderFromFrameDecode(IWICBitmapFrameDecode pIFrameDecoder, out nint ppIFastEncoder) => throw new NotImplementedException();
        public void CreateQueryWriter(ref Guid guidMetadataFormat, ref Guid pguidVendor, out nint ppIQueryWriter) => throw new NotImplementedException();
        public void CreateQueryWriterFromReader(IWICMetadataQueryReader pIQueryReader, ref Guid pguidVendor, out nint ppIQueryWriter) => throw new NotImplementedException();
    }

    private static object? ReadColorContexts(IWICImagingFactory factory, IWICBitmapFrameDecode frame)
    {
        var method = typeof(WicDirectDecoder).GetMethod("ReadColorContexts", BindingFlags.NonPublic | BindingFlags.Static)!;
        try
        {
            return method.Invoke(null, [factory, frame]);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    [Fact(DisplayName = "GetColorContexts failing releases every context pointer already created")]
    public void ReadColorContexts_FillFails_ReleasesTheRawPointers()
    {
        var created = new List<nint>();
        var frame = new Frame((_, count) => { if (count > 0) throw Fault(); });

        var result = ReadColorContexts(new Factory(created, _ => false), frame);

        Assert.Null(result);
        Assert.Equal(2, created.Count);
        // Each pointer: 1 (CCW creation, handed to the caller) + 1 (test's) = 2; the production release leaves only the test's one.
        Assert.All(created, p => Assert.Equal(0u, (uint)Marshal.Release(p)));
    }

    [Fact(DisplayName = "CreateColorContext failing midway releases the contexts already created")]
    public void ReadColorContexts_SecondCreateFails_ReleasesTheFirst()
    {
        var created = new List<nint>();
        var frame = new Frame((_, _) => { });

        var result = ReadColorContexts(new Factory(created, index => index == 1), frame);

        Assert.Null(result);
        Assert.Single(created);
        Assert.Equal(0u, (uint)Marshal.Release(created[0]));
    }

    [Fact(DisplayName = "Success hands every context to a wrapper (the raw pointer reference is consumed, none is left twice)")]
    public void ReadColorContexts_Success_WrapsEveryPointer()
    {
        var created = new List<nint>();
        var frame = new Frame((_, _) => { });

        var result = (IWICColorContext[]?)ReadColorContexts(new Factory(created, _ => false), frame);

        Assert.NotNull(result);
        Assert.Equal(2, created.Count);
        try
        {
            Assert.Equal(2, result.Length);
            // Each wrapper reports the CCW's own answer: the call went through the pointer that was created.
            foreach (var context in result)
            {
                context.GetContextType(out var type);
                Assert.Equal(WICColorContextType.Profile, type);
            }
        }
        finally
        {
            foreach (var context in result) WicCom.Release(context);
        }

        // creation 1 + test 1 = 2; the wrap consumed the first, the wrapper's own (and its QI) went with Release: only the test's is left.
        Assert.All(created, p => Assert.Equal(0u, (uint)Marshal.Release(p)));
    }
}

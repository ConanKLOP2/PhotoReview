using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoReview.Imaging.Decoding.Wic;
using PhotoReview.Imaging.Tests.Fixtures;
using PhotoReview.TestSupport;

namespace PhotoReview.Imaging.Tests.Decoding;

/// <summary>
/// Mutation-testing gap closers for <see cref="WicDirectDecoder"/> (Stryker survivors ~L59, L295, L379, L439-453, L546, L608-610,
/// L681-689): the pure decisions that sit between the COM calls (native-reduction predicate, colour-context choice, PROPVARIANT
/// shapes, orientation range, size and memory guards) driven with managed fakes of the WIC interfaces, plus the EXIF container
/// choice and the Adobe RGB colour transform through real decodes.
/// </summary>
public sealed class WicDirectInternalsMutationTests : IDisposable
{
    private readonly TempRoot _root = new("wic-internals");

    public void Dispose() => _root.Dispose();

    private static MethodInfo Private(string name) =>
        typeof(WicDirectDecoder).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("WicDirectDecoder." + name + " was renamed or removed; update this test.");

    // WP-03: the metadata half of WicDirectDecoder moved to WicExifReader.
    private static MethodInfo ExifReaderMethod(string name) =>
        typeof(WicExifReader).GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("WicExifReader." + name + " was renamed or removed; update this test.");

    // ---- ToIntSize / EnsureOutputFits ----

    [Theory]
    [InlineData(1u, 1u)]
    [InlineData(int.MaxValue, 1u)]
    [InlineData(1u, int.MaxValue)]
    [InlineData(int.MaxValue, int.MaxValue)]
    public void ToIntSize_WithinTheIntRange_IsReturnedUnchanged(uint width, uint height)
    {
        var (w, h) = WicDirectDecoder.ToIntSize(width, height);

        Assert.Equal(((int)width, (int)height), (w, h));
    }

    [Theory]
    [InlineData(int.MaxValue + 1u, 1u)]
    [InlineData(1u, int.MaxValue + 1u)]
    [InlineData(int.MaxValue + 1u, int.MaxValue + 1u)]
    [InlineData(uint.MaxValue, 5u)]
    [InlineData(5u, uint.MaxValue)]
    public void ToIntSize_BeyondTheIntRangeOnEitherAxis_IsRefusedAsInvalidData(uint width, uint height)
    {
        Assert.Throws<InvalidDataException>(() => WicDirectDecoder.ToIntSize(width, height));
    }

    [Fact]
    public void EnsureOutputFits_BufferOfExactlyTheGuardThreshold_ConsultsTheMemoryBudget()
    {
        var consulted = 0;

        Assert.Throws<DecoderMemoryAdmissionException>(() => WicDirectDecoder.EnsureOutputFits(
            8192, 4096, MemoryHeadroom.GuardThresholdBytes, () => { consulted++; return (MemoryHeadroom.GuardThresholdBytes / 2, 0); }));

        Assert.Equal(1, consulted);
    }

    [Fact(DisplayName = "R16: a byte count that wrapped negative is never mistaken for a small buffer")]
    public void EnsureOutputFits_NegativeWrappedLength_IsRefused()
    {
        var wrapped = unchecked((long)int.MaxValue * int.MaxValue * 4);

        Assert.True(wrapped < 0);
        Assert.Throws<DecoderMemoryAdmissionException>(() => WicDirectDecoder.EnsureOutputFits(
            int.MaxValue, int.MaxValue, wrapped, () => (64L * 1024 * 1024 * 1024, 0)));
    }

    [Theory(DisplayName = "R16: the output byte count saturates instead of wrapping")]
    [InlineData(1, 1, 4L)]
    [InlineData(6000, 6000, 144_000_000L)]
    [InlineData(int.MaxValue, int.MaxValue, long.MaxValue)]
    public void OutputByteLength_SaturatesInsteadOfWrapping(int width, int height, long expected)
        => Assert.Equal(expected, WicDirectDecoder.OutputByteLength(width, height));

    [Fact(DisplayName = "R16: a huge buffer whose doubled peak would overflow a long is refused")]
    public void OutputHasHeadroom_BufferWhosePeakOverflows_IsRefused()
        => Assert.False(MemoryHeadroom.OutputHasHeadroom(long.MaxValue, 64L * 1024 * 1024 * 1024, 0));

    [Fact]
    public void EnsureOutputFits_BufferJustBelowTheGuardThreshold_NeverConsultsTheMemoryBudget()
    {
        var consulted = false;
        WicDirectDecoder.EnsureOutputFits(8192, 4095, MemoryHeadroom.GuardThresholdBytes - 1,
            () => { consulted = true; throw new InvalidOperationException("must not be consulted"); });

        Assert.False(consulted);
    }

    // ---- native-reduction predicate (TryGetNativeReducedSize) ----

    private sealed class FakeFrame(Func<(uint W, uint H)> closest) : IWICBitmapFrameDecode, IWICBitmapSourceTransform
    {
        public void GetSize(out uint puiWidth, out uint puiHeight) => throw new NotImplementedException();
        public void GetPixelFormat(out Guid pPixelFormat) => throw new NotImplementedException();
        public void GetResolution(out double pDpiX, out double pDpiY) => throw new NotImplementedException();
        public void CopyPalette(IntPtr pIPalette) => throw new NotImplementedException();
        public void CopyPixels(IntPtr prc, uint cbStride, uint cbBufferSize, IntPtr pbBuffer) => throw new NotImplementedException();
        public void GetMetadataQueryReader(out nint ppIMetadataQueryReader) => throw new NotImplementedException();
        public void GetColorContexts(uint cCount, nint ppIColorContexts, out uint pcActualCount) => throw new NotImplementedException();
        public void GetThumbnail(out nint ppIThumbnail) => throw new NotImplementedException();

        public void CopyPixels(IntPtr prcDst, uint uiWidth, uint uiHeight, ref Guid pguidDstFormat, uint dstTransform, uint nStride, uint cbBufferSize, IntPtr pbBuffer) => throw new NotImplementedException();
        public void GetClosestSize(ref uint puiWidth, ref uint puiHeight) => (puiWidth, puiHeight) = closest();
        public void GetClosestPixelFormat(ref Guid pguidDstFormat) => throw new NotImplementedException();
        public void DoesSupportTransform(uint dstTransform, out int pfIsSupported) => throw new NotImplementedException();
    }

    private sealed class FrameWithoutTransform : IWICBitmapFrameDecode
    {
        public void GetSize(out uint puiWidth, out uint puiHeight) => throw new NotImplementedException();
        public void GetPixelFormat(out Guid pPixelFormat) => throw new NotImplementedException();
        public void GetResolution(out double pDpiX, out double pDpiY) => throw new NotImplementedException();
        public void CopyPalette(IntPtr pIPalette) => throw new NotImplementedException();
        public void CopyPixels(IntPtr prc, uint cbStride, uint cbBufferSize, IntPtr pbBuffer) => throw new NotImplementedException();
        public void GetMetadataQueryReader(out nint ppIMetadataQueryReader) => throw new NotImplementedException();
        public void GetColorContexts(uint cCount, nint ppIColorContexts, out uint pcActualCount) => throw new NotImplementedException();
        public void GetThumbnail(out nint ppIThumbnail) => throw new NotImplementedException();
    }

    private static (bool Useful, uint W, uint H) NativeReduction(object frame, uint targetW, uint targetH, uint origW, uint origH)
    {
        var args = new object?[] { frame, targetW, targetH, origW, origH, 0u, 0u };
        var useful = (bool)Private("TryGetNativeReducedSize").Invoke(null, args)!;
        return (useful, (uint)args[5]!, (uint)args[6]!);
    }

    [Theory]
    // target 100x80 of an original 400x320; the codec offers (closestW, closestH).
    [InlineData(200u, 160u, true)]   // reduced, above the target on both axes
    [InlineData(100u, 80u, true)]    // exactly the target: still a reduction
    [InlineData(100u, 160u, true)]   // exactly the target width
    [InlineData(200u, 80u, true)]    // exactly the target height
    [InlineData(400u, 160u, true)]   // reduced on the height only
    [InlineData(200u, 320u, true)]   // reduced on the width only
    [InlineData(400u, 320u, false)]  // the codec cannot reduce: nothing to gain
    [InlineData(99u, 160u, false)]   // undershoots the target width
    [InlineData(200u, 79u, false)]   // undershoots the target height
    [InlineData(99u, 79u, false)]    // undershoots both
    [InlineData(500u, 400u, false)]  // larger than the original: not a reduction
    [InlineData(401u, 160u, true)]   // wider than the original on one axis but reduced on the other
    [InlineData(200u, 321u, true)]   // taller than the original on one axis but reduced on the other
    public void TryGetNativeReducedSize_CodecOffer_IsUsefulOnlyWhenItReducesWithoutUndershooting(uint closestW, uint closestH, bool expected)
    {
        var frame = new FakeFrame(() => (closestW, closestH));

        var (useful, nativeW, nativeH) = NativeReduction(frame, 100, 80, 400, 320);

        Assert.Equal(expected, useful);
        Assert.Equal((closestW, closestH), (nativeW, nativeH));
    }

    [Fact]
    public void TryGetNativeReducedSize_FrameWithoutTransformInterface_IsNotUseful()
    {
        var (useful, nativeW, nativeH) = NativeReduction(new FrameWithoutTransform(), 100, 80, 400, 320);

        Assert.False(useful);
        Assert.Equal((100u, 80u), (nativeW, nativeH));
    }

    [Fact]
    public void TryGetNativeReducedSize_CodecThrowsComException_IsNotUseful()
    {
#pragma warning disable CA2201 // COMException simulates the WIC codec failing GetClosestSize
        var frame = new FakeFrame(() => throw new COMException("codec refuses", unchecked((int)0x88982F50)));
#pragma warning restore CA2201

        var (useful, _, _) = NativeReduction(frame, 100, 80, 400, 320);

        Assert.False(useful);
    }

    // ---- colour-context choice (SelectSourceColorContext) ----

    private sealed class FakeContext(WICColorContextType type, uint exifColorSpace = 0) : IWICColorContext
    {
        public void InitializeFromFilename(string wzFilename) => throw new NotImplementedException();
        public void InitializeFromMemory(IntPtr pbBuffer, uint cbBufferSize) => throw new NotImplementedException();
        public void InitializeFromExifColorSpace(uint value) => throw new NotImplementedException();
        public void GetContextType(out WICColorContextType pType) => pType = type;
        public void GetProfileBytes(uint cbBuffer, IntPtr pbBuffer, out uint pcbActual) => throw new NotImplementedException();
        public void GetExifColorSpace(out uint pValue) => pValue = exifColorSpace;
    }

    private static object? Select(params IWICColorContext?[]? contexts) =>
        Private("SelectSourceColorContext").Invoke(null, [contexts is null ? null : contexts.ToArray()]);

    [Fact]
    public void SelectSourceColorContext_NoContexts_IsNull()
    {
        Assert.Null(Select(null));
        Assert.Null(Select());
    }

    [Fact]
    public void SelectSourceColorContext_SrgbExifColorSpaceOnly_NeedsNoTransform()
    {
        Assert.Null(Select(new FakeContext(WICColorContextType.ExifColorSpace, 1)));
    }

    [Fact]
    public void SelectSourceColorContext_NonSrgbExifColorSpace_IsChosen()
    {
        var adobe = new FakeContext(WICColorContextType.ExifColorSpace, 2);

        Assert.Same(adobe, Select(adobe));
    }

    [Fact]
    public void SelectSourceColorContext_UninitializedContext_IsIgnored()
    {
        Assert.Null(Select(new FakeContext(WICColorContextType.Uninitialized, 2)));
    }

    [Fact]
    public void SelectSourceColorContext_EmbeddedProfile_BeatsAnyExifColorSpace()
    {
        var exif = new FakeContext(WICColorContextType.ExifColorSpace, 2);
        var profile = new FakeContext(WICColorContextType.Profile);

        Assert.Same(profile, Select(exif, profile));
        Assert.Same(profile, Select(profile, exif));
    }

    [Fact]
    public void SelectSourceColorContext_FirstNonSrgbExifColorSpaceWins_AndAnSrgbOneDoesNotBlockALaterOne()
    {
        var srgb = new FakeContext(WICColorContextType.ExifColorSpace, 1);
        var first = new FakeContext(WICColorContextType.ExifColorSpace, 2);
        var second = new FakeContext(WICColorContextType.ExifColorSpace, 3);

        Assert.Same(first, Select(first, second));
        Assert.Same(second, Select(srgb, second));
    }

    [Fact]
    public void SelectSourceColorContext_NullEntries_AreSkipped()
    {
        var adobe = new FakeContext(WICColorContextType.ExifColorSpace, 2);

        Assert.Same(adobe, Select(null, adobe));
    }

    // ---- PROPVARIANT shapes (ReadVariant) ----

    private static object? ReadVariant(Action<IntPtr> fill)
    {
        var pvar = Marshal.AllocHGlobal(24);
        try
        {
            for (var i = 0; i < 24; i += 8) Marshal.WriteInt64(pvar, i, 0);
            fill(pvar);
            return ExifReaderMethod("ReadVariant").Invoke(null, [pvar]);
        }
        finally
        {
            Marshal.FreeHGlobal(pvar);
        }
    }

    [Fact]
    public void ReadVariant_WideString_IsReadUnlessThePointerIsNull()
    {
        var text = Marshal.StringToHGlobalUni("Canon");
        try
        {
            Assert.Equal("Canon", ReadVariant(p => { Marshal.WriteInt16(p, 31); Marshal.WriteIntPtr(p, 8, text); }));
        }
        finally
        {
            Marshal.FreeHGlobal(text);
        }

        Assert.Null(ReadVariant(p => Marshal.WriteInt16(p, 31)));
    }

    [Fact]
    public void ReadVariant_AnsiString_IsReadUnlessThePointerIsNull()
    {
        var text = Marshal.StringToCoTaskMemUTF8("Nikon");
        try
        {
            Assert.Equal("Nikon", ReadVariant(p => { Marshal.WriteInt16(p, 30); Marshal.WriteIntPtr(p, 8, text); }));
        }
        finally
        {
            Marshal.FreeCoTaskMem(text);
        }

        Assert.Null(ReadVariant(p => Marshal.WriteInt16(p, 30)));
    }

    [Fact]
    public void ReadVariant_ScalarShapes_MatchWhatWpfReturns()
    {
        Assert.Equal((short)-5, ReadVariant(p => { Marshal.WriteInt16(p, 2); Marshal.WriteInt16(p, 8, -5); }));
        Assert.Equal(70000, ReadVariant(p => { Marshal.WriteInt16(p, 3); Marshal.WriteInt32(p, 8, 70000); }));
        Assert.Equal((byte)200, ReadVariant(p => { Marshal.WriteInt16(p, 17); Marshal.WriteByte(p, 8, 200); }));
        Assert.Equal((ushort)6, ReadVariant(p => { Marshal.WriteInt16(p, 18); Marshal.WriteInt16(p, 8, 6); }));
        Assert.Equal(4000000000u, ReadVariant(p => { Marshal.WriteInt16(p, 19); Marshal.WriteInt32(p, 8, unchecked((int)4000000000u)); }));
        Assert.Equal(-9L, ReadVariant(p => { Marshal.WriteInt16(p, 20); Marshal.WriteInt64(p, 8, -9); }));
        Assert.Equal(0x0000000200000001UL, ReadVariant(p => { Marshal.WriteInt16(p, 21); Marshal.WriteInt64(p, 8, 0x0000000200000001L); }));
        Assert.Null(ReadVariant(_ => { }));
    }

    [Fact]
    public void ReadVariant_Vector_ReturnsTheFirstElementOnly()
    {
        var elements = Marshal.AllocHGlobal(8);
        try
        {
            Marshal.WriteInt16(elements, 0, 6);
            Marshal.WriteInt16(elements, 2, 9);
            var value = ReadVariant(p =>
            {
                Marshal.WriteInt16(p, (short)(0x1000 | 18));
                Marshal.WriteInt32(p, 8, 2);
                Marshal.WriteIntPtr(p, 8 + IntPtr.Size, elements);
            });

            Assert.Equal(new ushort[] { 6 }, Assert.IsType<ushort[]>(value));
        }
        finally
        {
            Marshal.FreeHGlobal(elements);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ReadVariant_VectorWithoutElements_IsNullEvenWithAPointer(int count)
    {
        var elements = Marshal.AllocHGlobal(8);
        try
        {
            Marshal.WriteInt64(elements, 0x0009000900090009L);
            var value = ReadVariant(p =>
            {
                Marshal.WriteInt16(p, (short)(0x1000 | 18));
                Marshal.WriteInt32(p, 8, count);
                Marshal.WriteIntPtr(p, 8 + IntPtr.Size, elements);
            });

            Assert.Null(value);
        }
        finally
        {
            Marshal.FreeHGlobal(elements);
        }
    }

    [Fact]
    public void ReadVariant_VectorWithCountButNoPointer_IsNull()
    {
        var value = ReadVariant(p =>
        {
            Marshal.WriteInt16(p, (short)(0x1000 | 18));
            Marshal.WriteInt32(p, 8, 3);
        });

        Assert.Null(value);
    }

    // ---- orientation tag range (ReadMetadataValues) ----

    private const string ExifOrientationQuery = "/app1/ifd/{ushort=274}";
    private const string WindowsOrientationQuery = "System.Photo.Orientation";

    private static int Orientation(object? exif, object? windows = null, bool read = true)
    {
        object? Query(string name) => name switch
        {
            ExifOrientationQuery => exif,
            WindowsOrientationQuery => windows,
            _ => null,
        };

        return WicExifReader.ReadMetadataValues(Query, read, exifIfdRoot: null, out _);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(7, 7)]
    [InlineData(8, 8)]
    [InlineData(9, 1)]
    [InlineData(-1, 1)]
    [InlineData(65535, 1)]
    public void ReadMetadataValues_ExifOrientationTag_IsAcceptedOnlyInOneToEight(int tag, int expected)
    {
        Assert.Equal(expected, Orientation(tag is >= 0 and <= 65535 ? (ushort)tag : tag));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(6, 6)]
    [InlineData(8, 8)]
    [InlineData(9, 1)]
    public void ReadMetadataValues_WindowsFallback_IsAcceptedOnlyInOneToEightWhenTheExifTagIsUnusable(int tag, int expected)
    {
        Assert.Equal(expected, Orientation(exif: null, windows: (ushort)tag));
        Assert.Equal(expected, Orientation(exif: (ushort)0, windows: (ushort)tag));
        Assert.Equal(expected, Orientation(exif: (ushort)9, windows: (ushort)tag));
    }

    [Fact]
    public void ReadMetadataValues_UsableExifTag_WinsOverTheWindowsProperty()
    {
        Assert.Equal(3, Orientation(exif: (ushort)3, windows: (ushort)6));
    }

    [Theory]
    [InlineData(1, 6, 1)]   // an explicit "normal" is a usable tag: the Windows property is not consulted
    [InlineData(8, 3, 8)]   // the upper bound is usable too
    [InlineData(2, 6, 2)]
    public void ReadMetadataValues_ExifTagInRange_NeverFallsThroughToTheWindowsProperty(int exif, int windows, int expected)
    {
        Assert.Equal(expected, Orientation(exif: (ushort)exif, windows: (ushort)windows));
    }

    [Fact]
    public void ReadMetadataValues_OrientationNotRequested_IsNeverQueried()
    {
        var orientation = WicExifReader.ReadMetadataValues(
            _ => throw new InvalidOperationException("must not be queried"), readOrientation: false, exifIfdRoot: null, out var exif);

        Assert.Equal(1, orientation);
        Assert.Null(exif);
    }

    // ---- EXIF container choice and colour space through real decodes ----

    private string TiffWithExif(string make)
    {
        var path = _root.Combine("exif-" + Guid.NewGuid().ToString("N") + ".tif");
        var metadata = new BitmapMetadata("tiff");
        metadata.SetQuery("/ifd/{ushort=271}", make);
        var encoder = new TiffBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(FixtureGenerator.CreateGradientCheckerboard(32, 24), null, metadata, null));
        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }

    private string JpegWithExif(string make)
    {
        var path = _root.Combine("exif-" + Guid.NewGuid().ToString("N") + ".jpg");
        var metadata = new BitmapMetadata("jpg");
        metadata.SetQuery("/app1/ifd/{ushort=271}", make);
        FixtureGenerator.SaveJpeg(FixtureGenerator.CreateGradientCheckerboard(32, 24), path, metadata: metadata);
        return path;
    }

    [Fact]
    public void Decode_TiffWithExifMake_ReadsItThroughTheTiffIfdRoot()
    {
        var decoded = new WicDirectDecoder(WpfBitmapSourceCodec.Instance).Decode(new DecodeRequest(TiffWithExif("TiffMaker"), 0));

        Assert.Equal("TiffMaker", decoded.Exif?.CameraMake);
    }

    [Fact]
    public void Decode_JpegWithExifMake_ReadsItThroughTheJpegIfdRoot()
    {
        var decoded = new WicDirectDecoder(WpfBitmapSourceCodec.Instance).Decode(new DecodeRequest(JpegWithExif("JpegMaker"), 0));

        Assert.Equal("JpegMaker", decoded.Exif?.CameraMake);
    }

    [Fact]
    public void Decode_PngWithoutExifContainer_HasNoExif()
    {
        var path = FixtureGenerator.GeneratePng(_root.Combine("plain.png"), 32, 24);

        var decoded = new WicDirectDecoder(WpfBitmapSourceCodec.Instance).Decode(new DecodeRequest(path, 0));

        Assert.Null(decoded.Exif);
    }

    private string OrangeJpegWithExifColorSpace(ushort? colorSpace)
    {
        var path = _root.Combine("cs-" + (colorSpace?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none") + ".jpg");
        var pixels = new byte[32 * 24 * 4];
        for (var i = 0; i < pixels.Length; i += 4) { pixels[i] = 20; pixels[i + 1] = 120; pixels[i + 2] = 230; pixels[i + 3] = 255; }
        var bitmap = BitmapSource.Create(32, 24, 96, 96, PixelFormats.Bgra32, null, pixels, 32 * 4);
        BitmapMetadata? metadata = null;
        if (colorSpace.HasValue)
        {
            metadata = new BitmapMetadata("jpg");
            metadata.SetQuery("/app1/ifd/exif/{ushort=40961}", colorSpace.Value);
        }

        FixtureGenerator.SaveJpeg(bitmap, path, quality: 100, metadata: metadata);
        return path;
    }

    private static (byte B, byte G, byte R) CenterPixel(IDecodedImage image)
    {
        var bitmap = Assert.IsAssignableFrom<BitmapSource>(image.PlatformImage);
        var px = new byte[4];
        bitmap.CopyPixels(new System.Windows.Int32Rect(16, 12, 1, 1), px, 4, 0);
        return (px[0], px[1], px[2]);
    }

    [Fact]
    public void Decode_ExifAdobeRgbColorSpace_IsTransformedToSrgb_WhileSrgbAndUntaggedAreNot()
    {
        var decoder = new WicDirectDecoder(WpfBitmapSourceCodec.Instance);

        var untagged = CenterPixel(decoder.Decode(new DecodeRequest(OrangeJpegWithExifColorSpace(null), 0)));
        var srgb = CenterPixel(decoder.Decode(new DecodeRequest(OrangeJpegWithExifColorSpace(1), 0)));
        var adobe = CenterPixel(decoder.Decode(new DecodeRequest(OrangeJpegWithExifColorSpace(2), 0)));

        Assert.Equal(untagged, srgb);
        Assert.NotEqual(untagged, adobe);
    }
}
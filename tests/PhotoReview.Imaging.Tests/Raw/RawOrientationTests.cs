using System.IO;
using System.Windows.Media.Imaging;
using PhotoReview.Core.Abstractions;
using PhotoReview.Imaging.Decoding;
using PhotoReview.Imaging.Raw;
using PhotoReview.Imaging.Tests.Fixtures;
using PhotoReview.TestSupport;
using Xunit;

namespace PhotoReview.Imaging.Tests.Raw;

/// <summary>
/// RAF and RW2 keep their orientation in EXIF (RAF: only inside the embedded JPEG; RW2: IFD0 0x0112 or the embedded
/// JPEG). The container readers must report it, otherwise a portrait shot is decoded and measured sideways.
/// </summary>
public sealed class RawOrientationTests
{
    private const int PreviewWidth = 640;
    private const int PreviewHeight = 480;

    private static readonly string CorpusDir = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "../../../../../tests/Fixtures/raw-corpus"));

    private static byte[] OrientedJpeg(ushort orientation)
    {
        using var temp = new TempRoot("raw-orientation");
        var path = FixtureGenerator.GenerateJpegWithOrientation(temp.Combine("preview.jpg"), PreviewWidth, PreviewHeight, orientation);
        return File.ReadAllBytes(path);
    }

    private static RawContainerInfo ReadContainer(byte[] file, string extension)
    {
        var registry = new RawContainerReaderRegistry();
        var reader = registry.FindReader(file.AsSpan(0, Math.Min(64, file.Length)), extension);
        Assert.NotNull(reader);
        return reader.Read(new InMemoryRawHeaderSource(file), CancellationToken.None);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(6)]
    [InlineData(8)]
    public void RafReader_EmbeddedJpegExifOrientation_IsReported(ushort orientation)
    {
        var raf = SyntheticRawBuilder.BuildRaf(OrientedJpeg(orientation));

        Assert.Equal(orientation, ReadContainer(raf, ".raf").Orientation);
    }

    [Fact]
    public void RafReader_JpegWithoutExif_DefaultsToNormalOrientation()
    {
        var raf = SyntheticRawBuilder.BuildRaf(SyntheticRawBuilder.CreateMinimalJpeg(640, 480));

        Assert.Equal(1, ReadContainer(raf, ".raf").Orientation);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(6)]
    [InlineData(8)]
    public void Rw2Reader_Ifd0Orientation_IsReported(ushort orientation)
    {
        var rw2 = SyntheticRawBuilder.BuildRw2(SyntheticRawBuilder.CreateMinimalJpeg(640, 480), ifd0Orientation: orientation);

        Assert.Equal(orientation, ReadContainer(rw2, ".rw2").Orientation);
    }

    [Theory]
    [InlineData(6)]
    [InlineData(8)]
    public void Rw2Reader_NoIfd0Orientation_FallsBackToEmbeddedJpegExif(ushort orientation)
    {
        var rw2 = SyntheticRawBuilder.BuildRw2(OrientedJpeg(orientation));

        Assert.Equal(orientation, ReadContainer(rw2, ".rw2").Orientation);
    }

    [Fact]
    public void Rw2Reader_Ifd0OrientationWinsOverEmbeddedJpegExif()
    {
        var rw2 = SyntheticRawBuilder.BuildRw2(OrientedJpeg(8), ifd0Orientation: 6);

        Assert.Equal(6, ReadContainer(rw2, ".rw2").Orientation);
    }

    [Theory]
    [InlineData(".raf", 6, true)]
    [InlineData(".raf", 8, true)]
    [InlineData(".raf", 1, false)]
    [InlineData(".rw2", 6, true)]
    [InlineData(".rw2", 8, true)]
    [InlineData(".rw2", 1, false)]
    public void RawDecoder_PortraitRafAndRw2_ReadInfoAndDecodeAgreeOnOrientation(string extension, ushort orientation, bool expectTransposed)
    {
        var jpeg = OrientedJpeg(orientation);
        var file = extension == ".raf" ? SyntheticRawBuilder.BuildRaf(jpeg) : SyntheticRawBuilder.BuildRw2(jpeg);
        using var temp = new TempRoot("raw-orientation-decode");
        var path = temp.File("portrait" + extension, file);
        var decoder = new RawDecoder(new WpfBitmapImageDecoder());

        var info = decoder.ReadInfo(path);
        var decoded = decoder.Decode(new DecodeRequest(path, DecodeBox.Unbounded));

        var expectedWidth = expectTransposed ? PreviewHeight : PreviewWidth;
        var expectedHeight = expectTransposed ? PreviewWidth : PreviewHeight;
        Assert.Equal(orientation, info.Orientation);
        Assert.Equal(expectedWidth, info.Width);
        Assert.Equal(expectedHeight, info.Height);
        Assert.Equal(orientation, decoded.Orientation);
        Assert.Equal(expectedWidth, decoded.PixelWidth);
        Assert.Equal(expectedHeight, decoded.PixelHeight);
        Assert.Equal(expectedWidth, decoded.OriginalWidth);
        Assert.Equal(expectedHeight, decoded.OriginalHeight);
    }

    public static TheoryData<string> CorpusFiles()
    {
        var data = new TheoryData<string>();
        if (!Directory.Exists(CorpusDir)) return data;
        foreach (var file in Directory.GetFiles(CorpusDir).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            if (PhotoReview.Core.Catalog.ImageFileTypes.RawExtensions.Contains(Path.GetExtension(file)))
                data.Add(Path.GetFileName(file));
        }
        return data;
    }

    [Trait("Category", "Native")]
    [Theory]
    [MemberData(nameof(CorpusFiles))]
    public void RawDecoder_CorpusFile_DecodedDimensionsHonorReadInfoOrientation(string fileName)
    {
        var path = Path.Combine(CorpusDir, fileName);
        var decoder = new RawDecoder(new WpfBitmapImageDecoder());
        var info = decoder.ReadInfo(path);

        IDecodedImage decoded;
        try
        {
            decoded = decoder.Decode(new DecodeRequest(path, DecodeBox.Unbounded));
        }
        catch (NotSupportedException)
        {
            return; // preview codec unsupported by the inner WPF decoder (needs the LibRaw thumbnail fallback)
        }
        catch (InvalidDataException ex) when (ex.Message.Contains("No embedded preview", StringComparison.Ordinal))
        {
            return; // RAW-only DNG (no JPEG preview): decoded by the full-RAW path, covered by LibRaw tests
        }

        Assert.Equal(info.Orientation, decoded.Orientation);
        // The embedded preview may be a differently cropped/scaled view of the sensor, but its landscape/portrait
        // shape must match the orientation-adjusted shape ReadInfo reports.
        if (info.Width != info.Height && decoded.PixelWidth != decoded.PixelHeight)
            Assert.Equal(info.Width > info.Height, decoded.PixelWidth > decoded.PixelHeight);
    }

    [Trait("Category", "Native")]
    [Theory]
    [MemberData(nameof(CorpusFiles))]
    public void RafAndRw2CorpusFile_ContainerOrientationMatchesEmbeddedJpegExif(string fileName)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (extension is not (".raf" or ".rw2")) return;

        var bytes = File.ReadAllBytes(Path.Combine(CorpusDir, fileName));
        var info = ReadContainer(bytes, extension);
        var preview = Assert.Single(info.Previews);
        using var stream = new MemoryStream(bytes, checked((int)preview.Offset), checked((int)preview.Length), writable: false);
        BitmapFrame frame;
        try
        {
            frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or InvalidOperationException)
        {
            return; // WIC cannot open this camera's preview JPEG, so there is no independent oracle for it
        }

        Assert.Equal(ExifOrientation.Read(frame.Metadata as BitmapMetadata), info.Orientation);
    }

    /// <summary>
    /// The corpus holds only landscape shots (orientation 1). Rewriting the real orientation tag of every corpus file to 6
    /// (portrait) turns each real container into a portrait sample: every reader must then report 6, ReadInfo must swap
    /// the visual size and the decoded preview must come out transposed relative to the landscape original.
    /// </summary>
    [Trait("Category", "Native")]
    [Theory]
    [MemberData(nameof(CorpusFiles))]
    public void RawDecoder_CorpusFileRewrittenToPortrait_ReadInfoAndDecodeAreTransposed(string fileName)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        var original = File.ReadAllBytes(Path.Combine(CorpusDir, fileName));
        var originalInfo = ReadContainer(original, extension);
        if (originalInfo.Previews.Count == 0) return; // RAW-only DNG: no embedded preview to orient

        var portrait = (byte[])original.Clone();
        if (extension == ".cr3") return; // orientation lives in an ISO-BMFF CMT1 box, which this byte patcher does not walk
        Assert.True(TryWriteOrientation(portrait, extension, originalInfo, 6), $"{fileName} has no orientation tag to rewrite");

        Assert.Equal(1, originalInfo.Orientation);
        Assert.Equal(6, ReadContainer(portrait, extension).Orientation);

        var landscapeDecoder = new RawDecoder(new WpfBitmapImageDecoder(new InMemorySourceReader(original)), new InMemorySourceReader(original));
        var portraitDecoder = new RawDecoder(new WpfBitmapImageDecoder(new InMemorySourceReader(portrait)), new InMemorySourceReader(portrait));
        var landscapeInfo = landscapeDecoder.ReadInfo("landscape" + extension);
        var portraitInfo = portraitDecoder.ReadInfo("portrait" + extension);
        Assert.Equal(6, portraitInfo.Orientation);
        Assert.Equal(landscapeInfo.Height, portraitInfo.Width);
        Assert.Equal(landscapeInfo.Width, portraitInfo.Height);

        IDecodedImage landscapeImage;
        IDecodedImage portraitImage;
        try
        {
            landscapeImage = landscapeDecoder.Decode(new DecodeRequest("landscape" + extension, DecodeBox.Unbounded));
            portraitImage = portraitDecoder.Decode(new DecodeRequest("portrait" + extension, DecodeBox.Unbounded));
        }
        catch (NotSupportedException)
        {
            return; // preview codec unsupported by the inner WPF decoder (needs the LibRaw thumbnail fallback)
        }

        Assert.Equal(6, portraitImage.Orientation);
        Assert.Equal(landscapeImage.PixelHeight, portraitImage.PixelWidth);
        Assert.Equal(landscapeImage.PixelWidth, portraitImage.PixelHeight);
    }

    private sealed class InMemorySourceReader(byte[] bytes) : ISourceReader
    {
        public Stream OpenSource(string path, SourceReadPriority priority, int bufferSize = 1024 * 1024) =>
            new MemoryStream(bytes, writable: false);
    }

    /// <summary>Rewrites the Orientation tag in place: IFD0 of a TIFF-family RAW, else the embedded JPEG's EXIF (RAF, RW2 without IFD0 tag).</summary>
    private static bool TryWriteOrientation(byte[] file, string extension, RawContainerInfo info, ushort orientation)
    {
        if (extension != ".raf" && TryWriteTiffOrientation(file, 0, orientation)) return true;
        if (info.Previews.Count == 0) return false;

        var jpegStart = checked((int)info.Previews[0].Offset);
        var offset = jpegStart + 2;
        while (offset + 4 <= file.Length && file[offset] == 0xFF)
        {
            var marker = file[offset + 1];
            var length = (file[offset + 2] << 8) | file[offset + 3];
            if (marker == 0xE1 && length >= 8 && file.AsSpan(offset + 4, 6).SequenceEqual("Exif\0\0"u8))
                return TryWriteTiffOrientation(file, offset + 10, orientation);
            if (marker is 0xDA or 0xD9) break;
            offset += 2 + length;
        }
        return false;
    }

    private static bool TryWriteTiffOrientation(byte[] file, int tiffStart, ushort orientation)
    {
        if (tiffStart + 8 > file.Length) return false;
        var little = file[tiffStart] == (byte)'I';
        ushort ReadU16(int at) => little ? (ushort)(file[at] | (file[at + 1] << 8)) : (ushort)((file[at] << 8) | file[at + 1]);
        uint ReadU32(int at) => little
            ? (uint)(file[at] | (file[at + 1] << 8) | (file[at + 2] << 16) | (file[at + 3] << 24))
            : (uint)((file[at] << 24) | (file[at + 1] << 16) | (file[at + 2] << 8) | file[at + 3]);

        var ifd0 = checked(tiffStart + (int)ReadU32(tiffStart + 4));
        if (ifd0 + 2 > file.Length) return false;
        int count = ReadU16(ifd0);
        for (var i = 0; i < count && ifd0 + 2 + ((i + 1) * 12) <= file.Length; i++)
        {
            var entry = ifd0 + 2 + (i * 12);
            if (ReadU16(entry) != 0x0112 || ReadU16(entry + 2) != 3) continue;
            file[entry + 8] = little ? (byte)orientation : (byte)0;
            file[entry + 9] = little ? (byte)0 : (byte)orientation;
            return true;
        }
        return false;
    }
}

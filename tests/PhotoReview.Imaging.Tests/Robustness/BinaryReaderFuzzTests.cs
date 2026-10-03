using System.IO;
using PhotoReview.Imaging.Metadata;
using PhotoReview.Imaging.Raw;
using PhotoReview.Imaging.Raw.Bmff;
using PhotoReview.Imaging.Raw.Raf;
using PhotoReview.Imaging.Raw.Tiff;
using PhotoReview.Imaging.Tests.Metadata;
using PhotoReview.Imaging.Tests.Raw;

namespace PhotoReview.Imaging.Tests.Robustness;

/// <summary>
/// Deterministic, bounded fuzz of every binary reader that takes untrusted input (EXIF/TIFF, BMFF boxes, the RAW container
/// readers, the JPEG marker walkers and TurboJpeg ReadInfo), through the shared <see cref="BinaryFuzz"/> corpus: truncation at every
/// offset, length-field overwrites with 0 / 0x7FFFFFFF / 0xFFFFFFFF, seeded bit flips and splices. Asserted: no hang, and any
/// failure is the reader's documented one (InvalidDataException, or its own "not parsed" result) -- never an
/// IndexOutOfRange / ArgumentOutOfRange / NullReference / Overflow / OutOfMemory. Each test runs in about a second.
/// </summary>
[Trait("Category", "HotPath")]
public sealed class BinaryReaderFuzzTests : IDisposable
{
    private readonly TempRoot _root = new("BinaryFuzz");

    public void Dispose() => _root.Dispose();

    private static bool Invalid(Exception ex) => ex is InvalidDataException;

    private static byte[] Jpeg() => SyntheticRawBuilder.CreateMinimalJpeg(64, 48);

    private static void AssertReached(BinaryFuzz.Stats stats, string target)
    {
        Assert.True(stats.Accepted > 0, $"{target}: no mutant was accepted -- the corpus no longer reaches the parser.");
        Assert.True(stats.Total > 500, $"{target}: only {stats.Total} cases were generated.");
    }

    // ------------------------------------------------------------------ EXIF / TIFF structure

    [Fact(DisplayName = "Fuzz ExifParser (JPEG APP1 + bare TIFF block) and the TiffStructure value readers: never throw, never hang")]
    public void Fuzz_ExifParserAndTiffStructure_NeverThrow()
    {
        foreach (var little in new[] { true, false })
        {
            var (ifd0, exif) = ExifTestData.FullCamera(little);
            var tiff = ExifTestData.Tiff(little, ifd0, exif);

            var stats = BinaryFuzz.Run($"ExifParser/TiffStructure (little={little})",
                BinaryFuzz.Mutants(tiff, 4100 + (little ? 1 : 0)),
                bytes =>
                {
                    var parsed = ExifParser.TryParseTiffBlock(bytes) is not null;
                    _ = ExifParser.TryReadOrientation(bytes);
                    _ = ExifParser.TryParseTiffBlock(bytes, ifdIsExif: true);
                    WalkEveryEntry(bytes);
                    // The same bytes behind a JPEG APP1 header (the production entry point).
                    var jpeg = ExifTestData.JpegWithApp1(bytes);
                    parsed |= ExifParser.TryParseJpeg(jpeg) is not null;
                    _ = ExifParser.TryReadOrientationFromJpeg(jpeg);
                    return parsed;
                },
                static _ => false);
            AssertReached(stats, "ExifParser");
        }
    }

    /// <summary>Drives every TiffStructure reader over each entry of IFD0 the way the parsers do, whatever the (hostile) counts claim.</summary>
    private static void WalkEveryEntry(byte[] tiff)
    {
        if (!TiffStructure.TryReadHeader(tiff, out var little, out _, out var ifd0)) return;
        if (ifd0 > int.MaxValue) return;
        var count = TiffStructure.ReadU16(tiff, (int)ifd0, little);
        for (var i = 0; i < Math.Min((int)count, 256); i++)
        {
            var entry = (int)ifd0 + 2 + (12 * i);
            var type = TiffStructure.ReadU16(tiff, entry + 2, little);
            var n = TiffStructure.ReadU32(tiff, entry + 4, little);
            if (!TiffStructure.TryGetValueSpan(tiff, entry, type, n, little, out var value)) continue;
            _ = TiffStructure.ReadAscii(value, type);
            _ = TiffStructure.ReadUnsigned(value, type, little);
            _ = TiffStructure.ReadRational(value, type, little);
        }
    }

    [Fact(DisplayName = "Fuzz TiffHeaderNavigator (IFD walk, tag readers, EXIF block, strips, crop, JPEG interchange): only InvalidDataException")]
    public void Fuzz_TiffHeaderNavigator_FailsOnlyCleanly()
    {
        foreach (var little in new[] { true, false })
        {
            var seed = SyntheticRawBuilder.BuildTiff(little, Jpeg(), orientation: 6);
            var stats = BinaryFuzz.Run($"TiffHeaderNavigator (little={little})",
                BinaryFuzz.Mutants(seed, 4200 + (little ? 1 : 0)),
                bytes => ExerciseTiffNavigator(bytes),
                Invalid);
            AssertReached(stats, "TiffHeaderNavigator");
        }
    }

    private static bool ExerciseTiffNavigator(byte[] bytes)
    {
        var source = new InMemoryRawHeaderSource(bytes);
        if (!TiffStructure.TryReadHeader(bytes, out var little, out _, out var ifd0)) return false;
        var entries = TiffHeaderNavigator.ReadIfdEntries(source, ifd0, little, out var next);
        foreach (var entry in entries.Take(64))
        {
            _ = TiffHeaderNavigator.ReadTagUnsigned(source, entry, little);
            _ = TiffHeaderNavigator.ReadTagUnsignedArray(source, entry, little);
        }

        if (next != 0) _ = TiffHeaderNavigator.ReadIfdEntries(source, next, little, out _);
        _ = TiffHeaderNavigator.ComputeExifBlock(source, little, ifd0);
        _ = TiffHeaderNavigator.TryReadSingleStrip(source, entries, little, out _, out _);
        _ = TiffHeaderNavigator.TryReadDefaultCropSize(source, entries, little, out _, out _);
        _ = TiffHeaderNavigator.HasSquareDefaultScale(source, entries, little);
        TiffHeaderNavigator.ReadImageSize(source, entries, little, out _, out _);
        _ = TiffHeaderNavigator.TryReadExifPixelDimensions(source, ifd0, little, out _, out _);
        if (TiffHeaderNavigator.TryReadJpegInterchange(source, entries, little, out var offset, out var length))
        {
            int w = 64, h = 48;
            TiffHeaderNavigator.ReconcileJpegSize(source, offset, length, ref w, ref h);
        }

        return entries.Count > 0;
    }

    // ------------------------------------------------------------------ BMFF (CR3 / HEIF)

    [Fact(DisplayName = "Fuzz BmffBoxNavigator (TryReadBox + bounded child-tree walk) on CR3 and plain ISO-BMFF: only InvalidDataException")]
    public void Fuzz_BmffBoxNavigator_FailsOnlyCleanly()
    {
        var seeds = new[] { SyntheticRawBuilder.BuildCanonCr3(), SyntheticRawBuilder.BuildIsoBmff() };
        for (var s = 0; s < seeds.Length; s++)
        {
            var stats = BinaryFuzz.Run($"BmffBoxNavigator (seed {s})",
                BinaryFuzz.Mutants(seeds[s], 4300 + s),
                bytes =>
                {
                    var source = new InMemoryRawHeaderSource(bytes);
                    var visited = 0;
                    var found = WalkBoxes(source, 0, source.Length, depth: 0, ref visited);
                    Assert.True(visited <= 4_096 * 8, $"walk visited {visited} boxes");
                    return found;
                },
                Invalid);
            AssertReached(stats, "BmffBoxNavigator");
        }
    }

    private static bool WalkBoxes(InMemoryRawHeaderSource source, long offset, long end, int depth, ref int visited)
    {
        var any = false;
        for (var i = 0; i < BmffBoxNavigator.MaxChildBoxes && offset < end; i++)
        {
            if (!BmffBoxNavigator.TryReadBox(source, offset, out var box, end)) break;
            any = true;
            // A parsed box must stay inside the container; the walk must make forward progress or it would never end.
            Assert.True(box.Offset == offset && box.TotalSize >= 8 && box.Offset + box.TotalSize <= source.Length, $"box {box.Type} at {offset} escapes ({box.TotalSize})");
            if (++visited > 4_096 * 8) return any;
            if (depth < 5 && box.Type is "moov" or "trak" or "mdia" or "minf" or "stbl" or "uuid" or "meta")
            {
                foreach (var child in BmffBoxNavigator.ReadChildBoxes(source, box))
                {
                    Assert.True(child.Offset >= box.PayloadOffset && child.Offset + child.TotalSize <= box.Offset + box.TotalSize, $"child {child.Type} escapes its parent {box.Type}");
                    visited++;
                }
            }

            offset = box.Offset + box.TotalSize;
        }

        return any;
    }

    // ------------------------------------------------------------------ RAW container readers

    private static (string Name, IRawContainerReader Reader, string Ext, byte[] Seed)[] ContainerTargets() =>
    [
        ("CR2", new Cr2ContainerReader(), ".cr2", Cr2Seed()),
        ("CR3", new Cr3ContainerReader(), ".cr3", SyntheticRawBuilder.BuildCanonCr3(SyntheticRawBuilder.CreateMinimalJpeg(160, 120), SyntheticRawBuilder.CreateMinimalJpeg(32, 24))),
        ("RAF", new RafContainerReader(), ".raf", SyntheticRawBuilder.BuildRaf(Jpeg())),
        ("ORF", new OrfContainerReader(), ".orf", SyntheticRawBuilder.BuildTiff(true, Jpeg(), 1, 0x4F52)),
        ("NEF", new NefContainerReader(), ".nef", SyntheticRawBuilder.BuildTiff(true, Jpeg(), 1, 42)),
        ("DNG", new DngContainerReader(), ".dng", SyntheticRawBuilder.BuildTiff(true, Jpeg(), 1, 42)),
        ("RW2", new Rw2ContainerReader(), ".rw2", SyntheticRawBuilder.BuildRw2(Jpeg(), 6)),
        ("ARW", new ArwContainerReader(), ".arw", SyntheticRawBuilder.BuildTiff(true, Jpeg(), 1, 42)),
        ("NEF-BE", new NefContainerReader(), ".nef", SyntheticRawBuilder.BuildTiff(false, Jpeg(), 8, 42)),
    ];

    private static byte[] Cr2Seed()
    {
        var tiff = SyntheticRawBuilder.BuildTiff(true, Jpeg(), 6, 42);
        var cr2 = new byte[tiff.Length + 2];
        Array.Copy(tiff, 0, cr2, 0, 8);
        cr2[8] = (byte)'C';
        cr2[9] = (byte)'R';
        Array.Copy(tiff, 8, cr2, 10, tiff.Length - 8);
        return cr2;
    }

    [Fact(DisplayName = "Fuzz every RAW container reader (CR2/CR3/RAF/ORF/NEF/DNG/RW2/ARW), plus the consumers of what it accepts: only InvalidDataException")]
    public void Fuzz_RawContainerReaders_FailOnlyCleanly()
    {
        var index = 0;
        foreach (var (name, reader, ext, seed) in ContainerTargets())
        {
            var stats = BinaryFuzz.Run($"{name} container reader",
                BinaryFuzz.Mutants(seed, 4400 + index++),
                bytes =>
                {
                    if (!reader.CanRead(bytes.AsSpan(0, Math.Min(64, bytes.Length)), ext)) return false;
                    var source = new InMemoryRawHeaderSource(bytes);
                    var info = reader.Read(source, CancellationToken.None);
                    Assert.InRange(info.Orientation, 1, 8);
                    Assert.True(info.SensorWidth >= 0 && info.SensorHeight >= 0, "negative sensor size");
                    foreach (var preview in info.Previews)
                        Assert.True(preview.Offset >= 0 && preview.Length > 0 && preview.Length <= bytes.Length - preview.Offset, $"preview {preview.Offset}+{preview.Length} escapes the {bytes.Length}-byte file");

                    // Documented never-throw consumers of whatever the reader accepted.
                    _ = RawExif.TryReadExif(source, info);
                    _ = PreviewSelector.SelectPreview(source, info.Previews, new DecodeBox(32, 32), info.Orientation);
                    return true;
                },
                Invalid);
            AssertReached(stats, name);
        }
    }

    // ------------------------------------------------------------------ JPEG marker walkers

    [Fact(DisplayName = "Fuzz PreviewSelector / JpegMarkerProbe JPEG header walks (frame size, colour space, selection over hostile ranges): only InvalidDataException")]
    public void Fuzz_JpegMarkerWalkers_FailOnlyCleanly()
    {
        var withIcc = new List<byte> { 0xFF, 0xD8 };
        withIcc.AddRange(JpegBytes.ExifApp1(JpegBytes.OrientationTiff(true, 6)));
        withIcc.AddRange(JpegBytes.Segment(0xE2, [.. JpegBytes.IccTag, 1, 1, .. new byte[40]]));
        withIcc.AddRange(Jpeg().AsSpan(2).ToArray());
        var seeds = new[] { Jpeg(), [.. withIcc] };

        for (var s = 0; s < seeds.Length; s++)
        {
            var stats = BinaryFuzz.Run($"JPEG marker walkers (seed {s})",
                BinaryFuzz.Mutants(seeds[s], 4500 + s),
                bytes =>
                {
                    var source = new InMemoryRawHeaderSource(bytes);
                    var ok = JpegMarkerProbe.TryReadLossyFrame(source, 0, bytes.Length, out var w, out var h);
                    if (ok) Assert.True(w > 0 && h > 0, "frame accepted with a non-positive size");
                    _ = PreviewSelector.TryReadJpegFrame(source, 0, bytes.Length, out _, out _, out _);
                    // Lengths and offsets the container would claim but the file cannot honour.
                    _ = JpegMarkerProbe.TryReadLossyFrame(source, 0, long.MaxValue, out _, out _);
                    _ = JpegMarkerProbe.TryReadLossyFrame(source, bytes.Length / 2, bytes.Length, out _, out _);
                    _ = PreviewSelector.TryExtractJpegDimensions(bytes, out _, out _, out _);
                    _ = PreviewSelector.IsAdobeRgbExif(bytes);
                    var previews = new[]
                    {
                        new EmbeddedPreview(0, 0, bytes.Length, EmbeddedPreviewKind.Jpeg, 0, 0, PreviewColorSpace.Unknown),
                        new EmbeddedPreview(1, bytes.Length / 2, long.MaxValue / 2, EmbeddedPreviewKind.Jpeg, 0, 0, PreviewColorSpace.Unknown),
                        new EmbeddedPreview(2, 2, 3, EmbeddedPreviewKind.Jpeg, 640, 480, PreviewColorSpace.Unknown),
                    };
                    return PreviewSelector.SelectPreview(source, previews, new DecodeBox(32, 32), 1) is not null || ok;
                },
                Invalid);
            AssertReached(stats, "JPEG marker walkers");
        }
    }

    // ------------------------------------------------------------------ TurboJpeg header reading

    [Fact(DisplayName = "Fuzz TurboJpegDecoder.ReadInfo / ReadExifOrientation / HasEmbeddedIccProfile on mutated JPEG headers: documented exceptions only")]
    public void Fuzz_TurboJpegReadInfo_FailsOnlyCleanly()
    {
        var seed = ExifTestData.EncodeJpegWithExif(24, 16, orientation: 6);
        var decoder = new TurboJpegDecoder();
        var path = Path.Combine(_root.Path, "mutant.jpg");
        var stats = BinaryFuzz.Run("TurboJpeg ReadInfo",
            // ReadInfo goes through a file, so this corpus is thinner than the in-memory ones.
            BinaryFuzz.Mutants(seed, 4600, randomCount: 600),
            bytes =>
            {
                Assert.InRange(TurboJpegDecoder.ReadExifOrientation(bytes), 1, 8);
                _ = TurboJpegDecoder.HasEmbeddedIccProfile(bytes);
                File.WriteAllBytes(path, bytes);
                var info = decoder.ReadInfo(path);
                Assert.True(info.Width > 0 && info.Height > 0, "ReadInfo accepted a non-positive size");
                Assert.InRange(info.Orientation, 1, 8);
                return true;
            },
            ex => ex is InvalidDataException or NotSupportedException);
        AssertReached(stats, "TurboJpeg ReadInfo");
    }
}
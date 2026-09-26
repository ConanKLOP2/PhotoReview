using System.Diagnostics;
using PhotoReview.Imaging.Metadata;
using PhotoReview.Imaging.Tests.Metadata;
using PhotoReview.Imaging.TurboJpeg;

namespace PhotoReview.Imaging.Tests.Robustness;

/// <summary>
/// Random structural mutation (flip, insert, delete, duplicate, truncate, extreme 16-bit fields) of valid EXIF-bearing
/// JPEG headers and EXIF-summary cache blocks. The byte parsers run on every file a decoder or the disk cache hands
/// them, so they must never throw, hang or allocate proportionally to a hostile count field.
/// </summary>
[Trait("Category", "HotPath")]
public sealed class ParserMutationFuzzTests
{
    private const int Iterations = 40_000;

    private static IEnumerable<byte[]> Seeds()
    {
        foreach (var little in new[] { true, false })
        {
            var (ifd0, exif) = ExifTestData.FullCamera(little);
            yield return ExifTestData.JpegWithApp1(ExifTestData.Tiff(little, ifd0, exif), app0: "JFIF\0\u0001\u0001\0\0\u0001\0\u0001\0\0"u8.ToArray());
        }
        yield return [0xFF, 0xD8, .. JpegBytes.ExifApp1(JpegBytes.OrientationTiff(true, 6)), .. JpegBytes.Segment(0xE2, JpegBytes.IccTag), 0xFF, 0xDA, 0, 2];
        yield return [0xFF, 0xD8, .. JpegBytes.ExifApp1(JpegBytes.OrientationTiff(false, 8)), 0xFF, 0xDA, 0, 2];
    }

    [Fact(DisplayName = "Mutated EXIF/ICC JPEG headers never throw or stall in the byte parsers, and results stay in range")]
    public void MutatedJpegHeaders_NeverThrowOrStall()
    {
        var seeds = Seeds().ToArray();
        var rng = new Random(9_2026);
        var worst = TimeSpan.Zero;
        var parsed = 0;
        for (var i = 0; i < Iterations; i++)
        {
            var jpeg = JpegBytes.Mutate(rng, seeds[i % seeds.Length]);
            var started = Stopwatch.GetTimestamp();

            var summary = ExifParser.TryParseJpeg(jpeg);
            var orientation = TurboJpegDecoder.ReadExifOrientation(jpeg);
            _ = TurboJpegDecoder.HasEmbeddedIccProfile(jpeg);

            var elapsed = Stopwatch.GetElapsedTime(started);
            if (elapsed > worst) worst = elapsed;
            Assert.InRange(orientation, 1, 8);
            if (summary is not null)
            {
                parsed++;
                Assert.False(summary.IsEmpty);
                Assert.True((summary.CameraMake?.Length ?? 0) <= ExifSummary.MaxTextLength);
                Assert.True((summary.CameraModel?.Length ?? 0) <= ExifSummary.MaxTextLength);
                Assert.True((summary.LensModel?.Length ?? 0) <= ExifSummary.MaxTextLength);
            }
        }

        // Parsing a <= 64 KB header is microseconds; half a second for one call means an unbounded loop.
        Assert.True(worst < TimeSpan.FromMilliseconds(500), $"slowest parse {worst.TotalMilliseconds:F1} ms");
        Assert.True(parsed > Iterations / 10, $"only {parsed} mutants still parsed: the corpus no longer reaches the field readers");
    }

    [Fact(DisplayName = "Hostile TIFF structures (65535 declared entries, 0xFFFFFFFF counts, an offset at the block edge) are bounded")]
    public void HostileTiffStructures_AreBounded()
    {
        foreach (var little in new[] { true, false })
        {
            // 65535 declared entries in a block that holds two.
            var many = JpegBytes.Tiff(little, (0x010F, 2, 4, "Abc\0"u8.ToArray()), (0x0110, 2, 4, "Xyz\0"u8.ToArray()));
            many[8] = 0xFF;
            many[9] = 0xFF;
            Assert.NotNull(ExifParser.TryParseJpeg(JpegBytes.ExifApp1Jpeg(many)));

            // ASCII with count 0xFFFFFFFF whose offset points at the last byte of the block.
            var huge = JpegBytes.Tiff(little, (0x010F, 2, 0xFFFFFFFF, [0, 0, 0, 0]));
            var offsetField = 8 + 2 + 8;
            var edge = (uint)(huge.Length - 1);
            if (little) System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(huge.AsSpan(offsetField), edge);
            else System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(huge.AsSpan(offsetField), edge);
            Assert.Null(ExifParser.TryParseJpeg(JpegBytes.ExifApp1Jpeg(huge)));

        }
    }

    [Fact(DisplayName = "Mutated EXIF summary cache blocks decode to null or to a value that re-encodes to a decodable, equal block")]
    public void MutatedSummaryBlocks_NeverThrow_AndReEncodeStably()
    {
        var full = ExifSummary.Create("2024:05:01 14:03:22", "Canon", "Canon EOS R5", "RF24-70mm", 400,
            new ExifRational(50, 1), new ExifRational(28, 10), new ExifRational(1, 250))!;
        var seed = ExifSummaryCodec.Encode(full);
        Assert.NotEmpty(seed);
        var rng = new Random(77);
        var decoded = 0;
        for (var i = 0; i < Iterations; i++)
        {
            var block = JpegBytes.Mutate(rng, seed);
            var summary = ExifSummaryCodec.Decode(block);
            if (summary is null) continue;
            decoded++;
            Assert.False(summary.IsEmpty);
            var again = ExifSummaryCodec.Decode(ExifSummaryCodec.Encode(summary));
            Assert.Equal(summary, again);
        }

        Assert.True(decoded > 100, $"only {decoded} mutants decoded");
    }
}

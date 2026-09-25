using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoReview.Core.Model;
using PhotoReview.Imaging.Metadata;
using PhotoReview.Imaging.Tests.Fixtures;

namespace PhotoReview.Imaging.Tests.Robustness;

/// <summary>
/// The .pv4 preview cache entry is read from a disk cache that a killed process, a full disk or another tool can leave
/// in any state. PreviewImageService treats exactly IOException, UnauthorizedAccessException, NotSupportedException,
/// FileFormatException and InvalidDataException as "corrupt entry: delete and re-decode"; any other exception type
/// escaping PreviewCacheFile.Read would fail the user's preview instead. Random mutation of a valid entry (header
/// fields, EXIF block, JPEG payload) must therefore end in a good result or one of those types, quickly.
/// </summary>
[Trait("Category", "HotPath")]
public sealed class PreviewCacheFileMutationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "PhotoReview-Pv4Mutation-" + Guid.NewGuid().ToString("N"));

    public PreviewCacheFileMutationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static bool IsCorruptEntryFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or NotSupportedException or FileFormatException or InvalidDataException;

    private async Task<byte[]> WriteSeedAsync(int width = 16, int height = 12, int orientation = 6, ExifSummary? exif = null)
    {
        var gradient = FixtureGenerator.CreateGradientCheckerboard(width, height);
        var opaque = new FormatConvertedBitmap(gradient, PixelFormats.Bgr32, null, 0);
        opaque.Freeze();
        var image = new WpfDecodedImage(opaque, downscaled: true, orientation: orientation, actualBackend: DecoderBackend.TurboJpeg,
            originalWidth: 4000, originalHeight: 3000, exif: exif);
        var path = Path.Combine(_dir, "seed.pv4");
        await PreviewCacheFile.WriteAtomicallyAsync(image, path);
        return await File.ReadAllBytesAsync(path);
    }

    [Fact(DisplayName = "Mutated .pv4 entries always end in a good result or a 'corrupt entry' exception type, never anything else, and never stall")]
    public Task MutatedEntries_EndInResultOrCorruptEntryFailure() => RunMutations(iterations: 300, rngSeed: 4);

    [Fact(DisplayName = "Deep run: 10x more mutated .pv4 entries")]
    [Trait("Category", "Slow")]
    public Task MutatedEntries_DeepRun() => RunMutations(iterations: 3_000, rngSeed: DecoderMutationFuzzTests.DeepSeed(5));

    private async Task RunMutations(int iterations, int rngSeed)
    {
        var exif = ExifSummary.Create("2024:05:01 14:03:22", "Canon", "EOS R5", "RF24-70mm", 400,
            new ExifRational(50, 1), new ExifRational(28, 10), new ExifRational(1, 250));
        var seed = await WriteSeedAsync(exif: exif);
        var path = Path.Combine(_dir, "mutant.pv4");
        var rng = new Random(rngSeed);
        var good = 0;
        var rejected = 0;
        var worst = TimeSpan.Zero;

        for (var i = 0; i < iterations; i++)
        {
            var mutant = JpegBytes.Mutate(rng, seed);
            await File.WriteAllBytesAsync(path, mutant);
            var started = Stopwatch.GetTimestamp();
            try
            {
                var result = PreviewCacheFile.Read(path);
                good++;
                Assert.True(result.Bitmap.IsFrozen);
                Assert.InRange(result.Orientation, 1, 8);
                Assert.True(result.Bitmap.PixelWidth > 0 && result.Bitmap.PixelHeight > 0);
                Assert.True(result.OriginalWidth > 0 && result.OriginalHeight > 0);
                Assert.Equal(mutant.LongLength, result.FileBytes);
            }
            catch (Exception ex) when (IsCorruptEntryFailure(ex))
            {
                rejected++;
            }
            catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
            {
                Assert.Fail($"mutant {i} ({mutant.Length} bytes, {Convert.ToHexString(mutant.AsSpan(0, Math.Min(40, mutant.Length)))}...) escaped as {ex.GetType().FullName}: {ex.Message}");
            }

            var elapsed = Stopwatch.GetElapsedTime(started);
            if (elapsed > worst) worst = elapsed;
        }

        Assert.True(worst < TimeSpan.FromSeconds(2), $"slowest read {worst.TotalMilliseconds:F0} ms");
        Assert.True(good > iterations / 30 && rejected > iterations / 30, $"corpus is one-sided: {good} good, {rejected} rejected");
    }

    [Fact(DisplayName = "A .pv4 entry cut at ANY length is rejected: WPF would happily decode a truncated JPEG payload into a half-grey preview")]
    public async Task EveryTruncation_IsRejected()
    {
        var seed = await WriteSeedAsync(width: 8, height: 8, orientation: 3);
        var path = Path.Combine(_dir, "cut.pv4");
        for (var length = 0; length < seed.Length; length++)
        {
            await File.WriteAllBytesAsync(path, seed.AsSpan(0, length).ToArray());
            try
            {
                _ = PreviewCacheFile.Read(path);
                Assert.Fail($"an entry truncated to {length}/{seed.Length} bytes was accepted as a valid preview");
            }
            catch (Exception ex) when (IsCorruptEntryFailure(ex))
            {
                // expected
            }
        }

        Assert.True(PreviewCacheFile.Read(await WriteFullAsync(seed)).Bitmap.PixelWidth == 8); // the untruncated entry is still good
    }

    private async Task<string> WriteFullAsync(byte[] bytes)
    {
        var full = Path.Combine(_dir, "full.pv4");
        await File.WriteAllBytesAsync(full, bytes);
        return full;
    }

    [Theory(DisplayName = "Header fields at their extremes (dimensions 0/negative/int.Max, orientation 0/9/255, backend 255, alpha flag) are rejected as corrupt, not decoded or allocated")]
    [InlineData(8, 0)]
    [InlineData(8, -1)]
    [InlineData(8, int.MaxValue)]
    [InlineData(12, 0)]
    [InlineData(12, int.MinValue)]
    [InlineData(12, int.MaxValue)]
    [InlineData(16, 0)]
    [InlineData(16, -5)]
    [InlineData(20, 0)]
    [InlineData(20, int.MinValue)]
    public async Task ExtremeDimensionFields_AreRejected(int offset, int value)
    {
        var seed = await WriteSeedAsync();
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(seed.AsSpan(offset), value);
        var path = Path.Combine(_dir, "dims.pv4");
        await File.WriteAllBytesAsync(path, seed);

        // Width/height that disagree with the payload are corrupt; the original size must merely be positive, so a
        // huge original size is a good entry (it is only ever reported, never allocated).
        if (offset is 16 or 20 && value == int.MaxValue) { _ = PreviewCacheFile.Read(path); return; }
        Assert.Throws<InvalidDataException>(() => PreviewCacheFile.Read(path));
    }

    [Theory(DisplayName = "Orientation byte 0, 9 and 255 and an undefined backend byte are rejected")]
    [InlineData(6, 0)]
    [InlineData(6, 9)]
    [InlineData(6, 255)]
    [InlineData(5, 200)]
    public async Task InvalidEnumBytes_AreRejected(int offset, byte value)
    {
        var seed = await WriteSeedAsync();
        seed[offset] = value;
        var path = Path.Combine(_dir, "enum.pv4");
        await File.WriteAllBytesAsync(path, seed);

        Assert.Throws<InvalidDataException>(() => PreviewCacheFile.Read(path));
    }

    [Fact(DisplayName = "An EXIF block length that runs past the end of the file, or exceeds the codec maximum, is rejected")]
    public async Task OversizedExifLength_IsRejected()
    {
        var seed = await WriteSeedAsync();
        var path = Path.Combine(_dir, "exiflen.pv4");
        foreach (var length in new ushort[] { 1025, 0xFFFF, (ushort)(seed.Length) })
        {
            var mutant = (byte[])seed.Clone();
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(mutant.AsSpan(24), length);
            await File.WriteAllBytesAsync(path, mutant);
            Assert.Throws<InvalidDataException>(() => PreviewCacheFile.Read(path));
        }
    }

    [Fact(DisplayName = "Garbage in the EXIF block only loses the EXIF: the pixels of the entry are still served")]
    public async Task GarbageExifBlock_KeepsPixels()
    {
        var exif = ExifSummary.Create("2024:05:01 14:03:22", "Canon", "EOS R5", null, 400, null, null, null);
        var seed = await WriteSeedAsync(exif: exif);
        var length = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(seed.AsSpan(24));
        Assert.True(length > 4);
        for (var i = 0; i < length; i++) seed[26 + i] = 0xEE;
        var path = Path.Combine(_dir, "garbage-exif.pv4");
        await File.WriteAllBytesAsync(path, seed);

        var result = PreviewCacheFile.Read(path);

        Assert.Null(result.Exif);
        Assert.Equal(16, result.Bitmap.PixelWidth);
    }

    [Fact(DisplayName = "A zero-byte and a one-byte file are rejected as corrupt entries")]
    public async Task ZeroAndOneByteFiles_AreRejected()
    {
        var path = Path.Combine(_dir, "tiny.pv4");
        foreach (var bytes in new[] { Array.Empty<byte>(), new byte[] { (byte)'P' } })
        {
            await File.WriteAllBytesAsync(path, bytes);
            var ex = Assert.ThrowsAny<Exception>(() => PreviewCacheFile.Read(path));
            Assert.True(IsCorruptEntryFailure(ex), ex.GetType().FullName);
        }
    }
}

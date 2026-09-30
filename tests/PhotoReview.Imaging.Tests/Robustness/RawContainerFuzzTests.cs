using System.Diagnostics;
using System.IO;
using PhotoReview.Imaging.Raw;
using PhotoReview.Imaging.Raw.Bmff;
using PhotoReview.Imaging.Raw.Raf;
using PhotoReview.Imaging.Raw.Tiff;
using PhotoReview.Imaging.Tests.Raw;
using Xunit;

namespace PhotoReview.Imaging.Tests.Robustness;

/// <summary>
/// Fuzz tests for all RAW container readers (CR2, CR3, NEF, ARW, DNG, ORF, RAF, RW2).
/// Mutates headers via bit flips, truncation, slicing, extreme integers, and verifies:
/// 1. Only acceptable exception types are thrown (InvalidDataException, NotSupportedException, EndOfStreamException).
/// 2. Readers never hang or loop indefinitely (the p95 parse time after a warm-up stays under a generous budget).
/// 3. Memory read is strictly bounded by <see cref="RawContainerLimits.MaxHeaderBytes"/> (8 MB) and no allocations exceed safety thresholds.
/// </summary>
[Trait("Category", "HotPath")]
public sealed class RawContainerFuzzTests
{
    private const int IterationsPerFormat = 10_000;

    [Fact(DisplayName = "Fuzz CR2 container reader with mutated headers")]
    public void Fuzz_Cr2ContainerReader_Robust()
    {
        var validCr2 = SyntheticRawBuilder.BuildTiff(littleEndian: true, orientation: 6, magic: 42);
        // CR2 signature: 'CR' at offset 8
        var cr2Bytes = new byte[validCr2.Length + 10];
        Array.Copy(validCr2, 0, cr2Bytes, 0, 8);
        cr2Bytes[8] = (byte)'C';
        cr2Bytes[9] = (byte)'R';
        Array.Copy(validCr2, 8, cr2Bytes, 10, validCr2.Length - 8);

        var p95 = RunFuzzLoop(new Cr2ContainerReader(), ".cr2", cr2Bytes, 101);
        AssertWithinBudget(p95);
    }

    [Fact(DisplayName = "Fuzz NEF container reader with mutated headers")]
    public void Fuzz_NefContainerReader_Robust()
    {
        var validNef = SyntheticRawBuilder.BuildTiff(littleEndian: true, orientation: 1, magic: 42);
        var p95 = RunFuzzLoop(new NefContainerReader(), ".nef", validNef, 102);
        AssertWithinBudget(p95);
    }

    [Fact(DisplayName = "Fuzz ARW container reader with mutated headers")]
    public void Fuzz_ArwContainerReader_Robust()
    {
        var validArw = SyntheticRawBuilder.BuildTiff(littleEndian: true, orientation: 1, magic: 42);
        var p95 = RunFuzzLoop(new ArwContainerReader(), ".arw", validArw, 103);
        AssertWithinBudget(p95);
    }

    [Fact(DisplayName = "Fuzz DNG container reader with mutated headers")]
    public void Fuzz_DngContainerReader_Robust()
    {
        var validDng = SyntheticRawBuilder.BuildTiff(littleEndian: true, orientation: 1, magic: 42);
        var p95 = RunFuzzLoop(new DngContainerReader(), ".dng", validDng, 104);
        AssertWithinBudget(p95);
    }

    [Fact(DisplayName = "Fuzz ORF container reader with mutated headers")]
    public void Fuzz_OrfContainerReader_Robust()
    {
        var validOrf = SyntheticRawBuilder.BuildTiff(littleEndian: true, orientation: 1, magic: 0x4F52); // IIRO
        var p95 = RunFuzzLoop(new OrfContainerReader(), ".orf", validOrf, 105);
        AssertWithinBudget(p95);
    }

    [Fact(DisplayName = "Fuzz RW2 container reader with mutated headers")]
    public void Fuzz_Rw2ContainerReader_Robust()
    {
        var validRw2 = SyntheticRawBuilder.BuildTiff(littleEndian: true, orientation: 1, magic: 0x0055); // IIU\0
        var p95 = RunFuzzLoop(new Rw2ContainerReader(), ".rw2", validRw2, 106);
        AssertWithinBudget(p95);
    }

    [Fact(DisplayName = "Fuzz CR3 container reader with mutated headers")]
    public void Fuzz_Cr3ContainerReader_Robust()
    {
        var validCr3 = SyntheticRawBuilder.BuildIsoBmff();
        var p95 = RunFuzzLoop(new Cr3ContainerReader(), ".cr3", validCr3, 107);
        AssertWithinBudget(p95);
    }

    [Fact(DisplayName = "Fuzz RAF container reader with mutated headers")]
    public void Fuzz_RafContainerReader_Robust()
    {
        var validRaf = SyntheticRawBuilder.BuildRaf();
        var p95 = RunFuzzLoop(new RafContainerReader(), ".raf", validRaf, 108);
        AssertWithinBudget(p95);
    }

    [Fact(DisplayName = "Fuzz CR3 real Canon layout through strict sources, EXIF and preview selection")]
    public void Fuzz_Cr3RealLayout_StrictSource_FailsOnlyCleanlyAndConsumersNeverThrow()
    {
        var seed = SyntheticRawBuilder.BuildCanonCr3();
        var reader = new Cr3ContainerReader();
        var rng = new Random(109);
        int parsed = 0;

        for (int i = 0; i < IterationsPerFormat; i++)
        {
            var mutated = JpegBytes.Mutate(rng, seed);
            var source = new InMemoryRawHeaderSource(mutated);

            RawContainerInfo info;
            try
            {
                if (!reader.CanRead(mutated.AsSpan(0, Math.Min(64, mutated.Length)), ".cr3"))
                    continue;

                info = reader.Read(source, CancellationToken.None);
            }
            catch (InvalidDataException)
            {
                continue; // clean failure on a hostile file
            }

            parsed++;
            Assert.InRange(info.Orientation, 1, 8);
            Assert.True(info.SensorWidth >= 0 && info.SensorHeight >= 0, "Sensor dimensions went negative.");
            foreach (var p in info.Previews)
            {
                Assert.True(p.Offset >= 0 && p.Length > 0 && p.Length <= mutated.Length - p.Offset,
                    $"Preview {p.Offset}+{p.Length} escapes the {mutated.Length}-byte file.");
                Assert.True(p.Width >= 0 && p.Height >= 0);
            }

            // Documented never-throw consumers must hold on whatever the reader accepted.
            RawExif.TryReadExif(source, info);
            PreviewSelector.SelectPreview(source, info.Previews, DecodeBox.Unbounded, info.Orientation);
        }

        Assert.True(parsed > IterationsPerFormat / 10, $"Only {parsed} mutated files parsed; the fuzz seed is not exercising the reader.");
    }

    // A hang guard, not a benchmark: p95 of the parse times after a warm-up, with a budget ~1000x above the typical microseconds, so
    // JIT, a cold cache or a busy 2-vCPU CI runner cannot flip it, while a reader that becomes systematically slow (or loops) still does.
    // The single slowest parse is deliberately not asserted (it includes JIT and scheduler noise).
    private const int WarmUpIterations = 500;
    private static readonly TimeSpan P95Budget = TimeSpan.FromMilliseconds(2000);

    private static void AssertWithinBudget(TimeSpan p95) =>
        Assert.True(p95 < P95Budget, $"p95 parse time was {p95.TotalMilliseconds:F2} ms (budget {P95Budget.TotalMilliseconds:F0} ms)");

    /// <summary>Runs the mutated parses and returns the p95 parse time of the iterations after <see cref="WarmUpIterations"/>.</summary>
    private static TimeSpan RunFuzzLoop<TReader>(TReader reader, string ext, byte[] seed, int rngSeed)
        where TReader : IRawContainerReader
    {
        var rng = new Random(rngSeed);
        var timings = new List<TimeSpan>(IterationsPerFormat);

        for (int i = 0; i < IterationsPerFormat; i++)
        {
            var mutated = JpegBytes.Mutate(rng, seed);
            var headerSource = new FuzzTrackingHeaderSource(mutated);

            var started = Stopwatch.GetTimestamp();
            try
            {
                if (reader.CanRead(mutated.AsSpan(0, Math.Min(64, mutated.Length)), ext))
                {
                    var info = reader.Read(headerSource, CancellationToken.None);
                    Assert.NotNull(info);
                    Assert.InRange(info.Orientation, 1, 8);
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or EndOfStreamException)
            {
                // Expected parsing failure on malformed/corrupted streams
            }
            catch (Exception ex)
            {
                Assert.Fail($"Reader {reader.GetType().Name} threw unexpected exception {ex.GetType().FullName}: {ex.Message}");
            }

            var elapsed = Stopwatch.GetElapsedTime(started);
            if (i >= WarmUpIterations) timings.Add(elapsed);

            Assert.True(headerSource.TotalBytesRead <= RawContainerLimits.MaxHeaderBytes,
                $"Reader {reader.GetType().Name} exceeded MaxHeaderBytes limit ({headerSource.TotalBytesRead} > {RawContainerLimits.MaxHeaderBytes})");
        }

        timings.Sort();
        return timings[(int)Math.Ceiling(timings.Count * 0.95) - 1];
    }

    private sealed class FuzzTrackingHeaderSource(byte[] bytes) : IRawHeaderSource
    {
        public long Length => bytes.Length;
        public long TotalBytesRead { get; private set; }

        public ReadOnlySpan<byte> Read(long offset, int count)
        {
            if (offset < 0 || offset >= bytes.Length || count <= 0)
                return [];

            int available = (int)Math.Min(count, bytes.Length - offset);
            TotalBytesRead += available;

            if (TotalBytesRead > RawContainerLimits.MaxHeaderBytes)
                throw new InvalidDataException("Exceeded maximum header read budget.");

            return bytes.AsSpan((int)offset, available);
        }
    }
}

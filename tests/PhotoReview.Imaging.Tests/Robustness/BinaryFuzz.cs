using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace PhotoReview.Imaging.Tests.Robustness;

/// <summary>
/// Shared harness for the deterministic fuzz tests of the binary readers that take untrusted input. Two parts:
/// <see cref="Mutants"/> derives a bounded, fully reproducible corpus from valid sample bytes (truncation at every offset for
/// small files, length-field overwrites with 0 / 0x7FFFFFFF / 0xFFFFFFFF, seeded bit flips and random splices), and
/// <see cref="Run"/> feeds it to a reader on a worker thread under a wall-clock bound, so a hang fails the test (with the case
/// label that stalled) instead of hanging the run. Only two things are asserted: no hang, and every failure is the reader's
/// documented clean failure; anything else (IndexOutOfRange, ArgumentOutOfRange, NullReference, Overflow, OutOfMemory, ...)
/// is a defect reported with a label that reproduces it.
/// </summary>
internal static class BinaryFuzz
{
    /// <summary>Whole-corpus wall-clock bound: a corpus of a few thousand microsecond parses finishes in well under a second.</summary>
    public static readonly TimeSpan HangBound = TimeSpan.FromSeconds(60);

    /// <summary>Upper bound on the cases one systematic mutation family yields per seed, keeping each test in seconds.</summary>
    private const int FamilyCap = 4_000;

    /// <summary>One labelled mutant. The label names the mutation and its position, so a failure reproduces from the message alone.</summary>
    public readonly record struct Case(string Label, byte[] Bytes);

    /// <summary>Outcome counters of a <see cref="Run"/>: how many cases the reader accepted vs rejected cleanly.</summary>
    public sealed record Stats(int Accepted, int RejectedCleanly, int Total);

    /// <summary>The seed itself first (sanity: it must be accepted), then every mutation family. Deterministic for a given <paramref name="rngSeed"/>.</summary>
    public static IEnumerable<Case> Mutants(byte[] seed, int rngSeed, int randomCount = 1_500)
    {
        yield return new Case("seed", seed);

        // 1. Truncation: at every offset for a small file, otherwise the dense head, the dense tail and an even spread between.
        foreach (var keep in TruncationOffsets(seed.Length))
            yield return new Case($"truncate@{keep}", seed.AsSpan(0, keep).ToArray());

        // 2. Length / offset field overwrite: 4-byte extremes (both byte orders) and 2-byte extremes at (strided) positions.
        uint[] extremes32 = [0, 0x7FFFFFFF, 0xFFFFFFFF, 0x80000000, 1];
        ushort[] extremes16 = [0, 0x7FFF, 0xFFFF, 0x8000];
        foreach (var pos in Positions(seed.Length, FamilyCap / (extremes32.Length * 2 + extremes16.Length * 2)))
        {
            if (pos + 4 <= seed.Length)
            {
                foreach (var value in extremes32)
                {
                    var le = (byte[])seed.Clone();
                    BinaryPrimitives.WriteUInt32LittleEndian(le.AsSpan(pos), value);
                    yield return new Case($"u32le@{pos}={value:X8}", le);
                    var be = (byte[])seed.Clone();
                    BinaryPrimitives.WriteUInt32BigEndian(be.AsSpan(pos), value);
                    yield return new Case($"u32be@{pos}={value:X8}", be);
                }
            }

            if (pos + 2 <= seed.Length)
            {
                foreach (var value in extremes16)
                {
                    var le = (byte[])seed.Clone();
                    BinaryPrimitives.WriteUInt16LittleEndian(le.AsSpan(pos), value);
                    yield return new Case($"u16le@{pos}={value:X4}", le);
                    var be = (byte[])seed.Clone();
                    BinaryPrimitives.WriteUInt16BigEndian(be.AsSpan(pos), value);
                    yield return new Case($"u16be@{pos}={value:X4}", be);
                }
            }
        }

        // 3. Seeded random: bit flips, byte stores and slice splices (overwrite or insert a copy of another part of the file).
        var rng = new Random(rngSeed);
        for (var i = 0; i < randomCount; i++)
        {
            var data = (byte[])seed.Clone();
            string label;
            switch (rng.Next(4))
            {
                case 0:
                {
                    var sb = new StringBuilder("bitflip");
                    for (var f = rng.Next(1, 5); f > 0; f--)
                    {
                        var p = rng.Next(data.Length);
                        var bit = rng.Next(8);
                        data[p] ^= (byte)(1 << bit);
                        sb.Append(CultureInfo.InvariantCulture, $" {p}.{bit}");
                    }

                    label = sb.ToString();
                    break;
                }
                case 1:
                {
                    var p = rng.Next(data.Length);
                    var b = (byte)rng.Next(256);
                    data[p] = b;
                    label = $"store@{p}={b:X2}";
                    break;
                }
                case 2:
                {
                    var from = rng.Next(data.Length);
                    var len = rng.Next(1, Math.Min(64, data.Length - from) + 1);
                    var to = rng.Next(data.Length);
                    var n = Math.Min(len, data.Length - to);
                    Array.Copy(seed, from, data, to, n);
                    label = $"splice-over from={from} len={n} to={to}";
                    break;
                }
                default:
                {
                    var from = rng.Next(data.Length);
                    var len = rng.Next(1, Math.Min(64, data.Length - from) + 1);
                    var to = rng.Next(data.Length + 1);
                    data = [.. data.AsSpan(0, to), .. seed.AsSpan(from, len), .. data.AsSpan(to)];
                    label = $"splice-insert from={from} len={len} to={to}";
                    break;
                }
            }

            yield return new Case(label, data);
        }
    }

    private static IEnumerable<int> TruncationOffsets(int length)
    {
        if (length <= FamilyCap)
        {
            for (var keep = 0; keep < length; keep++) yield return keep;
            yield break;
        }

        var seen = new SortedSet<int>();
        for (var keep = 0; keep < 512; keep++) seen.Add(keep);
        for (var keep = length - 256; keep < length; keep++) seen.Add(keep);
        var step = Math.Max(1, length / (FamilyCap - 768));
        for (var keep = 512; keep < length; keep += step) seen.Add(keep);
        foreach (var keep in seen) yield return keep;
    }

    private static IEnumerable<int> Positions(int length, int cap)
    {
        var step = Math.Max(1, (int)Math.Ceiling(length / (double)Math.Max(1, cap)));
        for (var pos = 0; pos < length; pos += step) yield return pos;
    }

    /// <summary>
    /// Runs <paramref name="exercise"/> over <paramref name="cases"/> on a worker thread bounded by <paramref name="bound"/> (default
    /// <see cref="HangBound"/>); the worker checks a cancellation token between cases so a late finish stops cleanly. The delegate
    /// returns true when the reader accepted the input (parsed something), false when it rejected it through its normal result.
    /// A throw that <paramref name="isCleanFailure"/> accepts counts as a clean rejection; any other throw is collected as a defect.
    /// </summary>
    public static Stats Run(string target, IEnumerable<Case> cases, Func<byte[], bool> exercise, Func<Exception, bool> isCleanFailure, TimeSpan? bound = null)
    {
        var limit = bound ?? HangBound;
        using var cts = new CancellationTokenSource();
        var current = "(not started)";
        var defects = new List<string>();
        int accepted = 0, rejected = 0, total = 0;

        var worker = Task.Run(() =>
        {
            foreach (var c in cases)
            {
                if (cts.IsCancellationRequested) return;
                Volatile.Write(ref current, c.Label);
                total++;
                try
                {
                    if (exercise(c.Bytes)) accepted++;
                    else rejected++;
                }
                catch (Exception ex) when (isCleanFailure(ex))
                {
                    rejected++;
                }
                catch (Exception ex)
                {
                    if (defects.Count < 8) defects.Add($"[{c.Label}] ({c.Bytes.Length} bytes) {ex.GetType().FullName}: {ex.Message}");
                }
            }
        }, CancellationToken.None);

        if (!worker.Wait(limit))
        {
            cts.Cancel();
            Assert.Fail($"{target}: no result within {limit.TotalSeconds:F0} s -- stuck on case [{Volatile.Read(ref current)}] (hang or runaway loop).");
        }

        worker.GetAwaiter().GetResult();
        Assert.True(defects.Count == 0, $"{target}: {defects.Count} non-documented failure(s) in {total} cases:{Environment.NewLine}{string.Join(Environment.NewLine, defects)}");
        return new Stats(accepted, rejected, total);
    }
}
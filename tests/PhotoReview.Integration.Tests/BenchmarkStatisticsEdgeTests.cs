using System.Globalization;
using PhotoReview.Benchmarking;
using PhotoReview.Core.Diagnostics;
using PhotoReview.PerfAnalysis;

namespace PhotoReview.Integration.Tests;

/// <summary>
/// Review lane tools/CI/benchmark: edge-case, table-driven and property tests for the percentile/ranking maths used by
/// the benchmark reports (BenchmarkStatistics, BenchmarkPhaseResult, BenchmarkRanking) and the perf analyzer
/// (PerfStats.NearestRank, PerfRow parsing), against an independent integer/decimal oracle.
/// </summary>
public sealed class BenchmarkStatisticsEdgeTests
{
    private static BenchmarkPhaseResult Phase(string id, BenchmarkWorkload workload, double[] samples,
        BenchmarkResultStatus status = BenchmarkResultStatus.Pass, int? imagesPerSample = 1) =>
        new(id, workload, samples, status, null, imagesPerSample);

    // ---- PerfStats.NearestRank vs an exact oracle ------------------------------------------------------------

    /// <summary>Exact nearest-rank oracle: 1-based rank ceil(P*N/100) computed in integers (P is a whole percent).</summary>
    private static int OracleRank(int percent, int count) => Math.Max(1, (percent * count + 99) / 100);

    [Fact(DisplayName = "NearestRank equals the integer oracle for every whole percent and N up to 400 (floating-point ceil must not overshoot)")]
    public void NearestRank_MatchesIntegerOracleForAllWholePercents()
    {
        for (var n = 1; n <= 400; n++)
        {
            var sorted = Enumerable.Range(1, n).Select(i => (double)i).ToArray();
            for (var p = 0; p <= 100; p++)
            {
                var expected = OracleRank(p, n);
                var actual = PerfStats.NearestRank(sorted, p);
                Assert.True(expected == actual, $"P{p} of N={n}: expected rank {expected}, got {actual}");
            }
        }
    }

    [Theory(DisplayName = "NearestRank matches a decimal oracle for fractional percentiles")]
    [InlineData(99.9, 1000, 999)]
    [InlineData(99.5, 200, 199)]
    [InlineData(0.1, 1000, 1)]
    [InlineData(12.5, 8, 1)]
    [InlineData(37.5, 8, 3)]
    [InlineData(99.99, 10000, 9999)]
    public void NearestRank_FractionalPercentiles(double percentile, int count, int expectedRank)
    {
        var sorted = Enumerable.Range(1, count).Select(i => (double)i).ToArray();
        var oracle = (int)Math.Ceiling((decimal)percentile * count / 100m);
        Assert.Equal(expectedRank, oracle); // the table itself agrees with the decimal oracle
        Assert.Equal(expectedRank, PerfStats.NearestRank(sorted, percentile));
    }

    [Fact(DisplayName = "NearestRank property: seeded random data with ties is monotone in P, bounded by min/max and equals the oracle element")]
    public void NearestRank_RandomDataWithTies_Property()
    {
        var random = new Random(20260926);
        for (var iteration = 0; iteration < 300; iteration++)
        {
            var n = random.Next(1, 120);
            var data = Enumerable.Range(0, n).Select(_ => (double)random.Next(0, 15)).OrderBy(x => x).ToArray(); // many ties
            var previous = double.NegativeInfinity;
            for (var p = 0; p <= 100; p++)
            {
                var actual = PerfStats.NearestRank(data, p);
                Assert.Equal(data[OracleRank(p, n) - 1], actual);
                Assert.InRange(actual, data[0], data[^1]);
                Assert.True(actual >= previous, $"not monotone at P{p}, N={n}");
                previous = actual;
            }
        }
    }

    [Theory(DisplayName = "NearestRank with 1 and 2 samples: P0 is the minimum rank, P50 the first, P>50 the last")]
    [InlineData(0, 1)]
    [InlineData(50, 1)]
    [InlineData(51, 2)]
    [InlineData(95, 2)]
    [InlineData(100, 2)]
    public void NearestRank_TwoSamples(double percentile, double expected) =>
        Assert.Equal(expected, PerfStats.NearestRank([1.0, 2.0], percentile));

    [Fact(DisplayName = "NearestRank of a single sample is that sample for any percentile")]
    public void NearestRank_SingleSample() =>
        Assert.All(new[] { 0.0, 1, 50, 95, 100 }, p => Assert.Equal(7.5, PerfStats.NearestRank([7.5], p)));

    [Theory(DisplayName = "NearestRank clamps out-of-range percentiles and answers NaN for a NaN percentile instead of an arbitrary element")]
    [InlineData(-5.0, 1.0)]
    [InlineData(150.0, 3.0)]
    [InlineData(double.PositiveInfinity, 3.0)]
    [InlineData(double.NegativeInfinity, 1.0)]
    public void NearestRank_OutOfRangePercentileIsClamped(double percentile, double expected) =>
        Assert.Equal(expected, PerfStats.NearestRank([1.0, 2.0, 3.0], percentile));

    [Fact(DisplayName = "NearestRank of a NaN percentile is NaN (used to silently return the minimum)")]
    public void NearestRank_NaNPercentileIsNaN() =>
        Assert.True(double.IsNaN(PerfStats.NearestRank([1.0, 2.0, 3.0], double.NaN)));

    [Fact(DisplayName = "NearestRank keeps huge values exact (no arithmetic on the data)")]
    public void NearestRank_HugeValuesAreReturnedVerbatim()
    {
        double[] sorted = [-double.MaxValue, 0, double.MaxValue];
        Assert.Equal(double.MaxValue, PerfStats.NearestRank(sorted, 100));
        Assert.Equal(-double.MaxValue, PerfStats.NearestRank(sorted, 0));
    }

    // ---- PerfRow: non-finite numbers from a corrupted CSV -----------------------------------------------------

    [Theory(DisplayName = "PerfRow numeric columns ignore NaN/Infinity text so a corrupt CSV cannot poison percentiles")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    [InlineData("1e999")]
    [InlineData("")]
    [InlineData("abc")]
    public void PerfRow_NonFiniteNumbersAreNull(string text)
    {
        var row = new PerfRow(1, 1, 1, "Ev", "1", "p", text, text, text, text, "");
        Assert.Null(row.ANum);
        Assert.Null(row.DNum);
    }

    [Theory(DisplayName = "PerfRow parses finite numbers with the invariant culture")]
    [InlineData("12.5", 12.5)]
    [InlineData("-3", -3.0)]
    [InlineData("1e3", 1000.0)]
    public void PerfRow_FiniteNumbersParse(string text, double expected)
    {
        var row = new PerfRow(1, 1, 1, "Ev", "1", "p", text, "", "", "", "");
        Assert.Equal(expected, row.ANum);
    }

    // ---- BenchmarkStatistics.Percentile (linear interpolation) ------------------------------------------------

    private static double LinearOracle(double[] sorted, decimal p)
    {
        var position = (sorted.Length - 1) * p;
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        var fraction = (double)(position - lower);
        return sorted[lower] + (sorted[upper] - sorted[lower]) * fraction;
    }

    [Fact(DisplayName = "Percentile (linear) matches the oracle on random data with ties for many probabilities and sizes 1..60")]
    public void Percentile_MatchesOracle()
    {
        var random = new Random(4242);
        for (var iteration = 0; iteration < 200; iteration++)
        {
            var n = random.Next(1, 60);
            var data = Enumerable.Range(0, n).Select(_ => Math.Round(random.NextDouble() * 100, 1)).ToArray();
            var sorted = data.OrderBy(x => x).ToArray();
            foreach (var p in new[] { 0m, 0.01m, 0.25m, 0.5m, 0.75m, 0.95m, 0.99m, 1m })
            {
                var actual = BenchmarkStatistics.Percentile(data, (double)p);
                Assert.Equal(LinearOracle(sorted, p), actual, 9);
                Assert.InRange(actual, sorted[0], sorted[^1]);
            }
        }
    }

    [Theory(DisplayName = "Percentile with 0/1/2/3 samples (table)")]
    [InlineData(new double[0], 0.95, 0.0)]
    [InlineData(new[] { 5.0 }, 0.0, 5.0)]
    [InlineData(new[] { 5.0 }, 1.0, 5.0)]
    [InlineData(new[] { 2.0, 4.0 }, 0.99, 3.98)]
    [InlineData(new[] { 4.0, 2.0 }, 0.5, 3.0)]
    [InlineData(new[] { 3.0, 3.0, 3.0 }, 0.95, 3.0)]
    public void Percentile_SmallSamples(double[] values, double p, double expected) =>
        Assert.Equal(expected, BenchmarkStatistics.Percentile(values, p), 9);

    // ---- BenchmarkPhaseResult ---------------------------------------------------------------------------------

    [Fact(DisplayName = "BenchmarkPhaseResult: 0 samples report 0 for every statistic and Max, not an exception")]
    public void PhaseResult_NoSamples()
    {
        var phase = Phase("x", BenchmarkWorkload.Sequential, [], BenchmarkResultStatus.InsufficientData);
        Assert.Equal(0, phase.Count);
        Assert.Equal(0, phase.P50);
        Assert.Equal(0, phase.P95);
        Assert.Equal(0, phase.P99);
        Assert.Equal(0, phase.Max);
    }

    [Fact(DisplayName = "BenchmarkPhaseResult: one sample is every percentile and the max")]
    public void PhaseResult_OneSample()
    {
        var phase = Phase("x", BenchmarkWorkload.Sequential, [12.5]);
        Assert.All(new[] { phase.P50, phase.P95, phase.P99, phase.Max }, v => Assert.Equal(12.5, v));
    }

    [Fact(DisplayName = "BenchmarkPhaseResult: statistics ignore input order and handle ties")]
    public void PhaseResult_UnsortedInputWithTies()
    {
        var phase = Phase("x", BenchmarkWorkload.Sequential, [9, 1, 5, 5, 5, 1, 9]);
        Assert.Equal(5, phase.P50);
        Assert.Equal(9, phase.Max);
    }

    [Fact(DisplayName = "BenchmarkPhaseResult: a 'with' copy that replaces Samples reports the NEW samples' statistics (lazy cache must not be shared)")]
    public void PhaseResult_WithCopyRecomputesStatistics()
    {
        var original = Phase("x", BenchmarkWorkload.Sequential, [1, 2, 3]);
        Assert.Equal(2, original.P50); // forces the lazily sorted cache on the original

        var copy = original with { Samples = [100, 200, 300] };

        Assert.Equal(200, copy.P50);
        Assert.Equal(300, copy.Max);
        Assert.Equal(2, original.P50);
        Assert.Equal(3, original.Max);
    }

    [Fact(DisplayName = "BenchmarkPhaseResult: a 'with' copy taken BEFORE the cache is forced is also independent")]
    public void PhaseResult_WithCopyBeforeForcing()
    {
        var original = Phase("x", BenchmarkWorkload.Sequential, [1, 2, 3]);
        var copy = original with { Samples = [100, 200, 300] };
        Assert.Equal(200, copy.P50);
        Assert.Equal(2, original.P50);
    }

    [Theory(DisplayName = "BenchmarkPhaseResult: huge finite samples stay finite and ordered")]
    [InlineData(1e300)]
    [InlineData(double.MaxValue)]
    public void PhaseResult_HugeValues(double huge)
    {
        var phase = Phase("x", BenchmarkWorkload.Sequential, [1, huge / 4, huge / 2]);
        Assert.True(double.IsFinite(phase.P95));
        Assert.Equal(huge / 2, phase.Max);
        Assert.True(phase.P50 <= phase.P95 && phase.P95 <= phase.Max);
    }

    // ---- ImagesPerSample --------------------------------------------------------------------------------------

    private static BenchmarkProfile ProfileWith(int workers, BenchmarkWorkload workload) =>
        BenchmarkProfiles.All.First(p => p.Workload == workload) with { Workers = workers, Workload = workload };

    [Theory(DisplayName = "ImagesPerSample: navigation-style workloads decode one image; parallel ones min(workers, files), never below 1")]
    [InlineData(BenchmarkWorkload.FirstFrame, 8, 100, 1)]
    [InlineData(BenchmarkWorkload.Preload, 8, 100, 1)]
    [InlineData(BenchmarkWorkload.WarmNext, 8, 100, 1)]
    [InlineData(BenchmarkWorkload.FileAction, 8, 100, 1)]
    [InlineData(BenchmarkWorkload.Sequential, 8, 100, 8)]
    [InlineData(BenchmarkWorkload.Sequential, 8, 3, 3)]
    [InlineData(BenchmarkWorkload.Random, 12, 1, 1)]
    [InlineData(BenchmarkWorkload.Random, 12, 0, 1)]
    [InlineData(BenchmarkWorkload.Sequential, 0, 10, 1)]
    [InlineData(BenchmarkWorkload.Correctness, 4, int.MaxValue, 4)]
    public void ImagesPerSample_Table(BenchmarkWorkload workload, int workers, int files, int expected) =>
        Assert.Equal(expected, BenchmarkWorkloadRunner.ImagesPerSample(ProfileWith(workers, workload), workload, files));

    [Fact(DisplayName = "ImagesPerSample matches the number of indices SelectParallelIndices actually picks for every profile and folder size")]
    public void ImagesPerSample_AgreesWithSelectedIndices()
    {
        foreach (var profile in BenchmarkProfiles.All.Where(p => p.Workload is BenchmarkWorkload.Sequential or BenchmarkWorkload.Random or BenchmarkWorkload.Correctness))
            foreach (var files in new[] { 1, 2, 5, 64 })
            {
                var picked = BenchmarkWorkloadRunner.SelectParallelIndices(files, profile, profile.Workload, 0, new Random(1));
                Assert.Equal(picked.Length, BenchmarkWorkloadRunner.ImagesPerSample(profile, profile.Workload, files));
            }
    }

    [Fact(DisplayName = "EffectiveImagesPerSample is at least 1 even for a non-positive stamp")]
    public void EffectiveImagesPerSample_IsAtLeastOne()
    {
        Assert.Equal(1, Phase("no-such-profile", BenchmarkWorkload.Sequential, [1], imagesPerSample: 0).EffectiveImagesPerSample);
        Assert.Equal(1, Phase("no-such-profile", BenchmarkWorkload.Sequential, [1], imagesPerSample: -7).EffectiveImagesPerSample);
        Assert.Equal(1, Phase("no-such-profile", BenchmarkWorkload.Sequential, [1], imagesPerSample: null).EffectiveImagesPerSample);
    }

    // ---- BenchmarkRanking -------------------------------------------------------------------------------------

    [Fact(DisplayName = "Ranking is by per-image P95 then per-image P50, and ties keep the input order (stable)")]
    public void Ranking_TiesAreStable()
    {
        var a = Phase("a", BenchmarkWorkload.Sequential, [10, 10, 10]);
        var b = Phase("b", BenchmarkWorkload.Sequential, [10, 10, 10]);
        var c = Phase("c", BenchmarkWorkload.Sequential, [5, 5, 5]);
        var d = Phase("d", BenchmarkWorkload.Sequential, [10, 10, 10]);

        Assert.Equal(["c", "a", "b", "d"], BenchmarkRanking.Rank([a, b, c, d], BenchmarkWorkload.Sequential).Select(p => p.ProfileId));
        Assert.Equal(["c", "d", "b", "a"], BenchmarkRanking.Rank([d, b, c, a], BenchmarkWorkload.Sequential).Select(p => p.ProfileId));
    }

    [Fact(DisplayName = "Ranking breaks a P95 tie with the per-image P50")]
    public void Ranking_P95TieBrokenByP50()
    {
        var slowMedian = Phase("slow-median", BenchmarkWorkload.Sequential, [9, 9, 9, 9, 9, 9, 9, 9, 9, 9]);
        var fastMedian = Phase("fast-median", BenchmarkWorkload.Sequential, [1, 1, 1, 1, 1, 1, 1, 1, 9, 9]);
        Assert.Equal(slowMedian.P95, fastMedian.P95);
        Assert.Equal(["fast-median", "slow-median"], BenchmarkRanking.Rank([slowMedian, fastMedian], BenchmarkWorkload.Sequential).Select(p => p.ProfileId));
    }

    [Fact(DisplayName = "Ranking compares per image: a parallel sample of 8 images at 80 ms beats a single image at 20 ms")]
    public void Ranking_UsesPerImageUnit()
    {
        var parallel = Phase("parallel", BenchmarkWorkload.Sequential, [80, 80, 80], imagesPerSample: 8);
        var single = Phase("single", BenchmarkWorkload.Sequential, [20, 20, 20], imagesPerSample: 1);
        Assert.Equal(["parallel", "single"], BenchmarkRanking.Rank([single, parallel], BenchmarkWorkload.Sequential).Select(p => p.ProfileId));
    }

    [Fact(DisplayName = "Ranking drops other workloads, failed and insufficient-data phases, and keeps Warn")]
    public void Ranking_FiltersByWorkloadAndStatus()
    {
        var pass = Phase("pass", BenchmarkWorkload.Sequential, [3]);
        var warn = Phase("warn", BenchmarkWorkload.Sequential, [4], BenchmarkResultStatus.Warn);
        var fail = Phase("fail", BenchmarkWorkload.Sequential, [1], BenchmarkResultStatus.Fail);
        var none = Phase("none", BenchmarkWorkload.Sequential, [], BenchmarkResultStatus.InsufficientData);
        var other = Phase("other", BenchmarkWorkload.Random, [1]);

        Assert.Equal(["pass", "warn"], BenchmarkRanking.Rank([fail, other, warn, none, pass], BenchmarkWorkload.Sequential).Select(p => p.ProfileId));
        Assert.Empty(BenchmarkRanking.Rank([], BenchmarkWorkload.Sequential));
    }

    [Fact(DisplayName = "Ranking never lets a phase with NaN samples win (NaN sorts before every number in LINQ)")]
    public void Ranking_NaNSamplesDoNotWin()
    {
        var nan = Phase("nan", BenchmarkWorkload.Sequential, [double.NaN, double.NaN]);
        var real = Phase("real", BenchmarkWorkload.Sequential, [50, 50]);
        var ranked = BenchmarkRanking.Rank([nan, real], BenchmarkWorkload.Sequential).Select(p => p.ProfileId).ToArray();
        Assert.DoesNotContain("nan", ranked);
        Assert.Equal(["real"], ranked);
    }

    [Fact(DisplayName = "Report JSON of an empty phase serializes finite numbers (no NaN/Infinity tokens)")]
    public void ReportJson_EmptyPhaseIsFinite()
    {
        var report = new BenchmarkReport("run", DateTimeOffset.UnixEpoch, "C:\\x",
            [Phase("x", BenchmarkWorkload.Sequential, [], BenchmarkResultStatus.InsufficientData)]);
        var json = report.ToJson();
        Assert.DoesNotContain("NaN", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Infinity", json, StringComparison.Ordinal);
        Assert.Contains(string.Create(CultureInfo.InvariantCulture, $"\"P50\": 0"), json, StringComparison.Ordinal);
    }
}

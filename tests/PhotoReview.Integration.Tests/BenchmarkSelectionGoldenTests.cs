using PhotoReview.Benchmarking;

namespace PhotoReview.Integration.Tests;

/// <summary>
/// Benchmark reproducibility: the same profile must pick the same files in every process. Comparing two runs
/// inside one test process cannot catch a regression to <c>string.GetHashCode()</c> (randomized per process,
/// stable within one) or to an unseeded <see cref="Random"/> shared by both sides, so these pin golden values.
/// </summary>
[Trait("Category", "Integration")]
public sealed class BenchmarkSelectionGoldenTests
{
    [Fact(DisplayName = "CreateSeededRandom yields a fixed sequence for a profile id, identical in every process")]
    public void CreateSeededRandom_YieldsGoldenSequence()
    {
        var random = BenchmarkWorkloadRunner.CreateSeededRandom("preview-balanced");

        var sequence = Enumerable.Range(0, 12).Select(_ => random.Next(1000)).ToArray();

        Assert.Equal(GoldenPreviewBalanced, sequence);
    }

    [Fact(DisplayName = "The Random workload selects a fixed file-index sequence for its profile, identical in every process")]
    public void RandomWorkload_SelectsGoldenFileIndices()
    {
        var profile = BenchmarkProfiles.Find("random-navigation")! with { Workers = 2 };
        var random = BenchmarkWorkloadRunner.CreateSeededRandom(profile.Id);

        var selected = Enumerable.Range(0, 6)
            .Select(iteration => BenchmarkWorkloadRunner.SelectParallelIndices(5, profile, BenchmarkWorkload.Random, iteration, random))
            .ToArray();

        Assert.Equal(GoldenRandomNavigation, selected);
    }

    // Golden values: new Random(seed) with seed = the 17/31 string hash of the profile id. Recomputed independently
    // in another runtime (Windows PowerShell 5.1 / .NET Framework System.Random), which matched.
    private static readonly int[] GoldenPreviewBalanced = [93, 922, 763, 917, 322, 591, 596, 88, 118, 911, 927, 640];

    private static readonly int[][] GoldenRandomNavigation = [[4, 4], [3, 2], [3, 3], [0, 1], [4, 4], [2, 1]];
}

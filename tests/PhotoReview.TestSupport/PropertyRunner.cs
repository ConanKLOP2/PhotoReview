using System.Globalization;

namespace PhotoReview.TestSupport;

/// <summary>Thrown by <see cref="PropertyRunner"/>; the message always carries the seed and the iteration so a failure can be replayed.</summary>
public sealed class PropertyFailedException(string message, Exception inner) : Exception(message, inner);

/// <summary>
/// Minimal seeded property-test runner (no package: FsCheck is not available to the offline CI restore, and the repo already
/// uses seeded <see cref="Random"/> loops - this gives them one shared, reproducible shape).
/// <para>Default: a fixed seed list (1..<c>fixedSeeds</c>), so CI is deterministic. Set the environment variable
/// <see cref="SeedEnvVar"/> to an integer to replay exactly that seed, or to <c>random</c> to run one fresh seed
/// (<see cref="Environment.TickCount"/>-derived). Every failure message names the seed, the iteration and the replay command.</para>
/// </summary>
public static class PropertyRunner
{
    /// <summary>Environment variable: an integer (replay that seed only) or <c>random</c> (one fresh seed).</summary>
    public const string SeedEnvVar = "PHOTOREVIEW_PROP_SEED";

    /// <summary>The seeds a property runs with: the fixed list, or the one chosen through <see cref="SeedEnvVar"/>.</summary>
    public static IReadOnlyList<int> Seeds(int fixedSeeds = 3, string? envValue = null)
    {
        envValue ??= Environment.GetEnvironmentVariable(SeedEnvVar);
        if (string.IsNullOrWhiteSpace(envValue)) return [.. Enumerable.Range(1, fixedSeeds)];
        if (string.Equals(envValue.Trim(), "random", StringComparison.OrdinalIgnoreCase)) return [Environment.TickCount & int.MaxValue];
        return int.TryParse(envValue.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seed)
            ? [seed]
            : throw new ArgumentException($"{SeedEnvVar} must be an integer or 'random', not '{envValue}'.");
    }

    /// <summary>Runs <paramref name="body"/> <paramref name="iterations"/> times per seed with a <see cref="Random"/> seeded per seed.</summary>
    public static void Check(string property, int iterations, Action<Random, int> body, int fixedSeeds = 3, string? envValue = null)
    {
        ArgumentNullException.ThrowIfNull(body);
        foreach (var seed in Seeds(fixedSeeds, envValue))
        {
            var rng = new Random(seed);
            var iteration = 0;
            try
            {
                for (; iteration < iterations; iteration++) body(rng, iteration);
            }
            catch (Exception ex) when (ex is not PropertyFailedException)
            {
                throw new PropertyFailedException(
                    $"Property '{property}' failed: seed={seed} iteration={iteration}. Replay: set {SeedEnvVar}={seed}. {ex.Message}", ex);
            }
        }
    }

    /// <summary>A string of 0..<paramref name="maxLength"/> characters drawn from <paramref name="alphabet"/>.</summary>
    public static string RandomString(Random rng, string alphabet, int maxLength)
    {
        var chars = new char[rng.Next(0, maxLength + 1)];
        for (var i = 0; i < chars.Length; i++) chars[i] = alphabet[rng.Next(alphabet.Length)];
        return new string(chars);
    }

    /// <summary>A long weighted towards the edges (0, 1, negative, int/long limits) where bounds bugs live.</summary>
    public static long EdgyLong(Random rng) => rng.Next(10) switch
    {
        0 => 0,
        1 => 1,
        2 => -1,
        3 => long.MaxValue,
        4 => long.MinValue,
        5 => int.MaxValue + 1L,
        6 => rng.NextInt64(-1000, 1000),
        7 => rng.NextInt64(0, 1L << 32),
        _ => rng.NextInt64(long.MinValue, long.MaxValue),
    };
}

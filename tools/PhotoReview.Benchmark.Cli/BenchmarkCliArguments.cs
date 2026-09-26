using System.Globalization;
using PhotoReview.Benchmarking;

namespace PhotoReview.Benchmark.Cli;

/// <summary>
/// Argument interpretation for the CLI modes that take free-form lists, extracted from Program.cs so the edge cases
/// (empty list, unknown id, culture, negative numbers) are unit-testable. Every failure is an
/// <see cref="ArgumentException"/> whose message is safe to print; Program turns it into exit code 2.
/// </summary>
internal static class BenchmarkCliArguments
{
    /// <summary>The profiles a <c>--benchmark</c>, <c>--benchmark-all</c> or <c>--benchmark-actions</c> invocation runs.</summary>
    public static IReadOnlyList<BenchmarkProfile> ResolveProfiles(string mode, IReadOnlyList<string> args)
    {
        switch (mode)
        {
            case "--benchmark-all":
                return BenchmarkProfiles.Runnable;
            case "--benchmark-actions":
                return [.. BenchmarkProfiles.All.Where(p => p.Workload == BenchmarkWorkload.FileAction)];
            case "--benchmark":
                if (args.Count < 3) return [BenchmarkProfiles.Find("recommended-auto")!];
                var ids = args[2].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                // "," or " " would otherwise "succeed" after running nothing and writing an empty summary.
                if (ids.Length == 0) throw new ArgumentException("No benchmark profile id given (expected a comma-separated list)");
                return [.. ids.Select(id => BenchmarkProfiles.Find(id) ?? throw new ArgumentException($"Unknown benchmark profile: {id}"))];
            default:
                throw new ArgumentException($"Not a benchmark mode: {mode}");
        }
    }

    /// <summary>The report folder override: only the batch modes take it (as the third argument); <c>--benchmark</c> uses args[2] for profiles.</summary>
    public static string? ResolveOutput(string mode, IReadOnlyList<string> args) =>
        args.Count >= 3 && mode != "--benchmark" && !string.IsNullOrWhiteSpace(args[2]) ? args[2] : null;

    /// <summary>Parses a comma-separated width list such as <c>0,1920,2560</c> (0 = full size) using the invariant culture.</summary>
    public static int[] ParseWidths(string text)
    {
        var parts = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) throw new ArgumentException("At least one width is required");
        return [.. parts.Select(part =>
            int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var width)
                ? width
                : throw new ArgumentException($"Invalid width '{part}' (expected a non-negative whole number)"))];
    }

    /// <summary>Parses a positive whole number option (a file limit or count) using the invariant culture.</summary>
    public static int ParsePositiveInt(string text, string name) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : throw new ArgumentException($"Invalid {name} '{text}' (expected a positive whole number)");
}

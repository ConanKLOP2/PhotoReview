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

    /// <summary>Parses a comma-separated width list such as <c>0,1920,2560</c> (0 = full size) using the invariant culture. Duplicates are dropped (first occurrence kept): a repeated width would be measured and reported twice.</summary>
    public static int[] ParseWidths(string text)
    {
        var parts = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) throw new ArgumentException("At least one width is required");
        return [.. parts.Select(part =>
            int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var width)
                ? width
                : throw new ArgumentException($"Invalid width '{part}' (expected a non-negative whole number)")).Distinct()];
    }

    /// <summary>
    /// The optional <c>--rules &lt;file&gt;</c> of <c>--perf-analyze &lt;dir&gt;</c> (args[0] is the mode, args[1] the run dir). A dangling
    /// <c>--rules</c> or any other extra token is an error: silently falling back to the default thresholds would make the
    /// report look tuned when it is not.
    /// </summary>
    public static string? ParsePerfAnalyzeRules(IReadOnlyList<string> args)
    {
        string? rulesPath = null;
        for (var i = 2; i < args.Count; i++)
        {
            if (args[i] != "--rules") throw new ArgumentException($"Unexpected argument for --perf-analyze: {args[i]}");
            if (i + 1 >= args.Count || string.IsNullOrWhiteSpace(args[i + 1])) throw new ArgumentException("--rules needs a file path");
            rulesPath = args[++i];
        }
        return rulesPath;
    }

    /// <summary>Parses a positive whole number option (a file limit or count) using the invariant culture.</summary>
    public static int ParsePositiveInt(string text, string name) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : throw new ArgumentException($"Invalid {name} '{text}' (expected a positive whole number)");

    /// <summary>Parses a whole number in [<paramref name="min"/>, <paramref name="max"/>] using the invariant culture, digits only.</summary>
    public static int ParseIntInRange(string text, string name, int min, int max) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value >= min && value <= max
            ? value
            : throw new ArgumentException($"{name} must be a whole number {min}..{max} (got '{text}')");

    /// <summary>Parses a finite number greater than zero using the invariant culture; "Infinity" and "NaN" are rejected.</summary>
    public static double ParsePositiveFiniteDouble(string text, string name) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) && value > 0
            ? value
            : throw new ArgumentException($"{name} must be a finite number > 0 (got '{text}')");

    /// <summary>Parses an enum name (case-insensitive) and rejects numbers that name no member (<c>99</c>) as well as unknown names.</summary>
    public static TEnum ParseDefinedEnum<TEnum>(string text, string name) where TEnum : struct, Enum =>
        Enum.TryParse<TEnum>(text, true, out var value) && Enum.IsDefined(value)
            ? value
            : throw new ArgumentException($"invalid {name} '{text}' ({string.Join('|', Enum.GetNames<TEnum>())})");

    /// <summary>Failures every CLI mode reports as "message + exit code 2" instead of an unhandled-exception stack trace.</summary>
    public static bool IsExpectedToolFailure(Exception ex) =>
        ex is ArgumentException or IOException or InvalidOperationException or NotSupportedException or UnauthorizedAccessException
            or FormatException or InvalidDataException;

    /// <summary>
    /// <c>--benchmark &lt;folder&gt; [profiles]</c> takes at most one more argument; the batch modes take an optional output folder.
    /// A surplus token (<c>--benchmark-all folder out extra</c>) used to be ignored silently.
    /// </summary>
    public static void RejectSurplusBenchmarkArguments(IReadOnlyList<string> args)
    {
        if (args.Count > 3) throw new ArgumentException($"Unexpected argument for {args[0]}: {args[3]}");
    }

    /// <summary>
    /// Where the <c>--benchmark*</c> modes write their reports: the override, or a timestamped folder under the temp directory.
    /// An override that equals, contains or sits inside the photo folder is refused (T-B-03).
    /// </summary>
    public static string ResolveReportDirectory(string photoFolder, string? outputOverride)
    {
        var reportDirectory = outputOverride is { Length: > 0 }
            ? Path.GetFullPath(outputOverride)
            : Path.Combine(Path.GetTempPath(), "PhotoReview-Benchmark-Reports", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        ToolPathGuard.EnsureOutputDirectory(reportDirectory, photoFolder);
        return reportDirectory;
    }
}

using System.Globalization;
using System.Text;
using System.Text.Json;

namespace PhotoReview.Benchmark.Cli.PerfCompare;

/// <summary>
/// <c>compare &lt;baseline.json&gt; &lt;current.json&gt; [--threshold-percent 5] [--min-abs-ms 1] [--min-abs-mb 16] [--busy-cpu 60] [--max-cv 25]
/// [--min-samples 3] [--json out.json] [--markdown out.md]</c> (cũng nhận <c>--perf-compare</c>). Mã thoát: 0 PASS/WARN, 1 hồi quy chắc chắn,
/// 2 lỗi dùng/schema/scenario, 3 INCONCLUSIVE (máy bận, phương sai cao, thiếu chỉ số hoặc mẫu).
/// <c>--perf-export &lt;thư mục perf-session&gt; [--out file]</c> gom kết quả <c>--perf-session</c> thành một tệp schema chung.
/// </summary>
internal static class PerfCompareCommand
{
    public static bool IsCompare(IReadOnlyList<string> args) => args.Count >= 1 && args[0] is "compare" or "--perf-compare";

    public static bool IsExport(IReadOnlyList<string> args) => args.Count >= 1 && args[0] == "--perf-export";

    internal sealed record Parsed(string Baseline, string Current, PerfCompareOptions Options, string? JsonOut, string? MarkdownOut);

    public static Parsed Parse(IReadOnlyList<string> args)
    {
        var positional = new List<string>();
        var options = new PerfCompareOptions();
        string? jsonOut = null, markdownOut = null;
        for (var i = 1; i < args.Count; i++)
        {
            string Value() => i + 1 < args.Count ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
            double Number(string name, double min, double max)
            {
                var text = Value();
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) || !double.IsFinite(n) || n < min || n > max)
                {
                    throw new ArgumentException($"{name} must be a number in [{min}, {max}]: {text}");
                }

                return n;
            }

            switch (args[i])
            {
                case "--threshold-percent": options = options with { ThresholdPercent = Number("--threshold-percent", 0, 1000) }; break;
                case "--min-abs-ms": options = options with { MinAbsMs = Number("--min-abs-ms", 0, 1_000_000) }; break;
                case "--min-abs-mb": options = options with { MinAbsMb = Number("--min-abs-mb", 0, 1_000_000) }; break;
                case "--busy-cpu": options = options with { BusyCpuPercent = Number("--busy-cpu", 0, 100) }; break;
                case "--max-cv": options = options with { MaxCoefficientOfVariation = Number("--max-cv", 0, 1000) / 100.0 }; break;
                case "--min-samples": options = options with { MinSamples = (int)Number("--min-samples", 1, 1000) }; break;
                case "--json": jsonOut = Value(); break;
                case "--markdown": markdownOut = Value(); break;
                default:
                    if (args[i].StartsWith("--", StringComparison.Ordinal))
                    {
                        throw new ArgumentException("unknown argument: " + args[i]);
                    }

                    positional.Add(args[i]);
                    break;
            }
        }

        if (positional.Count != 2)
        {
            throw new ArgumentException("usage: compare <baseline.json> <current.json> [options]");
        }

        return new Parsed(positional[0], positional[1], options, jsonOut, markdownOut);
    }

    public static int RunCompare(IReadOnlyList<string> args, TextWriter output, TextWriter error)
    {
        try
        {
            var p = Parse(args);
            var baseline = PerfResultJson.Load(p.Baseline);
            var current = PerfResultJson.Load(p.Current);
            var result = PerfComparer.Compare(baseline, current, p.Options);
            var markdown = PerfComparer.ToMarkdown(result, $"{Path.GetFileName(p.Baseline)} ({baseline.Commit})", $"{Path.GetFileName(p.Current)} ({current.Commit})");
            output.WriteLine(markdown);
            output.WriteLine($"PERF-COMPARE: {PerfComparer.VerdictName(result.Overall)} (exit {result.ExitCode})");
            if (p.MarkdownOut is not null)
            {
                File.WriteAllText(p.MarkdownOut, markdown, new UTF8Encoding(false));
            }

            if (p.JsonOut is not null)
            {
                File.WriteAllText(p.JsonOut, ResultJson(result, baseline, current), new UTF8Encoding(false));
            }

            return result.ExitCode;
        }
        catch (Exception ex) when (ex is ArgumentException or PerfSchemaException or IOException)
        {
            error.WriteLine("PhotoReview.Benchmark.Cli: " + ex.Message);
            return 2;
        }
    }

    public static int RunExport(IReadOnlyList<string> args, TextWriter output, TextWriter error)
    {
        try
        {
            string? dir = null, outPath = null;
            for (var i = 1; i < args.Count; i++)
            {
                if (args[i] == "--out")
                {
                    outPath = i + 1 < args.Count ? args[++i] : throw new ArgumentException("--out needs a value");
                }
                else if (!args[i].StartsWith("--", StringComparison.Ordinal) && dir is null)
                {
                    dir = args[i];
                }
                else
                {
                    throw new ArgumentException("usage: --perf-export <perf-session dir> [--out file.json]");
                }
            }

            if (dir is null)
            {
                throw new ArgumentException("usage: --perf-export <perf-session dir> [--out file.json]");
            }

            var root = PerfEnvironmentProbe.FindRepoRoot();
            var result = PerfResultBuilder.FromPerfSessionDirectory(dir, PerfEnvironmentProbe.Capture(root));
            var path = outPath ?? DefaultPath(root, dir, result);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(path, PerfResultJson.Write(result), new UTF8Encoding(false));
            output.WriteLine($"PERF-JSON: {path} ({result.Metrics.Count} metrics, {result.Runs} runs)");
            return 0;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException or JsonException)
        {
            error.WriteLine("PhotoReview.Benchmark.Cli: " + ex.Message);
            return 2;
        }
    }

    /// <summary><c>&lt;repo&gt;/work/perf/&lt;scenario&gt;-&lt;máy&gt;-&lt;UTC&gt;.json</c> (thư mục bị .gitignore); ngoài repo thì cạnh thư mục nguồn.</summary>
    internal static string DefaultPath(string? repoRoot, string fallbackDirectory, PerfResult result)
    {
        var safeScenario = new string([.. result.Scenario.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-')]);
        var name = $"{safeScenario}-{result.Machine.Name}-{result.CreatedUtc.UtcDateTime:yyyyMMdd-HHmmss}.json";
        return repoRoot is null ? Path.Combine(fallbackDirectory, name) : Path.Combine(repoRoot, "work", "perf", name);
    }

    internal static string ResultJson(PerfComparisonResult result, PerfResult baseline, PerfResult current)
    {
        var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            void Number(string name, double v)
            {
                if (double.IsFinite(v)) w.WriteNumber(name, Math.Round(v, 3)); else w.WriteNull(name);
            }

            w.WriteStartObject();
            w.WriteString("scenario", baseline.Scenario);
            w.WriteString("overall", PerfComparer.VerdictName(result.Overall));
            w.WriteNumber("exitCode", result.ExitCode);
            w.WriteString("baselineCommit", baseline.Commit);
            w.WriteString("currentCommit", current.Commit);
            w.WriteStartArray("notes");
            foreach (var n in result.ContextNotes) w.WriteStringValue(n);
            w.WriteEndArray();
            w.WriteStartArray("metrics");
            foreach (var m in result.Metrics)
            {
                w.WriteStartObject();
                w.WriteString("name", m.Name);
                w.WriteString("unit", m.Unit);
                w.WriteString("verdict", PerfComparer.VerdictName(m.Verdict));
                w.WriteString("reason", m.Reason);
                w.WriteNumber("baseN", m.BaseN);
                w.WriteNumber("currentN", m.CurN);
                Number("baseMedian", m.BaseMedian);
                Number("currentMedian", m.CurMedian);
                Number("worse", m.Worse);
                Number("worsePercent", m.WorsePercent);
                Number("allowed", m.Allowed);
                Number("ciLower", m.LowerBound);
                Number("ciUpper", m.UpperBound);
                w.WriteBoolean("improved", m.Improved);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        return new UTF8Encoding(false).GetString(stream.ToArray()) + "\n";
    }
}

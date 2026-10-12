using System.Globalization;
using System.Text;
using System.Text.Json;
using PhotoReview.Benchmark.Cli.ShellHost;

namespace PhotoReview.Benchmark.Cli.PerfCompare;

/// <summary>Định danh + phiên bản schema của kết quả đo có cấu trúc (<c>work/perf/*.json</c>, <c>tools/perf-baselines/&lt;máy&gt;.json</c>).</summary>
internal static class PerfSchema
{
    public const string Name = "photoreview.perf-result";

    /// <summary>Tăng khi đổi KHÔNG tương thích ngược; bộ so sánh từ chối phiên bản khác (thà báo lỗi còn hơn so sánh sai).</summary>
    public const int Version = 1;

    public const string LowerIsBetter = "lowerIsBetter";
    public const string HigherIsBetter = "higherIsBetter";
}

/// <summary>Thông tin máy (để biết hai tệp có so sánh được không).</summary>
internal sealed record PerfMachine(string Name, string Cpu, int LogicalCores, double RamGb, string Os, string Gpu);

/// <summary>Tải nền khi đo: CPU toàn máy (%) và các cờ nhiễu (<c>busy-machine</c>, <c>failed-runs</c>, <c>forced-kill</c>).</summary>
internal sealed record PerfBackground(double CpuPercentMedian, double CpuPercentMax, int CpuSamples, IReadOnlyList<string> NoiseFlags);

/// <summary>
/// Một chỉ số: <paramref name="Samples"/> là mỗi lần chạy một giá trị (hoặc các giá trị gộp); tệp cũ chỉ có tóm tắt thì
/// <paramref name="SummaryP50"/> khác NaN và <paramref name="Samples"/> rỗng.
/// </summary>
internal sealed record PerfMetric(string Name, string Unit, string Direction, IReadOnlyList<double> Samples, double SummaryP50 = double.NaN)
{
    public bool LowerIsBetter => Direction == PerfSchema.LowerIsBetter;
}

internal sealed record PerfResult(
    int SchemaVersion, string Scenario, DateTimeOffset CreatedUtc, PerfMachine Machine, string Commit, bool Dirty, string AppVersion,
    int Runs, int WarmupRuns, IReadOnlyDictionary<string, string> Parameters, PerfBackground Background, IReadOnlyList<PerfMetric> Metrics);

/// <summary>Tệp không đọc được / sai schema / phiên bản không hỗ trợ (CLI trả mã 2).</summary>
internal sealed class PerfSchemaException(string message) : Exception(message);

internal static class PerfResultJson
{
    public static string Write(PerfResult result)
    {
        var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteString("schema", PerfSchema.Name);
            w.WriteNumber("schemaVersion", result.SchemaVersion);
            w.WriteString("scenario", result.Scenario);
            w.WriteString("createdUtc", result.CreatedUtc.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
            w.WriteStartObject("machine");
            w.WriteString("name", result.Machine.Name);
            w.WriteString("cpu", result.Machine.Cpu);
            w.WriteNumber("logicalCores", result.Machine.LogicalCores);
            Number(w, "ramGb", result.Machine.RamGb);
            w.WriteString("os", result.Machine.Os);
            w.WriteString("gpu", result.Machine.Gpu);
            w.WriteEndObject();
            w.WriteString("commit", result.Commit);
            w.WriteBoolean("dirty", result.Dirty);
            w.WriteString("appVersion", result.AppVersion);
            w.WriteNumber("runs", result.Runs);
            w.WriteNumber("warmupRuns", result.WarmupRuns);
            w.WriteStartObject("parameters");
            foreach (var (key, value) in result.Parameters.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                w.WriteString(key, value);
            }

            w.WriteEndObject();
            w.WriteStartObject("background");
            Number(w, "cpuPercentMedian", result.Background.CpuPercentMedian);
            Number(w, "cpuPercentMax", result.Background.CpuPercentMax);
            w.WriteNumber("cpuSamples", result.Background.CpuSamples);
            w.WriteStartArray("noiseFlags");
            foreach (var flag in result.Background.NoiseFlags)
            {
                w.WriteStringValue(flag);
            }

            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteStartArray("metrics");
            foreach (var m in result.Metrics.OrderBy(m => m.Name, StringComparer.Ordinal))
            {
                var finite = m.Samples.Where(double.IsFinite).ToArray();
                w.WriteStartObject();
                w.WriteString("name", m.Name);
                w.WriteString("unit", m.Unit);
                w.WriteString("direction", m.Direction);
                w.WriteNumber("n", finite.Length);
                Number(w, "p50", finite.Length > 0 ? ProbeStatistics.Median(finite) : m.SummaryP50);
                Number(w, "p95", ProbeStatistics.Percentile(finite, 95));
                Number(w, "min", finite.Length > 0 ? finite.Min() : double.NaN);
                Number(w, "max", finite.Length > 0 ? finite.Max() : double.NaN);
                Number(w, "mean", finite.Length > 0 ? finite.Average() : double.NaN);
                Number(w, "mad", finite.Length > 0 ? ProbeStatistics.Median([.. finite.Select(v => Math.Abs(v - ProbeStatistics.Median(finite)))]) : double.NaN);
                w.WriteStartArray("samples");
                foreach (var s in finite)
                {
                    w.WriteNumberValue(Math.Round(s, 3));
                }

                w.WriteEndArray();
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        return new UTF8Encoding(false).GetString(stream.ToArray()) + "\n";
    }

    private static void Number(Utf8JsonWriter w, string name, double value)
    {
        if (double.IsFinite(value))
        {
            w.WriteNumber(name, Math.Round(value, 3));
        }
        else
        {
            w.WriteNull(name);
        }
    }

    public static PerfResult Parse(string json, string sourceName = "perf result")
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException ex)
        {
            throw new PerfSchemaException($"{sourceName}: not valid JSON ({ex.Message})");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || Str(root, "schema") != PerfSchema.Name)
            {
                throw new PerfSchemaException($"{sourceName}: not a {PerfSchema.Name} file (missing \"schema\")");
            }

            var version = root.TryGetProperty("schemaVersion", out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var iv) ? iv : -1;
            if (version != PerfSchema.Version)
            {
                throw new PerfSchemaException($"{sourceName}: unsupported schemaVersion {(version < 0 ? "(missing)" : version.ToString(CultureInfo.InvariantCulture))}; this tool reads {PerfSchema.Version}. Re-measure to regenerate the file");
            }

            var scenario = Str(root, "scenario") ?? throw new PerfSchemaException($"{sourceName}: missing \"scenario\"");
            var created = DateTimeOffset.TryParse(Str(root, "createdUtc"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt) ? dt : DateTimeOffset.MinValue;
            var machine = new PerfMachine("", "", 0, double.NaN, "", "");
            if (root.TryGetProperty("machine", out var m) && m.ValueKind == JsonValueKind.Object)
            {
                machine = new PerfMachine(Str(m, "name") ?? "", Str(m, "cpu") ?? "", (int)Num(m, "logicalCores", 0), Num(m, "ramGb", double.NaN), Str(m, "os") ?? "", Str(m, "gpu") ?? "");
            }

            var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
            if (root.TryGetProperty("parameters", out var p) && p.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in p.EnumerateObject())
                {
                    parameters[prop.Name] = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() ?? "" : prop.Value.ToString();
                }
            }

            var background = new PerfBackground(double.NaN, double.NaN, 0, []);
            if (root.TryGetProperty("background", out var b) && b.ValueKind == JsonValueKind.Object)
            {
                var flags = new List<string>();
                if (b.TryGetProperty("noiseFlags", out var f) && f.ValueKind == JsonValueKind.Array)
                {
                    flags.AddRange(f.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!));
                }

                background = new PerfBackground(Num(b, "cpuPercentMedian", double.NaN), Num(b, "cpuPercentMax", double.NaN), (int)Num(b, "cpuSamples", 0), flags);
            }

            if (!root.TryGetProperty("metrics", out var metricsElement) || metricsElement.ValueKind != JsonValueKind.Array)
            {
                throw new PerfSchemaException($"{sourceName}: missing \"metrics\" array");
            }

            var metrics = new List<PerfMetric>();
            foreach (var e in metricsElement.EnumerateArray())
            {
                var name = Str(e, "name") ?? throw new PerfSchemaException($"{sourceName}: a metric has no \"name\"");
                var direction = Str(e, "direction") ?? PerfSchema.LowerIsBetter;
                if (direction != PerfSchema.LowerIsBetter && direction != PerfSchema.HigherIsBetter)
                {
                    throw new PerfSchemaException($"{sourceName}: metric {name}: unknown direction \"{direction}\"");
                }

                var samples = new List<double>();
                if (e.TryGetProperty("samples", out var s) && s.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in s.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.Number && item.TryGetDouble(out var d) && double.IsFinite(d))
                        {
                            samples.Add(d);
                        }
                    }
                }

                metrics.Add(new PerfMetric(name, Str(e, "unit") ?? "", direction, samples, Num(e, "p50", double.NaN)));
            }

            if (metrics.GroupBy(x => x.Name, StringComparer.Ordinal).Any(g => g.Count() > 1))
            {
                throw new PerfSchemaException($"{sourceName}: duplicate metric names");
            }

            return new PerfResult(version, scenario, created, machine, Str(root, "commit") ?? "", root.TryGetProperty("dirty", out var dirty) && dirty.ValueKind == JsonValueKind.True,
                Str(root, "appVersion") ?? "", (int)Num(root, "runs", 0), (int)Num(root, "warmupRuns", 0), parameters, background, metrics);
        }
    }

    public static PerfResult Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Perf result not found: " + path);
        }

        return Parse(File.ReadAllText(path), path);
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double Num(JsonElement e, string name, double fallback) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : fallback;
}

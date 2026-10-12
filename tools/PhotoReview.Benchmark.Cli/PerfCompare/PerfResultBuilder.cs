using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using PhotoReview.Benchmark.Cli.ShellHost;

namespace PhotoReview.Benchmark.Cli.PerfCompare;

/// <summary>Máy + commit khi đo. Tách khỏi builder để test truyền giá trị cố định.</summary>
internal sealed record PerfEnvironment(PerfMachine Machine, string Commit, bool Dirty, string AppVersion, DateTimeOffset CreatedUtc);

internal static class PerfEnvironmentProbe
{
    public static PerfEnvironment Capture(string? repoRoot)
    {
        var (commit, dirty) = ReadGit(repoRoot);
        return new PerfEnvironment(CaptureMachine(), commit, dirty, ReadAppVersion(), DateTimeOffset.UtcNow);
    }

    internal static PerfMachine CaptureMachine()
    {
        double ramGb = double.NaN;
        try
        {
            ramGb = Math.Round(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024.0 * 1024 * 1024), 1);
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException)
        {
        }

        return new PerfMachine(Environment.MachineName, RegistryString(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString") ?? "unknown",
            Environment.ProcessorCount, ramGb, System.Runtime.InteropServices.RuntimeInformation.OSDescription, ReadGpu());
    }

    private static string? RegistryString(string subKey, string value)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(subKey);
            return (key?.GetValue(value) as string)?.Trim();
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private static string ReadGpu()
    {
        const string classKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
        var names = new List<string>();
        for (var i = 0; i < 8; i++)
        {
            var name = RegistryString($@"{classKey}\{i:D4}", "DriverDesc");
            if (!string.IsNullOrWhiteSpace(name) && !names.Contains(name))
            {
                names.Add(name);
            }
        }

        return names.Count == 0 ? "unknown" : string.Join(" + ", names);
    }

    private static string ReadAppVersion() =>
        typeof(PerfEnvironmentProbe).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "unknown";

    /// <summary>Thư mục gốc repo (có <c>PhotoReview.slnx</c>) đi ngược từ thư mục chạy; null khi không thấy (chạy từ bản đã chép đi nơi khác).</summary>
    public static string? FindRepoRoot(string? start = null)
    {
        var dir = new DirectoryInfo(start ?? AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PhotoReview.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return null;
    }

    private static (string Commit, bool Dirty) ReadGit(string? root)
    {
        var envCommit = Environment.GetEnvironmentVariable("PHOTOREVIEW_COMMIT");
        if (root is null)
        {
            return (string.IsNullOrWhiteSpace(envCommit) ? "unknown" : envCommit.Trim(), false);
        }

        var sha = RunGit(root, "rev-parse", "--short=12", "HEAD");
        var status = RunGit(root, "status", "--porcelain", "--untracked-files=no");
        return (sha ?? (string.IsNullOrWhiteSpace(envCommit) ? "unknown" : envCommit.Trim()), !string.IsNullOrWhiteSpace(status));
    }

    private static string? RunGit(string root, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("-C");
            psi.ArgumentList.Add(root);
            foreach (var a in args)
            {
                psi.ArgumentList.Add(a);
            }

            using var process = Process.Start(psi);
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(10_000))
            {
                try { process.Kill(); } catch (InvalidOperationException) { }
                return null;
            }

            return process.ExitCode == 0 ? output.GetAwaiter().GetResult().Trim() : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }
}

/// <summary>Chuyển kết quả các verb đo hiện có sang <see cref="PerfResult"/> (schema versioned).</summary>
internal static class PerfResultBuilder
{
    private const string BusyFlag = "busy-machine";

    /// <summary>Probe khởi động WP-29: mỗi ứng dụng (<c>shell</c>/<c>wpf</c>) một nhóm chỉ số, mẫu = mỗi lần chạy (đã bỏ vòng khởi động và lần lỗi).</summary>
    public static PerfResult FromStartupProbe(ProbeOutcome outcome, ProbeOptions options, IReadOnlyDictionary<string, string> parameters,
        PerfEnvironment env, PerfCompareOptions? thresholds = null)
    {
        thresholds ??= new PerfCompareOptions();
        var metrics = new List<PerfMetric>();
        foreach (var app in options.Apps)
        {
            var good = outcome.Runs.Where(r => r.App == app.Id && !r.IsWarmup && r.Ok).ToArray();
            void Add(string name, string unit, IEnumerable<double> values)
            {
                var samples = values.Where(double.IsFinite).ToArray();
                if (samples.Length > 0)
                {
                    metrics.Add(new PerfMetric($"{app.Id}.{name}", unit, PerfSchema.LowerIsBetter, samples));
                }
            }

            Add("firstFrameMs", "ms", good.Select(r => r.FirstFrameMs));
            Add("windowShownMs", "ms", good.Select(r => r.WindowShownMs));
            Add("windowRevealedMs", "ms", good.Select(r => r.WindowRevealedMs));
            Add("keyToFrameCachedP50Ms", "ms", good.Where(r => r.KeyToFrameCachedMs.Count > 0).Select(r => ProbeStatistics.Percentile(r.KeyToFrameCachedMs, 50)));
            Add("keyToFrameCachedP95Ms", "ms", good.Where(r => r.KeyToFrameCachedMs.Count > 0).Select(r => ProbeStatistics.Percentile(r.KeyToFrameCachedMs, 95)));
            Add("peakWorkingSetMb", "MB", good.Select(r => r.PeakWorkingSetMb));
            Add("keysWithoutFrame", "count", good.Select(r => (double)r.KeysWithoutFrame));
        }

        var measured = outcome.Runs.Where(r => !r.IsWarmup).ToArray();
        var cpu = measured.Select(r => r.SystemCpuPercent).Where(double.IsFinite).ToArray();
        var flags = new List<string>();
        if (cpu.Length > 0 && ProbeStatistics.Median(cpu) > thresholds.BusyCpuPercent)
        {
            flags.Add(BusyFlag);
        }

        if (measured.Any(r => !r.Ok))
        {
            flags.Add("failed-runs");
        }

        if (measured.Any(r => r.ForcedKill))
        {
            flags.Add("forced-kill");
        }

        var background = new PerfBackground(
            cpu.Length > 0 ? ProbeStatistics.Median(cpu) : double.NaN, cpu.Length > 0 ? cpu.Max() : double.NaN, cpu.Length, flags);
        return new PerfResult(PerfSchema.Version, "startup-probe", env.CreatedUtc, env.Machine, env.Commit, env.Dirty, env.AppVersion,
            options.Rounds, options.WarmupRounds, parameters, background, metrics);
    }

    /// <summary>
    /// Thư mục kết quả của <c>--perf-session</c> (mỗi lần lặp một thư mục con có <c>process.json</c>, <c>session.json</c>, <c>metrics.json</c>).
    /// Mẫu = mỗi lần lặp; tải CPU máy không có trong đầu ra này nên <see cref="PerfBackground.CpuPercentMedian"/> là NaN.
    /// </summary>
    public static PerfResult FromPerfSessionDirectory(string directory, PerfEnvironment env)
    {
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException(directory);
        }

        var iterationDirs = Directory.EnumerateFiles(directory, "process.json", SearchOption.AllDirectories)
            .Select(p => Path.GetDirectoryName(p)!)
            .Where(d => File.Exists(Path.Combine(d, "session.json")))
            .OrderBy(d => d, StringComparer.Ordinal)
            .ToArray();
        if (iterationDirs.Length == 0)
        {
            throw new InvalidOperationException($"{directory}: no perf-session iteration folders (process.json + session.json) found");
        }

        var series = new SortedDictionary<string, (string Unit, List<double> Values)>(StringComparer.Ordinal);
        void Add(string name, string unit, double? value)
        {
            if (value is not { } v || !double.IsFinite(v))
            {
                return;
            }

            if (!series.TryGetValue(name, out var entry))
            {
                entry = (unit, []);
                series[name] = entry;
            }

            entry.Values.Add(v);
        }

        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        string scenario = "perf-session";
        var commit = env.Commit;
        foreach (var dir in iterationDirs)
        {
            using var process = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "process.json")));
            using var session = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "session.json")));
            var p = process.RootElement;
            var s = session.RootElement;
            Add("peakWorkingSetMb", "MB", Get(p, "peakWorkingSetBytes") / (1024.0 * 1024));
            Add("workingSetMb", "MB", Get(p, "workingSetBytes") / (1024.0 * 1024));
            Add("gcGen0Count", "count", Get(p, "gcCountDelta", "gen0"));
            Add("gcGen1Count", "count", Get(p, "gcCountDelta", "gen1"));
            Add("gcGen2Count", "count", Get(p, "gcCountDelta", "gen2"));
            Add("gcPauseMs", "ms", Get(p, "gcPauseDeltaMs"));
            Add("cpuMs", "ms", Get(p, "cpuDeltaMs"));
            Add("elapsedMs", "ms", Get(p, "elapsedMs"));
            Add("keySettleP50Ms", "ms", Get(s, "keySettle", "p50Ms"));
            Add("keySettleP95Ms", "ms", Get(s, "keySettle", "p95Ms"));
            Add("keyDispatchP95Ms", "ms", Get(s, "keyDispatch", "p95Ms"));
            Add("keyUiBusyP95Ms", "ms", Get(s, "keyUiBusy", "p95Ms"));
            var metricsPath = Path.Combine(dir, "metrics.json");
            if (File.Exists(metricsPath))
            {
                using var metrics = JsonDocument.Parse(File.ReadAllText(metricsPath));
                Add("sourceReads", "count", Get(metrics.RootElement, "sourceReads"));
                Add("sourceBytesReadMb", "MB", Get(metrics.RootElement, "sourceBytesRead") / (1024.0 * 1024));
                Add("decodeMs", "ms", Get(metrics.RootElement, "decodeMilliseconds"));
                Add("cacheMisses", "count", Get(metrics.RootElement, "cacheMisses"));
                Add("presentMs", "ms", Get(metrics.RootElement, "presentMilliseconds"));
            }

            if (s.TryGetProperty("scenario", out var sc) && sc.ValueKind == JsonValueKind.String)
            {
                scenario = "perf-session:" + sc.GetString();
            }

            foreach (var key in new[] { "mode", "folderAlias", "openedImageCount", "scenarioFile" })
            {
                if (s.TryGetProperty(key, out var v) && v.ValueKind is JsonValueKind.String or JsonValueKind.Number)
                {
                    parameters[key] = v.ToString();
                }
            }

            if (s.TryGetProperty("commit", out var c) && c.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(c.GetString()))
            {
                commit = c.GetString()!;
            }
        }

        parameters["iterations"] = iterationDirs.Length.ToString(CultureInfo.InvariantCulture);
        var list = series.Select(kv => new PerfMetric(kv.Key, kv.Value.Unit, PerfSchema.LowerIsBetter, kv.Value.Values)).ToArray();
        return new PerfResult(PerfSchema.Version, scenario, env.CreatedUtc, env.Machine, commit, env.Dirty, env.AppVersion,
            iterationDirs.Length, 0, parameters, new PerfBackground(double.NaN, double.NaN, 0, []), list);
    }

    private static double? Get(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var name in path)
        {
            // Tên thuộc tính của <c>metrics.json</c> là PascalCase hay camelCase tuỳ serializer: so không phân biệt hoa thường.
            var found = false;
            if (current.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in current.EnumerateObject())
                {
                    if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        current = prop.Value;
                        found = true;
                        break;
                    }
                }
            }

            if (!found)
            {
                return null;
            }
        }

        return current.ValueKind == JsonValueKind.Number && current.TryGetDouble(out var d) ? d : null;
    }
}

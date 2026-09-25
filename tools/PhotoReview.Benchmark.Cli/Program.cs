using PhotoReview.Benchmark.Cli;
using PhotoReview.Benchmarking;
using PhotoReview.App;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Caching;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.Model;
using PhotoReview.PerfAnalysis;
using System.IO;
using System.Windows.Media;
using System.Globalization;
static async Task RunCliBenchmarksAsync(string folder, IReadOnlyList<BenchmarkProfile> profiles, string? outputOverride)
{
    if (!Directory.Exists(folder)) throw new DirectoryNotFoundException(folder);
    var reportDirectory = outputOverride is { Length: > 0 } ? Path.GetFullPath(outputOverride) : Path.Combine(Path.GetTempPath(), "PhotoReview-Benchmark-Reports", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
    Directory.CreateDirectory(reportDirectory);
    var supported = ImageFileTypes.SupportedExtensions;
    var allFiles = Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly)
        .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ThenBy(p => p, StringComparer.Ordinal)
        .ToArray();
    var unsupportedExtensions = allFiles
        .Where(p => !supported.Contains(Path.GetExtension(p)))
        .GroupBy(p => string.IsNullOrWhiteSpace(Path.GetExtension(p)) ? "<none>" : Path.GetExtension(p).ToLowerInvariant(), StringComparer.Ordinal)
        .OrderBy(g => g.Key, StringComparer.Ordinal)
        .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
    var files = allFiles.Where(p => supported.Contains(Path.GetExtension(p))).Take(64).ToArray();
    if (files.Length == 0) throw new InvalidOperationException("Benchmark folder contains no supported images");
    var totalSourceBytes = files.Sum(path => { try { return new FileInfo(path).Length; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0L; } });
    var reports = new List<BenchmarkReport>();
    var outcomes = new List<BenchmarkPhaseResult>();
    var manifest = new BenchmarkDatasetManifest(Path.GetFullPath(folder),
        files.Select(Path.GetFullPath).ToArray(), unsupportedExtensions, 64);
    var anyFailed = false;
    var recycleBin = BenchmarkRecycleBin.Create(() => PhotoReview.Platform.Windows.WindowsRecycleBin.Instance);
    foreach (var profile in profiles)
    {
        Console.WriteLine($"START profile={profile.Id} workload={profile.Workload} mode={profile.LoadingMode} workers={profile.Workers} window={profile.NextWindow}/{profile.PreviousWindow}");
        await using var imageExecutor = new BenchmarkImageExecutor(profile, files, totalSourceBytes);
        var random = BenchmarkWorkloadRunner.CreateSeededRandom(profile.Id);
        // Same profile application as the WPF benchmark window: logging-on/logging-off must differ (PERF-01).
        using var loggingScope = BenchmarkProfileScope.ApplyLogging(profile, () => AppLog.Enabled, enabled => AppLog.Enabled = enabled);
        try
        {
            // Reuses the WPF benchmark workload so CLI profiles exercise their real behavior, with the same
            // setup/measure split (R2-F-14): scratch copies and cold-cache eviction stay out of the samples.
            var report = await BenchmarkWorkloadRunner.RunProfileAsync(folder, profile, imageExecutor, files, random, recycleBin,
                new Progress<BenchmarkProgress>(p => Console.WriteLine($"  {p.ProfileId}: {p.Completed}/{p.Total} {p.Message}")));
            reports.Add(report);
            outcomes.Add(report.Phases[0]);
            var pathOut = Path.Combine(reportDirectory, $"{profile.Id}-{report.RunId}.json"); await File.WriteAllTextAsync(pathOut, report.ToJson());
            var phase = report.Phases[0];
            if (phase.Status == BenchmarkResultStatus.Fail) anyFailed = true;
            Console.WriteLine($"DONE profile={profile.Id} status={phase.Status} p50={phase.P50:F1}ms p95={phase.P95:F1}ms max={phase.Max:F1}ms report={pathOut}");
        }
        catch (Exception ex)
        {
            // Report this profile's failure and continue the batch, as the WPF window does.
            anyFailed = true;
            var failed = new BenchmarkPhaseResult(profile.Id, profile.Workload, [], BenchmarkResultStatus.Fail,
                $"Profile failed before producing samples: {ex.GetType().Name}: {ex.Message}");
            outcomes.Add(failed);
            var failedReport = new BenchmarkReport(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow,
                Path.GetFullPath(folder), [failed], Machine: Environment.MachineName);
            reports.Add(failedReport);
            var failedPath = Path.Combine(reportDirectory, $"{profile.Id}-{failedReport.RunId}.json");
            await File.WriteAllTextAsync(failedPath, failedReport.ToJson());
            Console.Error.WriteLine($"FAIL profile={profile.Id} error={ex.Message}");
        }
    }
    var summary = Path.Combine(reportDirectory, "summary.json");
    var batch = new BenchmarkBatchSummary(manifest, reports, outcomes);
    await File.WriteAllTextAsync(summary, batch.ToJson());
    Console.WriteLine($"REPORT: {summary}");
    if (anyFailed) Environment.ExitCode = 1;
}
if (args.Length == 1 && args[0] == "--benchmark-list-profiles")
{
    foreach (var profile in BenchmarkProfiles.All)
        Console.WriteLine($"{profile.Id}\t{profile.Name}\t{profile.Workload}\tmode={profile.LoadingMode}\tworkers={profile.Workers}\titerations={profile.Iterations}\tcorrectnessOnly={profile.CorrectnessOnly}\t{profile.Description}");
    return;
}

if (args.Length >= 2 && (args[0] == "--benchmark" || args[0] == "--benchmark-all" || args[0] == "--benchmark-actions"))
{
    var benchmarkFolder = args[1];
    IReadOnlyList<BenchmarkProfile> requested;
    try { requested = BenchmarkCliArguments.ResolveProfiles(args[0], args); }
    catch (ArgumentException ex)
    {
        Console.Error.WriteLine($"PhotoReview.Benchmark.Cli: {ex.Message}");
        Environment.ExitCode = 2;
        return;
    }
    var output = BenchmarkCliArguments.ResolveOutput(args[0], args);
    await RunCliBenchmarksAsync(benchmarkFolder, requested, output);
    return;
}
if (args.Length is 2 or 4 && args[0] == "--ui-next-probe" && (args.Length == 2 || args[2] == "--cache-dir"))
{
    await LocalUiNextProbe.RunAsync(args[1], args.Length == 4 ? args[3] : null);
    return;
}

if (args.Length >= 1 && args[0] == "--perf-session")
{
    Environment.ExitCode = await PerfSession.RunAsync(args);
    return;
}

if ((args.Length == 2 || args.Length == 3) && args[0] == "--preload-bench")
{
    int workers;
    try { workers = args.Length == 3 ? BenchmarkCliArguments.ParsePositiveInt(args[2], "worker count") : 8; }
    catch (ArgumentException ex)
    {
        Console.Error.WriteLine($"PhotoReview.Benchmark.Cli: {ex.Message}");
        Environment.ExitCode = 2;
        return;
    }
    await LocalImageBenchmark.RunAsync(args[1], workers);
    return;
}

if (args.Length >= 2 && args[0] == "--perf-analyze")
{
    PerfAnalyze.AnalysisResult analysis;
    try
    {
        var rulesPath = BenchmarkCliArguments.ParsePerfAnalyzeRules(args);
        analysis = await PerfAnalyze.RunAsync(args[1], rulesPath);
    }
    catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException or System.Text.Json.JsonException)
    {
        Console.Error.WriteLine($"PhotoReview.Benchmark.Cli: {ex.Message}");
        Environment.ExitCode = 2;
        return;
    }
    Console.WriteLine($"PERF-ANALYZE: {analysis.CsvFileCount} file, {analysis.Groups.Count} groups");
    Console.WriteLine($"REPORT: {analysis.SummaryMdPath}");
    Console.WriteLine($"REPORT: {analysis.SummaryJsonPath}");
    return;
}

if (args.Length is >= 3 and <= 5 && args[0] == "--io-decode-split")
{
    int[] ioWidths;
    int ioMax;
    try
    {
        ioWidths = args.Length >= 4 ? BenchmarkCliArguments.ParseWidths(args[3]) : [0, 1920, 2560, 3840];
        ioMax = args.Length >= 5 ? BenchmarkCliArguments.ParsePositiveInt(args[4], "file limit") : 60;
    }
    catch (ArgumentException ex)
    {
        Console.Error.WriteLine($"PhotoReview.Benchmark.Cli: {ex.Message}");
        Environment.ExitCode = 2;
        return;
    }
    await IoDecodeSplit.RunAsync(args[1], args[2], ioWidths, ioMax);
    return;
}

if (args.Length is >= 3 and <= 6 && args[0] == "--decoder-bench")
{
    Environment.ExitCode = await DecoderBenchmark.RunAsync(args);
    return;
}

if (args.Length == 2 && args[0] == "--explorer-probe")
{
    var probe = await new ExplorerOrderService().TryGetSnapshotAsync(args[1], TimeSpan.FromSeconds(5), CancellationToken.None);
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(probe, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    var scanned = Directory.EnumerateFiles(args[1]).Where(ImageFileTypes.IsSupported).ToArray();
    var accepted = ExplorerSnapshotValidator.TryValidate(probe, scanned, out var ordered, out var reason);
    Console.WriteLine($"VALIDATOR: accepted={accepted}, scanned={scanned.Length}, ordered={ordered.Count}, reason={reason ?? "none"}");
    return;
}

Console.Error.WriteLine("PhotoReview.Benchmark.Cli: unknown or missing mode. Supported modes:");
foreach (var mode in new[]
{
    "--benchmark-list-profiles", "--benchmark <folder> <profile,...>", "--benchmark-all <folder> [output]", "--benchmark-actions <folder> [output]",
    "--ui-next-probe <folder> [--cache-dir DIR]", "--perf-session ...", "--preload-bench <folder> [n]", "--perf-analyze <dir> [--rules <file>]",
    "--io-decode-split ...", "--decoder-bench ...", "--explorer-probe <folder>",
})
    Console.Error.WriteLine("  " + mode);
Environment.ExitCode = 2;

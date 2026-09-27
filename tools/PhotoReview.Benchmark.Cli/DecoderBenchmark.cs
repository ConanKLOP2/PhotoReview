using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Model;
using PhotoReview.Imaging.Decoding;

namespace PhotoReview.Benchmark.Cli;

/// <summary>
/// CLI implementation of task T81: <c>--decoder-bench</c>.
/// Measures and compares image decoding performance (P50, P95, max, throughput, memory)
/// across different decoder backends (e.g. Wpf, WicDirect, TurboJpeg) and target widths.
/// </summary>
public static class DecoderBenchmark
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public sealed class BenchmarkRecord
    {
        public string FilePath { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public int FileIndex { get; set; }
        public int Iteration { get; set; }
        public int TargetWidth { get; set; }
        public string RequestedBackend { get; set; } = string.Empty;
        public string ActualBackend { get; set; } = string.Empty;
        public double DecodeMs { get; set; }
        public long AllocatedBytes { get; set; }
        public int PixelWidth { get; set; }
        public int PixelHeight { get; set; }
        public bool Success { get; set; }
        public string? Error { get; set; }
    }

    public sealed class GroupStatistics
    {
        public string Backend { get; set; } = string.Empty;
        public int TargetWidth { get; set; }
        public int TotalRuns { get; set; }
        public int SuccessCount { get; set; }
        public int FailureCount { get; set; }
        public string? FirstError { get; set; }
        public int FallbackCount { get; set; }
        public double MeanMs { get; set; }
        public double P50Ms { get; set; }
        public double P95Ms { get; set; }
        public double MinMs { get; set; }
        public double MaxMs { get; set; }
        public double StdDevMs { get; set; }
        public double ThroughputMegapixelsPerSec { get; set; }
        public long AvgAllocatedBytes { get; set; }
        public double SpeedupVsWpf { get; set; }
    }

    public sealed class BenchmarkSummary
    {
        public DateTime TimestampUtc { get; set; }
        public string MachineName { get; set; } = string.Empty;
        public string OsVersion { get; set; } = string.Empty;
        public int ProcessorCount { get; set; }
        public string SourceFolder { get; set; } = string.Empty;
        public int ImageCount { get; set; }
        public int Iterations { get; set; }
        public List<string> TestedBackends { get; set; } = new();
        public List<int> TestedWidths { get; set; } = new();
        public string CacheCondition { get; set; } = "Warm OS cache (interleaved by file)";
        public int TotalRuns { get; set; }
        public int TotalFailures { get; set; }
        public List<GroupStatistics> Groups { get; set; } = new();
    }

    /// <returns>0 on success; 1 when usage is wrong or no decode succeeded at all (so a folder of undecodable files never looks like a pass).</returns>
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: --decoder-bench <folder> <outDir> [backends=Wpf,WicDirect,TurboJpeg] [widths=0,1920,2560,3840] [iterations=5]");
            return 1;
        }

        var folder = Path.GetFullPath(args[1]);
        var outDir = Path.GetFullPath(args[2]);

        if (!Directory.Exists(folder))
        {
            throw new DirectoryNotFoundException($"Image directory not found: {folder}");
        }

        Directory.CreateDirectory(outDir);

        // Parse backends (defaults to Wpf,WicDirect,TurboJpeg)
        var requestedBackendNames = args.Length >= 4
            ? args[3].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : new[] { "Wpf", "WicDirect", "TurboJpeg" };

        // Parse widths (defaults to 0, 1920, 2560, 3840)
        var widths = args.Length >= 5
            ? args[4].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToArray()
            : new[] { 0, 1920, 2560, 3840 };

        // Parse iterations (defaults to 5)
        var iterations = args.Length >= 6 ? Math.Max(1, int.Parse(args[5], CultureInfo.InvariantCulture)) : 5;

        // Resolve available decoders
        var activeDecoders = new List<(DecoderBackend Backend, IImageDecoder Decoder)>();

        foreach (var name in requestedBackendNames)
        {
            if (Enum.TryParse<DecoderBackend>(name, ignoreCase: true, out var backend))
            {
                IImageDecoder? decoder = backend switch
                {
                    DecoderBackend.Wpf => new WpfBitmapImageDecoder(),
                    DecoderBackend.WicDirect => new PhotoReview.Imaging.Decoding.Wic.WicDirectDecoder(),
                    DecoderBackend.TurboJpeg => new PhotoReview.Imaging.TurboJpeg.TurboJpegDecoder(),
                    _ => null
                };

                if (decoder is not null)
                {
                    activeDecoders.Add((backend, decoder));
                    Console.WriteLine($"[DEC-BENCH] Enabled decoder backend: {backend}");
                }
                else
                {
                    Console.WriteLine($"[DEC-BENCH] Skipped decoder backend: {name} (not implemented yet)");
                }
            }
            else
            {
                Console.WriteLine($"[DEC-BENCH] Unknown decoder backend name: {name}");
            }
        }

        if (activeDecoders.Count == 0)
        {
            throw new InvalidOperationException("No valid decoder backends available to benchmark.");
        }

        // Gather image files
        var files = Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly)
            .Where(ImageFileTypes.IsSupported)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (files.Length == 0)
        {
            throw new InvalidOperationException($"No supported image files found in: {folder}");
        }

        Console.WriteLine($"[DEC-BENCH] Found {files.Length} images in {folder}");
        Console.WriteLine($"[DEC-BENCH] Testing backends: {string.Join(", ", activeDecoders.Select(d => d.Backend))}");
        Console.WriteLine($"[DEC-BENCH] Testing widths: {string.Join(", ", widths)} (0 = full original)");
        Console.WriteLine($"[DEC-BENCH] Iterations per file: {iterations}");
        Console.WriteLine($"[DEC-BENCH] Output directory: {outDir}");

        // Warm-up pass on all files to ensure warm OS file system cache
        Console.Write("[DEC-BENCH] Warming up OS file cache across all files... ");
        var defaultDecoder = activeDecoders[0].Decoder;
        foreach (var file in files)
        {
            try
            {
                _ = defaultDecoder.Decode(new DecodeRequest(file, TargetWidth: 0, ApplyOrientation: false));
            }
            catch
            {
                // Ignore warmup errors
            }
        }
        Console.WriteLine("Done.");

        // Interleaved benchmark loop
        var records = new List<BenchmarkRecord>();
        var totalDecodes = files.Length * iterations * widths.Length * activeDecoders.Count;
        var completedDecodes = 0;
        var stopwatch = Stopwatch.StartNew();

        Console.WriteLine($"[DEC-BENCH] Running {totalDecodes} total decodes (interleaved per file)...");

        for (var fIdx = 0; fIdx < files.Length; fIdx++)
        {
            var file = files[fIdx];
            var fileName = Path.GetFileName(file);

            for (var iter = 0; iter < iterations; iter++)
            {
                foreach (var width in widths)
                {
                    foreach (var (backend, decoder) in activeDecoders)
                    {
                        // Clean memory state before timing
                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                        GC.Collect();

                        var startAlloc = GC.GetTotalAllocatedBytes(precise: true);
                        var sw = Stopwatch.StartNew();

                        IDecodedImage? decoded = null;
                        Exception? caughtEx = null;

                        try
                        {
                            decoded = decoder.Decode(new DecodeRequest(file, TargetWidth: width, ApplyOrientation: true));
                        }
                        catch (Exception ex)
                        {
                            caughtEx = ex;
                        }

                        sw.Stop();
                        var endAlloc = GC.GetTotalAllocatedBytes(precise: true);

                        var record = new BenchmarkRecord
                        {
                            FilePath = file,
                            FileName = fileName,
                            FileIndex = fIdx,
                            Iteration = iter,
                            TargetWidth = width,
                            RequestedBackend = backend.ToString(),
                            ActualBackend = decoded?.ActualBackend.ToString() ?? "None",
                            DecodeMs = sw.Elapsed.TotalMilliseconds,
                            AllocatedBytes = Math.Max(0, endAlloc - startAlloc),
                            PixelWidth = decoded?.PixelWidth ?? 0,
                            PixelHeight = decoded?.PixelHeight ?? 0,
                            Success = decoded != null,
                            Error = caughtEx?.Message
                        };

                        records.Add(record);
                        completedDecodes++;

                        if (completedDecodes % Math.Max(1, totalDecodes / 10) == 0 || completedDecodes == totalDecodes)
                        {
                            var pct = (double)completedDecodes / totalDecodes * 100.0;
                            var elapsedSec = stopwatch.Elapsed.TotalSeconds;
                            var decodesPerSec = completedDecodes / Math.Max(0.001, elapsedSec);
                            Console.WriteLine(FormattableString.Invariant($"  [{pct:F1}%] {completedDecodes}/{totalDecodes} decodes done ({decodesPerSec:F1} decodes/s)"));
                        }
                    }
                }
            }
        }

        stopwatch.Stop();
        Console.WriteLine(FormattableString.Invariant($"[DEC-BENCH] Benchmark completed in {stopwatch.Elapsed.TotalSeconds:F1}s."));

        // Compute group statistics
        var groupStats = new List<GroupStatistics>();
        var grouped = records.GroupBy(r => (r.RequestedBackend, r.TargetWidth));

        foreach (var g in grouped)
        {
            var valid = g.Where(r => r.Success).ToList();
            var failed = g.Where(r => !r.Success).ToList();
            if (valid.Count == 0)
            {
                // Keep all-failed groups in the report (zeroed timings): silently dropping them made a folder
                // of undecodable files produce an empty, apparently successful summary.
                groupStats.Add(new GroupStatistics
                {
                    Backend = g.Key.RequestedBackend,
                    TargetWidth = g.Key.TargetWidth,
                    TotalRuns = g.Count(),
                    SuccessCount = 0,
                    FailureCount = failed.Count,
                    FirstError = failed.Select(r => r.Error).FirstOrDefault(e => !string.IsNullOrEmpty(e)),
                });
                continue;
            }

            var times = valid.Select(r => r.DecodeMs).OrderBy(t => t).ToList();
            var mean = times.Average();
            var p50 = GetPercentile(times, 0.50);
            var p95 = GetPercentile(times, 0.95);
            var min = times[0];
            var max = times[^1];
            var variance = times.Select(t => Math.Pow(t - mean, 2)).Average();
            var stdDev = Math.Sqrt(variance);

            var totalMegapixels = valid.Sum(r => (long)r.PixelWidth * r.PixelHeight) / 1_000_000.0;
            var totalTimeSec = times.Sum() / 1000.0;
            var throughput = totalTimeSec > 0 ? totalMegapixels / totalTimeSec : 0.0;
            var avgAlloc = (long)valid.Select(r => r.AllocatedBytes).Average();
            var fallbacks = valid.Count(r => !string.Equals(r.RequestedBackend, r.ActualBackend, StringComparison.OrdinalIgnoreCase));

            groupStats.Add(new GroupStatistics
            {
                Backend = g.Key.RequestedBackend,
                TargetWidth = g.Key.TargetWidth,
                TotalRuns = g.Count(),
                SuccessCount = valid.Count,
                FailureCount = failed.Count,
                FirstError = failed.Select(r => r.Error).FirstOrDefault(e => !string.IsNullOrEmpty(e)),
                FallbackCount = fallbacks,
                MeanMs = mean,
                P50Ms = p50,
                P95Ms = p95,
                MinMs = min,
                MaxMs = max,
                StdDevMs = stdDev,
                ThroughputMegapixelsPerSec = throughput,
                AvgAllocatedBytes = avgAlloc
            });
        }

        // Calculate speedup vs Wpf
        foreach (var width in widths)
        {
            var wpfStat = groupStats.FirstOrDefault(s => s.Backend == "Wpf" && s.TargetWidth == width);
            if (wpfStat is { P50Ms: > 0 })
            {
                foreach (var stat in groupStats.Where(s => s.TargetWidth == width))
                {
                    stat.SpeedupVsWpf = wpfStat.P50Ms / stat.P50Ms;
                }
            }
        }

        // Prepare Summary
        var summary = new BenchmarkSummary
        {
            TimestampUtc = DateTime.UtcNow,
            MachineName = Environment.MachineName,
            OsVersion = Environment.OSVersion.VersionString,
            ProcessorCount = Environment.ProcessorCount,
            SourceFolder = folder,
            ImageCount = files.Length,
            Iterations = iterations,
            TestedBackends = activeDecoders.Select(d => d.Backend.ToString()).ToList(),
            TestedWidths = widths.ToList(),
            CacheCondition = "Warm OS file-system cache (interleaved per file)",
            TotalRuns = records.Count,
            TotalFailures = records.Count(r => !r.Success),
            Groups = groupStats.OrderBy(s => s.TargetWidth).ThenBy(s => s.Backend).ToList()
        };

        // Write outputs
        var summaryJsonPath = Path.Combine(outDir, "summary.json");
        await File.WriteAllTextAsync(summaryJsonPath, JsonSerializer.Serialize(summary, JsonOptions));

        var summaryMdPath = Path.Combine(outDir, "summary.md");
        await File.WriteAllTextAsync(summaryMdPath, GenerateMarkdownReport(summary));

        var detailsCsvPath = Path.Combine(outDir, "details.csv");
        await WriteDetailsCsvAsync(detailsCsvPath, records);

        // Console Output
        Console.WriteLine();
        Console.WriteLine("==================================== BENCHMARK RESULTS ====================================");
        PrintConsoleSummaryTable(summary);
        Console.WriteLine("===========================================================================================");
        Console.WriteLine($"[DEC-BENCH] Markdown Report: {summaryMdPath}");
        Console.WriteLine($"[DEC-BENCH] JSON Summary:    {summaryJsonPath}");
        Console.WriteLine($"[DEC-BENCH] Raw Details CSV: {detailsCsvPath}");

        Console.WriteLine($"[DEC-BENCH] Decodes: {summary.TotalRuns - summary.TotalFailures} succeeded, {summary.TotalFailures} failed of {summary.TotalRuns}.");
        if (summary.TotalRuns - summary.TotalFailures == 0)
        {
            Console.Error.WriteLine("[DEC-BENCH] FAIL: no decode succeeded; the results above are not a valid benchmark.");
            return 1;
        }
        return 0;
    }

    private static double GetPercentile(List<double> sorted, double percentile)
    {
        if (sorted.Count == 0) return 0;
        if (sorted.Count == 1) return sorted[0];

        var index = percentile * (sorted.Count - 1);
        var lower = (int)Math.Floor(index);
        var upper = (int)Math.Ceiling(index);
        if (lower == upper) return sorted[lower];

        var fraction = index - lower;
        return sorted[lower] * (1 - fraction) + sorted[upper] * fraction;
    }

    internal static string GenerateMarkdownReport(BenchmarkSummary summary)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# PhotoReview — Decoder Benchmark Report");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **Timestamp:** {summary.TimestampUtc:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **Machine:** {summary.MachineName} ({summary.ProcessorCount} cores, {summary.OsVersion})");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **Source Folder:** `{summary.SourceFolder}` ({summary.ImageCount} images)");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **Iterations:** {summary.Iterations} iterations per image");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **Condition:** {summary.CacheCondition}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **Failed decodes:** {summary.TotalFailures} of {summary.TotalRuns}");
        sb.AppendLine();
        sb.AppendLine("## Summary Table");
        sb.AppendLine();
        sb.AppendLine("| Backend | Target Width | Failures | P50 (ms) | P95 (ms) | Mean (ms) | Max (ms) | Throughput (MP/s) | Avg Alloc | Speedup vs Wpf |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|");

        foreach (var g in summary.Groups)
        {
            var widthStr = g.TargetWidth == 0 ? "Full (0)" : g.TargetWidth.ToString(CultureInfo.InvariantCulture);
            var allocStr = FormatBytes(g.AvgAllocatedBytes);
            var speedupStr = FormatSpeedup(g.SpeedupVsWpf);

            sb.AppendLine(CultureInfo.InvariantCulture, $"| **{g.Backend}** | {widthStr} | {g.FailureCount}/{g.TotalRuns} | {g.P50Ms:F2} | {g.P95Ms:F2} | {g.MeanMs:F2} | {g.MaxMs:F2} | {g.ThroughputMegapixelsPerSec:F1} | {allocStr} | **{speedupStr}** |");
        }

        sb.AppendLine();
        sb.AppendLine("## Analysis & Observations");
        sb.AppendLine();

        foreach (var width in summary.TestedWidths)
        {
            var wStr = width == 0 ? "Full resolution (Original)" : $"{width}px target width";
            sb.AppendLine(CultureInfo.InvariantCulture, $"### Width: {wStr}");

            var statsForWidth = summary.Groups.Where(g => g.TargetWidth == width && g.SuccessCount > 0).OrderBy(g => g.P50Ms).ToList();
            if (statsForWidth.Count >= 2)
            {
                var fastest = statsForWidth[0];
                var baseline = statsForWidth.FirstOrDefault(g => g.Backend == "Wpf") ?? statsForWidth[^1];
                var diffPct = (baseline.P50Ms - fastest.P50Ms) / baseline.P50Ms * 100.0;

                sb.AppendLine(CultureInfo.InvariantCulture, $"- **Fastest backend:** `{fastest.Backend}` (P50: {fastest.P50Ms:F2} ms, Throughput: {fastest.ThroughputMegapixelsPerSec:F1} MP/s).");
                if (fastest.Backend != baseline.Backend)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"- **Improvement over {baseline.Backend}:** {diffPct:F1}% faster ({baseline.P50Ms / fastest.P50Ms:F2}x speedup).");
                }
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static void PrintConsoleSummaryTable(BenchmarkSummary summary)
    {
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "{0,-12} | {1,-12} | {8,9} | {2,10} | {3,10} | {4,10} | {5,14} | {6,12} | {7,14}",
            "Backend", "Target Width", "P50 (ms)", "P95 (ms)", "Max (ms)", "Throughput", "Avg Alloc", "Speedup vs Wpf", "Failures"));
        Console.WriteLine(new string('-', 114));

        foreach (var g in summary.Groups)
        {
            var widthStr = g.TargetWidth == 0 ? "Full (0)" : g.TargetWidth.ToString(CultureInfo.InvariantCulture);
            var allocStr = FormatBytes(g.AvgAllocatedBytes);
            var tpStr = string.Create(CultureInfo.InvariantCulture, $"{g.ThroughputMegapixelsPerSec:F1} MP/s");
            var speedupStr = FormatSpeedup(g.SpeedupVsWpf);

            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "{0,-12} | {1,-12} | {8,9} | {2,10:F2} | {3,10:F2} | {4,10:F2} | {5,14} | {6,12} | {7,14}",
                g.Backend, widthStr, g.P50Ms, g.P95Ms, g.MaxMs, tpStr, allocStr, speedupStr, $"{g.FailureCount}/{g.TotalRuns}"));
            if (g.FailureCount > 0 && g.FirstError is not null) Console.WriteLine($"    first error: {g.FirstError}");
        }
    }

    private static async Task WriteDetailsCsvAsync(string path, IReadOnlyList<BenchmarkRecord> records)
    {
        var sb = new StringBuilder();
        sb.AppendLine("FileIndex,FileName,Iteration,TargetWidth,RequestedBackend,ActualBackend,DecodeMs,AllocatedBytes,PixelWidth,PixelHeight,Success,Error");

        foreach (var r in records)
        {
            var escapedError = r.Error?.Replace("\"", "\"\"") ?? string.Empty;
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "{0},\"{1}\",{2},{3},{4},{5},{6:F3},{7},{8},{9},{10},\"{11}\"",
                r.FileIndex, r.FileName, r.Iteration, r.TargetWidth, r.RequestedBackend, r.ActualBackend,
                r.DecodeMs, r.AllocatedBytes, r.PixelWidth, r.PixelHeight, r.Success, escapedError));
        }

        await File.WriteAllTextAsync(path, sb.ToString());
    }

    /// <summary>"1.25x", or "n/a" when there is no Wpf baseline for the width (a 0 speedup is "not computed", never "1.00x").</summary>
    private static string FormatSpeedup(double speedup) =>
        speedup > 0 ? string.Create(CultureInfo.InvariantCulture, $"{speedup:F2}x") : "n/a";

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return string.Create(CultureInfo.InvariantCulture, $"{bytes} B");
        if (bytes < 1024 * 1024) return string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:F1} KB");
        return string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024.0):F1} MB");
    }
}

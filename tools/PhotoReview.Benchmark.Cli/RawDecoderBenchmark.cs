using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using PhotoReview.Imaging.Decoding;
using PhotoReview.Imaging.LibRaw;
using PhotoReview.Imaging.Raw;

namespace PhotoReview.Benchmark.Cli;

/// <summary>
/// Measures RAW header parsing, embedded-preview first paint, direct JPEG-preview decoding and full LibRaw decode.
/// Failures are per file and per row: one truncated RAW, a missing libraw.dll or a WPF decode error becomes a failed row,
/// the CSV/JSON reports are always written, and the exit code says whether every required decoder row succeeded.
/// </summary>
public static class RawDecoderBenchmark
{
    private static readonly int[] s_widths = [1920, 2560, 3840];
    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };

    /// <summary>Operations whose failure makes the run fail (EmbeddedJpegDirect is a comparison row and is reported but not required).</summary>
    internal static readonly string[] RequiredOperations = ["HeaderParse", "PreviewSelect", "RawPreview", "LibRawFullDecode", "FileProcess"];

    public sealed record Measurement(string File, string Format, string Operation, int TargetWidth, int Iteration,
        double ElapsedMs, int OutputWidth, int OutputHeight, bool Success, string? Error, string? SelectedPreview = null);

    /// <summary>Per operation and width: iteration 0 (cold: JIT, OS file cache) is reported separately from the warm median of iterations 1+.</summary>
    public sealed record OperationSummary(string Operation, int TargetWidth, int Samples, int Successes, int Failures,
        double? ColdMedianMs, double? WarmMedianMs, int WarmSamples);

    internal static int ParseIterations(string[] args)
    {
        if (args.Length is < 4 or > 5 || args[0] != "--decoder-bench" || args[1] != "--raw")
            throw new ArgumentException("Usage: --decoder-bench --raw <folder> <outDir> [iterations=3]");
        return args.Length == 5 ? BenchmarkCliArguments.ParsePositiveInt(args[4], "iterations") : 3;
    }

    /// <summary>
    /// Exceptions that mean "this file/decoder row failed" (recorded, run continues). Includes <see cref="InvalidDataException"/> (the RAW
    /// readers contract, not an IOException) and WPF FileFormatException (a FormatException). Cancellation and out-of-memory stay fatal.
    /// </summary>
    internal static bool IsMeasurementFailure(Exception ex) => ex is not (OperationCanceledException or OutOfMemoryException) &&
        ex is IOException or InvalidDataException or InvalidOperationException or NotSupportedException or FormatException
            or ArgumentException or UnauthorizedAccessException or ExternalException or DllNotFoundException
            or EntryPointNotFoundException or BadImageFormatException;

    /// <summary>0 only when files were measured and no required decoder row failed; a run that measured nothing is a failure.</summary>
    internal static int ComputeExitCode(IReadOnlyList<Measurement> rows, int fileCount)
    {
        if (fileCount == 0 || rows.Count == 0 || !rows.Any(row => row.Success)) return 1;
        if (!rows.Any(row => row.Success && row.Operation == "LibRawFullDecode")) return 1;
        return rows.Any(row => !row.Success && RequiredOperations.Contains(row.Operation, StringComparer.Ordinal)) ? 1 : 0;
    }

    /// <summary>The preview both RawPreview (via <see cref="RawDecoder"/>) and EmbeddedJpegDirect decode for a target width: the same selector call, so the rows are like-for-like.</summary>
    internal static EmbeddedPreview? SelectPreviewForWidth(IRawHeaderSource source, IReadOnlyList<EmbeddedPreview> previews, int width, int orientation) =>
        PreviewSelector.SelectPreview(source, previews, new DecodeBox(width, 0), orientation);

    internal static string DescribePreview(EmbeddedPreview? preview) => preview is null
        ? "none"
        : string.Create(CultureInfo.InvariantCulture, $"{preview.Width}x{preview.Height}@{preview.Offset}+{preview.Length}");

    public static async Task<int> RunAsync(string[] args)
    {
        var iterations = ParseIterations(args);
        var folder = Path.GetFullPath(args[2]);
        var outDir = Path.GetFullPath(args[3]);
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException($"RAW directory not found: {folder}");
        ToolPathGuard.EnsureOutputDirectory(outDir, folder);
        var files = Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly)
            .Where(RawFileTypes.IsRawExtension)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        if (files.Length == 0) throw new InvalidOperationException($"No supported RAW files found in: {folder}");

        var (exitCode, _) = await RunFilesAsync(files, iterations, folder, outDir, s_widths);
        Console.WriteLine($"[RAW-BENCH] CSV: {Path.Combine(outDir, "raw-decoder-bench.csv")}");
        Console.WriteLine($"[RAW-BENCH] JSON: {Path.Combine(outDir, "raw-decoder-bench.json")}");
        return exitCode;
    }

    /// <summary>Measures every file, always writes both reports, and returns the exit code with the rows.</summary>
    internal static async Task<(int ExitCode, IReadOnlyList<Measurement> Rows)> RunFilesAsync(
        IReadOnlyList<string> files, int iterations, string folder, string outDir, IReadOnlyList<int> widths)
    {
        Directory.CreateDirectory(outDir);
        var rows = new List<Measurement>();
        Console.WriteLine($"[RAW-BENCH] {files.Count} RAW files; iterations={iterations}; widths={string.Join(',', widths)}");
        foreach (var file in files)
        {
            Console.WriteLine($"[RAW-BENCH] {Path.GetFileName(file)}");
            try { MeasureFile(file, iterations, widths, rows); }
            catch (Exception ex) when (IsMeasurementFailure(ex))
            {
                rows.Add(new Measurement(Path.GetFileName(file), Path.GetExtension(file).TrimStart('.').ToUpperInvariant(), "FileProcess", 0, 0, 0, 0, 0,
                    false, ex.GetType().Name + ": " + ex.Message));
            }
        }

        await WriteReportsAsync(rows, outDir, folder);
        foreach (var failure in rows.Where(row => !row.Success).Take(20))
            Console.Error.WriteLine($"[RAW-BENCH] FAILED {failure.File} {failure.Operation} width={failure.TargetWidth} iteration={failure.Iteration}: {failure.Error}");
        return (ComputeExitCode(rows, files.Count), rows);
    }

    private static void MeasureFile(string file, int iterations, IReadOnlyList<int> widths, List<Measurement> rows)
    {
        var ext = Path.GetExtension(file);
        RawContainerInfo? info = null;
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            RawContainerInfo? parsed = null;
            var parse = Measure(file, ext.TrimStart('.').ToUpperInvariant(), "HeaderParse", 0, iteration, () =>
            {
                using var source = new SourceRawHeaderSource(file, PhotoReview.Core.Abstractions.PhysicalSourceReader.Instance);
                var probe = source.Read(0, Math.Min(64, (int)source.Length));
                var reader = new RawContainerReaderRegistry().FindReader(probe, ext)
                    ?? throw new NotSupportedException($"No RAW reader for {ext}");
                parsed = reader.Read(source, CancellationToken.None);
                return (parsed.SensorWidth, parsed.SensorHeight);
            });
            rows.Add(parse);
            if (parsed is not null) info = parsed;
        }

        if (info is null)
        {
            Console.WriteLine("  Header parse failed; decode phases skipped.");
            return;
        }

        foreach (var width in widths)
        {
            EmbeddedPreview? preview;
            using (var source = new SourceRawHeaderSource(file, PhotoReview.Core.Abstractions.PhysicalSourceReader.Instance))
                preview = SelectPreviewForWidth(source, info.Previews, width, info.Orientation);
            if (preview is null || preview.Length <= 0 || preview.Length > int.MaxValue)
            {
                rows.Add(new Measurement(Path.GetFileName(file), info.Format.ToString(), "PreviewSelect", width, 0, 0, 0, 0, false, "No supported embedded preview"));
                continue;
            }

            var selected = DescribePreview(preview);
            for (var iteration = 0; iteration < iterations; iteration++)
            {
                rows.Add(Measure(file, info.Format.ToString(), "RawPreview", width, iteration, () =>
                {
                    var decoder = new RawDecoder(new WpfBitmapImageDecoder());
                    var decoded = decoder.Decode(new DecodeRequest(file, width, ApplyOrientation: true));
                    return (decoded.PixelWidth, decoded.PixelHeight);
                }, selected));
                rows.Add(Measure(file, info.Format.ToString(), "EmbeddedJpegDirect", width, iteration, () =>
                {
                    var bytes = ReadRange(file, preview.Offset, checked((int)preview.Length));
                    var decoded = new WpfBitmapImageDecoder().Decode(new DecodeRequest(file, width,
                        ApplyOrientation: true, Bytes: bytes, SourceOrientation: info.Orientation));
                    return (decoded.PixelWidth, decoded.PixelHeight);
                }, selected));
            }
        }

        for (var iteration = 0; iteration < iterations; iteration++)
            rows.Add(Measure(file, info.Format.ToString(), "LibRawFullDecode", 0, iteration, () =>
            {
                var decoded = new LibRawDecoder().Decode(new DecodeRequest(file, DecodeBox.Unbounded));
                return (decoded.PixelWidth, decoded.PixelHeight);
            }));
    }

    private static Measurement Measure(string path, string format, string operation, int width, int iteration,
        Func<(int Width, int Height)> action, string? selectedPreview = null)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            var result = action();
            timer.Stop();
            return new(Path.GetFileName(path), format, operation, width, iteration, timer.Elapsed.TotalMilliseconds,
                result.Width, result.Height, true, null, selectedPreview);
        }
        catch (Exception ex) when (IsMeasurementFailure(ex))
        {
            timer.Stop();
            return new(Path.GetFileName(path), format, operation, width, iteration, timer.Elapsed.TotalMilliseconds,
                0, 0, false, ex.GetType().Name + ": " + ex.Message, selectedPreview);
        }
    }

    private static byte[] ReadRange(string path, long offset, int length)
    {
        var bytes = new byte[length];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            64 * 1024, FileOptions.RandomAccess);
        stream.Position = offset;
        stream.ReadExactly(bytes);
        return bytes;
    }

    internal static OperationSummary[] Summarize(IReadOnlyList<Measurement> rows) =>
        [.. rows.GroupBy(row => (row.Operation, row.TargetWidth)).Select(group =>
        {
            var ok = group.Where(row => row.Success).ToArray();
            return new OperationSummary(group.Key.Operation, group.Key.TargetWidth, group.Count(), ok.Length, group.Count() - ok.Length,
                Median(ok.Where(row => row.Iteration == 0).Select(row => row.ElapsedMs).ToArray()),
                Median(ok.Where(row => row.Iteration > 0).Select(row => row.ElapsedMs).ToArray()),
                ok.Count(row => row.Iteration > 0));
        })];

    private static async Task WriteReportsAsync(IReadOnlyList<Measurement> rows, string outDir, string folder)
    {
        var csv = new System.Text.StringBuilder("file,format,operation,targetWidth,iteration,elapsedMs,outputWidth,outputHeight,success,error,selectedPreview");
        csv.AppendLine();
        foreach (var row in rows)
        {
            var fields = new[]
            {
                Csv(row.File), Csv(row.Format), Csv(row.Operation),
                row.TargetWidth.ToString(CultureInfo.InvariantCulture), row.Iteration.ToString(CultureInfo.InvariantCulture),
                row.ElapsedMs.ToString("F3", CultureInfo.InvariantCulture), row.OutputWidth.ToString(CultureInfo.InvariantCulture),
                row.OutputHeight.ToString(CultureInfo.InvariantCulture), row.Success ? "true" : "false", Csv(row.Error ?? ""),
                Csv(row.SelectedPreview ?? ""),
            };
            csv.AppendLine(string.Join(",", fields));
        }

        await File.WriteAllTextAsync(Path.Combine(outDir, "raw-decoder-bench.csv"), csv.ToString(), System.Text.Encoding.UTF8);
        var report = new { TimestampUtc = DateTime.UtcNow, MachineName = Environment.MachineName,
            OsVersion = Environment.OSVersion.VersionString, SourceFolder = folder,
            Method = "RawPreview and EmbeddedJpegDirect decode the SAME embedded JPEG (PreviewSelector for the target width: smallest preview with both sides >= width, else largest; see selectedPreview). "
                + "RawPreview additionally includes container parse, the preview range read and Adobe RGB profile handling; EmbeddedJpegDirect reads the range and decodes it. LibRawFullDecode is full sensor demosaic. "
                + "ColdMedianMs is iteration 0 (JIT and OS file cache cold) and WarmMedianMs the median of iterations 1+ (null with iterations=1).",
            StandaloneSameCameraJpegCorpusAvailable = false, Rows = rows, Summary = Summarize(rows) };
        await File.WriteAllTextAsync(Path.Combine(outDir, "raw-decoder-bench.json"), JsonSerializer.Serialize(report, s_jsonOptions), System.Text.Encoding.UTF8);
    }

    private static double? Median(double[] values)
    {
        if (values.Length == 0) return null;
        Array.Sort(values);
        return values.Length % 2 == 1 ? values[values.Length / 2] : (values[values.Length / 2 - 1] + values[values.Length / 2]) / 2d;
    }

    private static string Csv(string value)
    {
        var quote = new string((char)34, 1);
        return quote + value.Replace(quote, quote + quote, StringComparison.Ordinal) + quote;
    }
}

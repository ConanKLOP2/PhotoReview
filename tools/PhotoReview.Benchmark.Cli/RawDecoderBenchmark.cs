using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using PhotoReview.Imaging.Decoding;
using PhotoReview.Imaging.LibRaw;
using PhotoReview.Imaging.Raw;

namespace PhotoReview.Benchmark.Cli;

/// <summary>Measures RAW header parsing, embedded-preview first paint, direct JPEG-preview decoding and full LibRaw decode.</summary>
public static class RawDecoderBenchmark
{
    private static readonly int[] s_widths = [1920, 2560, 3840];
    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };

    public sealed record Measurement(string File, string Format, string Operation, int TargetWidth, int Iteration,
        double ElapsedMs, int OutputWidth, int OutputHeight, bool Success, string? Error);

    internal static int ParseIterations(string[] args)
    {
        if (args.Length is < 4 or > 5 || args[0] != "--decoder-bench" || args[1] != "--raw")
            throw new ArgumentException("Usage: --decoder-bench --raw <folder> <outDir> [iterations=3]");
        return args.Length == 5 ? BenchmarkCliArguments.ParsePositiveInt(args[4], "iterations") : 3;
    }

    public static async Task<int> RunAsync(string[] args)
    {
        var iterations = ParseIterations(args);
        var folder = Path.GetFullPath(args[2]);
        var outDir = Path.GetFullPath(args[3]);
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException($"RAW directory not found: {folder}");
        var files = Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly)
            .Where(RawFileTypes.IsRawExtension)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        if (files.Length == 0) throw new InvalidOperationException($"No supported RAW files found in: {folder}");
        Directory.CreateDirectory(outDir);

        var rows = new List<Measurement>();
        Console.WriteLine($"[RAW-BENCH] {files.Length} RAW files; iterations={iterations}; widths={string.Join(',', s_widths)}");
        foreach (var file in files)
        {
            var ext = Path.GetExtension(file);
            Console.WriteLine($"[RAW-BENCH] {Path.GetFileName(file)}");
            RawContainerInfo? info = null;
            EmbeddedPreview? preview = null;
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
                continue;
            }
            using (var source = new SourceRawHeaderSource(file, PhotoReview.Core.Abstractions.PhysicalSourceReader.Instance))
                preview = PreviewSelector.SelectPreview(source, info.Previews, DecodeBox.Unbounded, info.Orientation);
            if (preview is null || preview.Length <= 0 || preview.Length > int.MaxValue)
            {
                rows.Add(new Measurement(Path.GetFileName(file), info.Format.ToString(), "PreviewSelect", 0, 0, 0, 0, 0, false, "No supported embedded preview"));
                continue;
            }

            foreach (var width in s_widths)
            {
                for (var iteration = 0; iteration < iterations; iteration++)
                {
                    rows.Add(Measure(file, info.Format.ToString(), "RawPreview", width, iteration, () =>
                    {
                        var decoder = new RawDecoder(new WpfBitmapImageDecoder());
                        var decoded = decoder.Decode(new DecodeRequest(file, width, ApplyOrientation: true));
                        return (decoded.PixelWidth, decoded.PixelHeight);
                    }));
                    rows.Add(Measure(file, info.Format.ToString(), "EmbeddedJpegDirect", width, iteration, () =>
                    {
                        var bytes = ReadRange(file, preview.Offset, checked((int)preview.Length));
                        var decoded = new WpfBitmapImageDecoder().Decode(new DecodeRequest(file, width,
                            ApplyOrientation: true, Bytes: bytes, SourceOrientation: info.Orientation));
                        return (decoded.PixelWidth, decoded.PixelHeight);
                    }));
                }
            }

            for (var iteration = 0; iteration < iterations; iteration++)
                rows.Add(Measure(file, info.Format.ToString(), "LibRawFullDecode", 0, iteration, () =>
                {
                    var decoded = new LibRawDecoder().Decode(new DecodeRequest(file, DecodeBox.Unbounded));
                    return (decoded.PixelWidth, decoded.PixelHeight);
                }));
        }

        await WriteReportsAsync(rows, outDir, folder);
        Console.WriteLine($"[RAW-BENCH] CSV: {Path.Combine(outDir, "raw-decoder-bench.csv")}");
        Console.WriteLine($"[RAW-BENCH] JSON: {Path.Combine(outDir, "raw-decoder-bench.json")}");
        return rows.Any(row => row.Success) ? 0 : 1;
    }

    private static Measurement Measure(string path, string format, string operation, int width, int iteration, Func<(int Width, int Height)> action)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            var result = action();
            timer.Stop();
            return new(Path.GetFileName(path), format, operation, width, iteration, timer.Elapsed.TotalMilliseconds,
                result.Width, result.Height, true, null);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            timer.Stop();
            return new(Path.GetFileName(path), format, operation, width, iteration, timer.Elapsed.TotalMilliseconds,
                0, 0, false, ex.GetType().Name + ": " + ex.Message);
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

    private static async Task WriteReportsAsync(IReadOnlyList<Measurement> rows, string outDir, string folder)
    {
        var csv = new System.Text.StringBuilder("file,format,operation,targetWidth,iteration,elapsedMs,outputWidth,outputHeight,success,error\n");
        foreach (var row in rows)
            csv.Append(Csv(row.File)).Append(',').Append(Csv(row.Format)).Append(',').Append(Csv(row.Operation)).Append(',')
                .Append(row.TargetWidth.ToString(CultureInfo.InvariantCulture)).Append(',').Append(row.Iteration.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(row.ElapsedMs.ToString("F3", CultureInfo.InvariantCulture)).Append(',').Append(row.OutputWidth.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(row.OutputHeight.ToString(CultureInfo.InvariantCulture)).Append(',').Append(row.Success ? "true" : "false").Append(',').Append(Csv(row.Error ?? "")).AppendLine();
        await File.WriteAllTextAsync(Path.Combine(outDir, "raw-decoder-bench.csv"), csv.ToString(), System.Text.Encoding.UTF8);
        var summary = rows.GroupBy(row => (row.Operation, row.TargetWidth)).Select(group => new
        {
            group.Key.Operation,
            TargetWidth = group.Key.TargetWidth,
            Samples = group.Count(),
            Successes = group.Count(row => row.Success),
            Failures = group.Count(row => !row.Success),
            MedianMs = Median(group.Where(row => row.Success).Select(row => row.ElapsedMs).ToArray())
        }).ToArray();
        var report = new { TimestampUtc = DateTime.UtcNow, MachineName = Environment.MachineName,
            OsVersion = Environment.OSVersion.VersionString, SourceFolder = folder,
            Method = "RawPreview includes container parse, preview range read and WPF decode; EmbeddedJpegDirect decodes the same selected embedded JPEG range directly; LibRawFullDecode is full sensor demosaic.",
            StandaloneSameCameraJpegCorpusAvailable = false, Rows = rows, Summary = summary };
        await File.WriteAllTextAsync(Path.Combine(outDir, "raw-decoder-bench.json"), JsonSerializer.Serialize(report, s_jsonOptions), System.Text.Encoding.UTF8);
    }

    private static double? Median(double[] values)
    {
        if (values.Length == 0) return null;
        Array.Sort(values);
        return values.Length % 2 == 1 ? values[values.Length / 2] : (values[values.Length / 2 - 1] + values[values.Length / 2]) / 2d;
    }

    private static string Csv(string value) => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}

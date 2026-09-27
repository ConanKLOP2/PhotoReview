using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Media.Imaging;
using PhotoReview.App;
using PhotoReview.Core.Catalog;
using PhotoReview.Imaging.Decoding;

/// <summary>
/// D05: <c>--io-decode-split &lt;folder&gt; &lt;outDir&gt; [widths] [max]</c> splits a preview
/// navigation into its I/O and decode halves so H8 ("DecodePixelWidth doesn't meaningfully cut JPEG
/// decode time") and H9 ("reading the PNG disk cache back is slower than re-decoding the JPEG
/// source") can be checked with real numbers instead of a single fused decode measurement.
///
/// For every supported file (up to <paramref name="max"/>, ordered by name like
/// <see cref="LocalImageBenchmark"/>) this measures, three times each (run 1 = cold, P50 of runs 2-3
/// = warm): a full sequential read into RAM, a decode from that in-memory copy at each width, a
/// decode straight from the file via a real <see cref="WpfBitmapImageDecoder"/> instance at each
/// width, a header-only <see cref="BitmapDecoder"/> read (DelayCreation, mirrors
/// <c>GetOriginalDimensionsAsync</c>), and a PNG encode/decode round trip at width 2560 that stands
/// in for the on-disk preview cache. Nothing here mutates <see cref="DiagOptions"/> or reads
/// PHOTOREVIEW_DIAG_* -- it exercises the always-on decode path directly, on a single thread, so the
/// numbers are not skewed by preload contention or the diagnostics listener.
/// </summary>
internal static class IoDecodeSplit
{
    private const int PngCompareWidth = 2560;

    /// <summary>One timed quantity across 3 runs. <see cref="WarmP50"/> is the average of runs 2-3
    /// (median of two values), used everywhere a single "warm" number is reported; run 1 ("cold") is
    /// kept separately because it is not necessarily cold with respect to the OS file-system cache --
    /// only with respect to this process (see io-decode-split.md caveats).</summary>
    internal readonly record struct Measurement(double Cold, double Warm2, double Warm3, long Bytes)
    {
        public double WarmP50 => (Warm2 + Warm3) / 2.0;
    }

    internal sealed class FileResult
    {
        public int Index;
        public long SourceBytes;
        public int OriginalWidth;
        public int OriginalHeight;
        public Measurement Read;
        public Measurement HeaderOnly;
        public Measurement PngEncode;
        public Measurement PngDecode;
        public Measurement Decode2560Mem;
        public readonly Dictionary<int, Measurement> DecodeFromMem = new();
        public readonly Dictionary<int, Measurement> DecodeFromFile = new();
    }

    public static async Task RunAsync(string folder, string outDir, int[] widths, int max)
    {
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException(folder);
        if (widths.Length == 0) throw new ArgumentException("At least one width is required", nameof(widths));
        Directory.CreateDirectory(outDir);
        var files = Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly)
            .Where(ImageFileTypes.IsSupported)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, max))
            .ToArray();
        if (files.Length == 0) throw new InvalidOperationException("No supported images found in: (see console arg)");

        var rawCsv = new StringBuilder("index,metric,width,run,ms,bytes\n");
        var results = new List<FileResult>(files.Length);
        for (var i = 0; i < files.Length; i++)
        {
            // Sequential, single-thread, on whatever thread Task.Run schedules us to -- this tool
            // measures raw decode cost, not UI-thread contention, so no STA/dispatcher is involved.
            var result = await Task.Run(() => MeasureFile(i, files[i], widths, rawCsv));
            results.Add(result);
            Console.WriteLine(FormattableString.Invariant(
                $"[{i + 1}/{files.Length}] read(warm)={result.Read.WarmP50:F1}ms decodeMem@{widths[0]}(warm)={result.DecodeFromMem[widths[0]].WarmP50:F1}ms {result.OriginalWidth}x{result.OriginalHeight}"));
        }

        var rawPath = Path.Combine(outDir, "raw.csv");
        await File.WriteAllTextAsync(rawPath, rawCsv.ToString());
        var summaryPath = Path.Combine(outDir, "summary.md");
        await File.WriteAllTextAsync(summaryPath, BuildSummary(results, widths, files.Length, max));
        Console.WriteLine($"RAW: {rawPath}");
        Console.WriteLine($"REPORT: {summaryPath}");
    }

    private static FileResult MeasureFile(int index, string path, int[] widths, StringBuilder rawCsv)
    {
        var result = new FileResult { Index = index, SourceBytes = SafeLength(path) };
        var imageDecoder = new WpfBitmapImageDecoder();

        var readRuns = new double[3];
        byte[] bytes = [];
        for (var run = 0; run < 3; run++)
        {
            var sw = Stopwatch.StartNew();
            bytes = ReadAllBytesSequential(path);
            sw.Stop();
            readRuns[run] = sw.Elapsed.TotalMilliseconds;
            AppendRaw(rawCsv, index, "read", 0, run, readRuns[run], bytes.LongLength);
        }
        result.Read = new Measurement(readRuns[0], readRuns[1], readRuns[2], bytes.LongLength);

        var headerRuns = new double[3];
        for (var run = 0; run < 3; run++)
        {
            var sw = Stopwatch.StartNew();
            using (var stream = new MemoryStream(bytes, writable: false))
            {
                // Same options GetOriginalDimensionsAsync uses: DelayCreation avoids decoding pixels
                // just to read the header.
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                var frame = decoder.Frames[0];
                result.OriginalWidth = frame.PixelWidth;
                result.OriginalHeight = frame.PixelHeight;
            }
            sw.Stop();
            headerRuns[run] = sw.Elapsed.TotalMilliseconds;
            AppendRaw(rawCsv, index, "headerOnly", 0, run, headerRuns[run], 0);
        }
        result.HeaderOnly = new Measurement(headerRuns[0], headerRuns[1], headerRuns[2], 0);

        foreach (var width in widths)
        {
            result.DecodeFromMem[width] = TimeThree(rawCsv, index, "decodeFromMem", width,
                () => DecodeFromMemory(bytes, width));
            result.DecodeFromFile[width] = TimeThree(rawCsv, index, "decodeFromFile", width,
                () => imageDecoder.Decode(new DecodeRequest(path, width)));
        }
        result.Decode2560Mem = result.DecodeFromMem.TryGetValue(PngCompareWidth, out var existing)
            ? existing
            : TimeThree(rawCsv, index, "decodeFromMem", PngCompareWidth, () => DecodeFromMemory(bytes, PngCompareWidth));

        var pngEncodeRuns = new double[3];
        var pngDecodeRuns = new double[3];
        long pngBytes = 0;
        var tempPngPath = Path.Combine(Path.GetTempPath(), $"io-decode-split-{Guid.NewGuid():N}.png");
        try
        {
            for (var run = 0; run < 3; run++)
            {
                // Decode is untimed here (already measured above via Decode2560Mem/DecodeFromMem);
                // only encode and the disk-cache-simulating decode below are timed.
                var previewBitmap = DecodeFromMemory(bytes, PngCompareWidth);
                var swEncode = Stopwatch.StartNew();
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(previewBitmap));
                using (var pngStream = new MemoryStream())
                {
                    encoder.Save(pngStream);
                    swEncode.Stop();
                    pngBytes = pngStream.Length;
                    File.WriteAllBytes(tempPngPath, pngStream.ToArray());
                }
                pngEncodeRuns[run] = swEncode.Elapsed.TotalMilliseconds;
                AppendRaw(rawCsv, index, "pngEncode", PngCompareWidth, run, pngEncodeRuns[run], pngBytes);

                var swDecode = Stopwatch.StartNew();
                imageDecoder.Decode(new DecodeRequest(tempPngPath, 0));
                swDecode.Stop();
                pngDecodeRuns[run] = swDecode.Elapsed.TotalMilliseconds;
                AppendRaw(rawCsv, index, "pngDecode", PngCompareWidth, run, pngDecodeRuns[run], pngBytes);
            }
        }
        finally
        {
            try { File.Delete(tempPngPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        result.PngEncode = new Measurement(pngEncodeRuns[0], pngEncodeRuns[1], pngEncodeRuns[2], pngBytes);
        result.PngDecode = new Measurement(pngDecodeRuns[0], pngDecodeRuns[1], pngDecodeRuns[2], pngBytes);

        return result;
    }

    private static Measurement TimeThree(StringBuilder rawCsv, int index, string metric, int width, Action action)
    {
        var runs = new double[3];
        for (var run = 0; run < 3; run++)
        {
            var sw = Stopwatch.StartNew();
            action();
            sw.Stop();
            runs[run] = sw.Elapsed.TotalMilliseconds;
            AppendRaw(rawCsv, index, metric, width, run, runs[run], 0);
        }
        return new Measurement(runs[0], runs[1], runs[2], 0);
    }

    private static byte[] ReadAllBytesSequential(string path)
    {
        // Same FileStream shape as PreviewImageService.DecodeSource's PreRead branch (D05): allow
        // concurrent Move/Delete, 1 MB buffer, sequential-scan hint.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.SequentialScan);
        var buffer = new byte[stream.Length];
        var offset = 0;
        int read;
        while (offset < buffer.Length && (read = stream.Read(buffer, offset, buffer.Length - offset)) > 0) offset += read;
        return buffer;
    }

    private static BitmapImage DecodeFromMemory(byte[] bytes, int targetWidth)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var bitmap = new BitmapImage();
        bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
        if (targetWidth > 0) bitmap.DecodePixelWidth = targetWidth;
        bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
        return bitmap;
    }

    private static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; } catch (IOException) { return 0; }
    }

    private static void AppendRaw(StringBuilder sb, int index, string metric, int width, int run, double ms, long bytes)
        => sb.Append(index).Append(',').Append(metric).Append(',').Append(width).Append(',').Append(run).Append(',')
             .Append(ms.ToString("F3", CultureInfo.InvariantCulture)).Append(',').Append(bytes).Append('\n');

    private static double Percentile(IEnumerable<double> values, double fraction)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        if (sorted.Length == 0) return double.NaN;
        var idx = Math.Clamp((int)Math.Ceiling(fraction * sorted.Length) - 1, 0, sorted.Length - 1);
        return sorted[idx];
    }

    internal static string BuildSummary(IReadOnlyList<FileResult> results, int[] widths, int fileCount, int max)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# io-decode-split summary");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Generated (UTC): {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Files measured: {fileCount} (max={max})");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Widths: {string.Join(", ", widths)}");
        sb.AppendLine("- Paths are not recorded; files are referenced by index only (see raw.csv).");
        sb.AppendLine();

        var megapixels = results.Select(r => r.OriginalWidth * (double)r.OriginalHeight / 1_000_000.0).ToArray();
        sb.AppendLine("## Nguồn ảnh");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Megapixel: P50={Percentile(megapixels, .5):F1} MP, min={megapixels.Min():F1} MP, max={megapixels.Max():F1} MP");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Byte trung bình: {results.Average(r => r.SourceBytes) / 1024.0 / 1024.0:F2} MB");
        sb.AppendLine();

        sb.AppendLine("## Read (đọc toàn bộ vào RAM)");
        sb.AppendLine();
        sb.AppendLine("| | cold P50 (ms) | warm P50 (ms) | warm P95 (ms) |");
        sb.AppendLine("|---|---:|---:|---:|");
        sb.AppendLine(MetricRow("read", results.Select(r => r.Read)));
        sb.AppendLine(MetricRow("headerOnly", results.Select(r => r.HeaderOnly)));
        sb.AppendLine();

        sb.AppendLine("## Decode theo width (H8: giảm width có giảm t_decode không)");
        sb.AppendLine();
        sb.AppendLine("| width | mem cold P50 | mem warm P50 | mem warm P95 | file cold P50 | file warm P50 | file warm P95 |");
        sb.AppendLine("|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var width in widths)
        {
            var mem = results.Select(r => r.DecodeFromMem[width]).ToArray();
            var file = results.Select(r => r.DecodeFromFile[width]).ToArray();
            // One interpolation with the invariant provider: a "+"-joined second interpolation is a plain string formatted with the current culture.
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {width} | {Percentile(mem.Select(m => m.Cold), .5):F1} | {Percentile(mem.Select(m => m.WarmP50), .5):F1} | {Percentile(mem.Select(m => m.WarmP50), .95):F1} | {Percentile(file.Select(m => m.Cold), .5):F1} | {Percentile(file.Select(m => m.WarmP50), .5):F1} | {Percentile(file.Select(m => m.WarmP50), .95):F1} |");
        }
        var distinctWidths = widths.Distinct().OrderBy(w => w).ToArray();
        sb.AppendLine();
        if (distinctWidths.Length < 2)
        {
            sb.AppendLine("- H8: chỉ có một width được đo, không đủ dữ liệu để so sánh đường cong decode.");
        }
        else
        {
            // Compare the two ends of whatever width list was requested. When only one nonzero
            // width was given (e.g. "0,1920"), this ends up comparing width=0 (no DecodePixelWidth,
            // decode at native resolution) against that width, which is still meaningful: it shows
            // whether requesting a resize costs more than just decoding at native size.
            var low = distinctWidths[0];
            var high = distinctWidths[^1];
            var lowP50 = Percentile(results.Select(r => r.DecodeFromMem[low].WarmP50), .5);
            var highP50 = Percentile(results.Select(r => r.DecodeFromMem[high].WarmP50), .5);
            var h8Confirmed = Math.Abs(highP50 - lowP50) <= 0.15 * Math.Max(lowP50, highP50); // decode time roughly flat across width
            sb.AppendLine(CultureInfo.InvariantCulture, $"- H8: decodeFromMem warm P50 tại width={low} là {lowP50:F1} ms, tại width={high} là {highP50:F1} ms " +
                           $"({(h8Confirmed ? "gần như không đổi -> H8 ĐÚNG cho bộ này" : "khác biệt đáng kể -> H8 SAI (DecodePixelWidth có ảnh hưởng) cho bộ này")}).");
        }
        sb.AppendLine();

        sb.AppendLine("## Tỷ lệ I/O so với decode (read / (read + decodeFromMem))");
        sb.AppendLine();
        foreach (var width in widths)
        {
            var ratios = results.Select(r => r.Read.WarmP50 / Math.Max(0.001, r.Read.WarmP50 + r.DecodeFromMem[width].WarmP50)).ToArray();
            sb.AppendLine(CultureInfo.InvariantCulture, $"- width={width}: P50={Percentile(ratios, .5):P0}, P95={Percentile(ratios, .95):P0}");
        }
        sb.AppendLine();

        sb.AppendLine("## PNG disk cache so với đọc lại + decode nguồn (H9, width=2560)");
        sb.AppendLine();
        var pngDecodeWarm = results.Select(r => r.PngDecode.WarmP50).ToArray();
        var rebuildWarm = results.Select(r => r.Read.WarmP50 + r.Decode2560Mem.WarmP50).ToArray();
        var pngDecodeP50 = Percentile(pngDecodeWarm, .5);
        var rebuildP50 = Percentile(rebuildWarm, .5);
        var pngSizeAvgKb = results.Average(r => r.PngDecode.Bytes) / 1024.0;
        var h9Confirmed = pngDecodeP50 > rebuildP50;
        sb.AppendLine(CultureInfo.InvariantCulture, $"- pngDecode warm P50={pngDecodeP50:F1} ms, P95={Percentile(pngDecodeWarm, .95):F1} ms");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- read + decodeFromMem@2560 warm P50={rebuildP50:F1} ms, P95={Percentile(rebuildWarm, .95):F1} ms");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- pngEncode warm P50={Percentile(results.Select(r => r.PngEncode.WarmP50), .5):F1} ms, kích thước PNG trung bình={pngSizeAvgKb:F0} KB");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- H9 ({(h9Confirmed ? "ĐÚNG" : "SAI")} cho bộ này): t_disk (pngDecode) {(h9Confirmed ? ">" : "<=")} t_read + t_decode(JPEG nguồn)");
        sb.AppendLine();
        return sb.ToString();
    }

    private static string MetricRow(string name, IEnumerable<Measurement> measurements)
    {
        var list = measurements.ToArray();
        return string.Create(CultureInfo.InvariantCulture, $"| {name} | {Percentile(list.Select(m => m.Cold), .5):F1} | {Percentile(list.Select(m => m.WarmP50), .5):F1} | {Percentile(list.Select(m => m.WarmP50), .95):F1} |");
    }
}

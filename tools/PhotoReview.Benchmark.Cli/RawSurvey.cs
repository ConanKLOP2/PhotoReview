using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using PhotoReview.Core.Abstractions;
using PhotoReview.Imaging;
using PhotoReview.Imaging.Decoding;
using PhotoReview.Imaging.Decoding.Wic;
using PhotoReview.Imaging.Raw;

namespace PhotoReview.Benchmark.Cli;

/// <summary>
/// RAW survey tool for RAW-01: inspects container format, embedded JPEGs (via throwaway marker scan),
/// container orientation, sensor/visible size, and runs a WIC probe (ReadInfo + full Decode).
/// </summary>
internal static class RawSurvey
{
    public sealed record EmbeddedJpeg(
        int Index,
        long Offset,
        long Length,
        int Width,
        int Height,
        string ColorSpaceHint);

    public sealed record SurveyFileResult(
        string Path,
        string FileName,
        string Format,
        long FileSizeBytes,
        int ContainerOrientation,
        int SensorWidth,
        int SensorHeight,
        IReadOnlyList<EmbeddedJpeg> EmbeddedJpegs,
        bool WicReadInfoSuccess,
        int WicInfoWidth,
        int WicInfoHeight,
        int WicInfoOrientation,
        string? WicInfoError,
        bool WicDecodeSuccess,
        int WicDecodedWidth,
        int WicDecodedHeight,
        double WicMedianDecodeTimeMs,
        string? WicDecodeError,
        bool WicDecodedPreviewOnly,
        string SensorSource = "unknown",
        string? Error = null);

    public sealed record SurveySummary(
        string WindowsVersion,
        string WicRawCodecInfo,
        IReadOnlyList<SurveyFileResult> Files);

    internal const string Usage = "Usage: PhotoReview.Benchmark.Cli --raw-survey <directory> [--markdown <output.md> [--force]]";

    /// <summary>
    /// Validates <c>--raw-survey &lt;dir&gt; [--markdown|-o &lt;file&gt; [--force]]</c> (args[0] is the mode). A valueless <c>--markdown</c>, a repeated one or
    /// any other token is an error: silently ignoring it would exit 0 without the report the caller asked for.
    /// <c>--force</c> allows replacing an existing report file (see <see cref="ValidateMarkdownPath"/>).
    /// </summary>
    internal static bool TryParseArgs(IReadOnlyList<string> args, out string? markdownPath, out bool force, out string? error)
    {
        markdownPath = null;
        force = false;
        error = null;
        if (args.Count < 2 || string.IsNullOrWhiteSpace(args[1])) return false;
        for (var i = 2; i < args.Count; i++)
        {
            if (args[i] == "--force")
            {
                if (force)
                {
                    error = "--force was given more than once";
                    return false;
                }

                force = true;
                continue;
            }

            if (args[i] is not ("--markdown" or "-o"))
            {
                error = $"Unexpected argument for --raw-survey: {args[i]}";
                return false;
            }

            if (i + 1 >= args.Count || string.IsNullOrWhiteSpace(args[i + 1]) || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                error = $"{args[i]} needs an output file path";
                return false;
            }

            if (markdownPath is not null)
            {
                error = "--markdown was given more than once";
                return false;
            }

            markdownPath = args[++i];
        }

        if (force && markdownPath is null)
        {
            error = "--force only applies together with --markdown";
            return false;
        }

        return true;
    }

    /// <summary>
    /// The report path must be a <c>.md</c> file outside the surveyed folder, and an existing file is only replaced with
    /// <c>--force</c>: <c>--markdown D:\RAW\IMG_0001.CR2</c> must never overwrite a RAW (T-B-04). Returns the problem, or null.
    /// </summary>
    internal static string? ValidateMarkdownPath(string markdownPath, string surveyDir, bool force)
    {
        if (!string.Equals(Path.GetExtension(markdownPath), ".md", StringComparison.OrdinalIgnoreCase))
            return $"--markdown must name a .md file (got '{markdownPath}')";
        if (Directory.Exists(markdownPath)) return $"--markdown '{markdownPath}' is a directory";
        if (ToolPathGuard.IsSameOrUnder(Path.GetDirectoryName(Path.GetFullPath(markdownPath)) ?? markdownPath, surveyDir))
            return $"--markdown '{markdownPath}' must not be inside the surveyed folder '{surveyDir}'";
        if (File.Exists(markdownPath) && !force)
            return $"--markdown '{markdownPath}' already exists; pass --force to replace it";
        return null;
    }

    /// <summary>Only RAW containers are surveyed: a .jpg/.jpeg in the folder is neither a RAW nor a RAW-container error source.</summary>
    internal static bool IsSurveyable(string path) => RawFileTypes.IsRawExtension(path);

    public static async Task<int> RunAsync(string[] args, Func<string, long>? readLength = null)
    {
        if (!TryParseArgs(args, out var markdownPath, out var force, out var argError))
        {
            if (argError is not null) Console.Error.WriteLine(argError);
            Console.Error.WriteLine(Usage);
            return 2;
        }

        var dir = args[1];
        if (!Directory.Exists(dir))
        {
            Console.Error.WriteLine($"Directory not found: {dir}");
            return 2;
        }

        if (markdownPath is not null && ValidateMarkdownPath(markdownPath, dir, force) is { } markdownProblem)
        {
            Console.Error.WriteLine(markdownProblem);
            return 2;
        }

        SurveySummary summary;
        try
        {
            summary = await SurveyDirectoryAsync(dir, readLength);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Could not enumerate {dir}: {ex.Message}");
            return 2;
        }

        PrintConsoleSummary(summary);

        var exitCode = ComputeExitCode(summary);
        if (!string.IsNullOrWhiteSpace(markdownPath))
        {
            try
            {
                var md = GenerateMarkdownReport(summary);
                await File.WriteAllTextAsync(markdownPath, md, Encoding.UTF8);
                Console.WriteLine($"Markdown report written to: {markdownPath}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"Could not write the markdown report {markdownPath}: {ex.Message}");
                return 2;
            }
        }

        foreach (var failed in summary.Files.Where(f => f.Error is not null))
            Console.Error.WriteLine($"FAILED {failed.FileName}: {failed.Error}");
        if (summary.Files.Count == 0) Console.Error.WriteLine($"No surveyable files found in {dir}.");
        return exitCode;
    }

    /// <summary>1 when nothing was surveyed or any file could not be read/parsed; 0 only for a fully surveyed corpus.</summary>
    internal static int ComputeExitCode(SurveySummary summary) =>
        summary.Files.Count == 0 || summary.Files.Any(f => f.Error is not null) ? 1 : 0;

    /// <summary>The size of a file in bytes; the seam lets tests make the stat fail for one file (it vanishes or is locked mid-survey).</summary>
    internal static long ReadFileLength(string path) => new FileInfo(path).Length;

    public static async Task<SurveySummary> SurveyDirectoryAsync(string dir, Func<string, long>? readLength = null)
    {
        var files = Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly)
            .Where(IsSurveyable)
            .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
            .ToList();

        var windowsVersion = Environment.OSVersion.VersionString;
        var codecInfo = ProbeWicRawCodecInfo();

        var results = new List<SurveyFileResult>();
        foreach (var file in files)
        {
            // Safety net: whatever one file throws becomes that file's error row, so the report of the others is always written.
            SurveyFileResult res;
            try { res = await SurveyFileAsync(file, readLength); }
            catch (Exception ex) when (RawDecoderBenchmark.IsMeasurementFailure(ex))
            {
                res = FailedFile(file, $"{ex.GetType().Name}: {ex.Message}");
            }

            results.Add(res);
        }

        return new SurveySummary(windowsVersion, codecInfo, results);
    }

    private static SurveyFileResult FailedFile(string filePath, string error) =>
        new(filePath, Path.GetFileName(filePath), FormatOf(filePath), 0, 1, 0, 0, [], false, 0, 0, 1, null, false, 0, 0, 0, null, false, "unknown", error);

    private static string FormatOf(string filePath)
    {
        var ext = Path.GetExtension(filePath).TrimStart('.').ToUpperInvariant();
        return ext switch
        {
            "JPG" or "JPEG" => "JPEG",
            _ => ext
        };
    }

    public static async Task<SurveyFileResult> SurveyFileAsync(string filePath, Func<string, long>? readLength = null)
    {
        var ext = Path.GetExtension(filePath).TrimStart('.').ToUpperInvariant();
        var format = ext switch
        {
            "CR2" => "CR2",
            "CR3" => "CR3",
            "NEF" => "NEF",
            "ARW" => "ARW",
            "DNG" => "DNG",
            "RAF" => "RAF",
            "ORF" => "ORF",
            "RW2" => "RW2",
            "JPG" or "JPEG" => "JPEG",
            _ => ext
        };

        IReadOnlyList<EmbeddedJpeg> embeddedJpegs = [];
        string? fileError = null;
        // The size is read once, up front and failure-tolerant: a file that vanished or is locked between the scan steps must
        // not abort the whole survey (and lose the report of every file already surveyed). Unknown size = 0 plus an error row.
        long fileSize = 0;
        try
        {
            fileSize = (readLength ?? ReadFileLength)(filePath);
        }
        catch (Exception ex) when (RawDecoderBenchmark.IsMeasurementFailure(ex))
        {
            fileError = $"size: {ex.GetType().Name}: {ex.Message}";
        }

        try
        {
            embeddedJpegs = ScanEmbeddedJpegs(filePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            fileError ??= $"{ex.GetType().Name}: {ex.Message}";
        }

        // Sensor size and orientation come from the RAW container readers (never from WIC: without a RAW codec WIC only sees the preview).
        var container = ReadContainer(filePath, out var containerError);
        fileError ??= containerError;


        // WIC probe
        var wic = new WicDirectDecoder();
        bool wicReadSuccess = false;
        int wicInfoW = 0, wicInfoH = 0, wicInfoOrient = 1;
        string? wicInfoErr = null;

        try
        {
            var info = wic.ReadInfo(filePath);
            wicReadSuccess = true;
            wicInfoW = info.Width;
            wicInfoH = info.Height;
            wicInfoOrient = info.Orientation;
        }
        catch (Exception ex)
        {
            wicInfoErr = $"{ex.GetType().Name}: {ex.Message}";
        }

        bool wicDecodeSuccess = false;
        int wicDecodedW = 0, wicDecodedH = 0;
        double medianTimeMs = 0;
        string? wicDecodeErr = null;

        if (wicReadSuccess)
        {
            try
            {
                var times = new List<double>(3);
                IDecodedImage? lastImage = null;
                for (var run = 0; run < 3; run++)
                {
                    var sw = Stopwatch.StartNew();
                    lastImage = wic.Decode(new DecodeRequest(filePath, TargetWidth: 0, TargetHeight: 0, Priority: SourceReadPriority.Viewer));
                    sw.Stop();
                    times.Add(sw.Elapsed.TotalMilliseconds);
                }

                if (lastImage != null)
                {
                    wicDecodeSuccess = true;
                    wicDecodedW = lastImage.PixelWidth;
                    wicDecodedH = lastImage.PixelHeight;
                    times.Sort();
                    medianTimeMs = times[1]; // median of 3
                }
            }
            catch (Exception ex)
            {
                wicDecodeErr = $"{ex.GetType().Name}: {ex.Message}";
            }
        }
        else
        {
            wicDecodeErr = wicInfoErr;
        }

        // Sensor / container dimensions: container readers for RAW files; plain JPEG files have no container, their image size is the frame size.
        int sensorW = 0, sensorH = 0, containerOrient = 1;
        var sensorSource = "unknown";
        if (container is not null)
        {
            containerOrient = container.Orientation;
            if (container.SensorWidth > 0 && container.SensorHeight > 0)
            {
                sensorW = container.SensorWidth;
                sensorH = container.SensorHeight;
                sensorSource = "container";
            }
        }
        else if (format == "JPEG" && wicReadSuccess)
        {
            sensorW = wicInfoW;
            sensorH = wicInfoH;
            containerOrient = wicInfoOrient;
            sensorSource = "jpeg";
        }

        // If embedded JPEGs exist, check if WIC decoded only the preview
        var largestPreview = embeddedJpegs.OrderByDescending(j => (long)j.Width * j.Height).FirstOrDefault();
        bool isPreviewOnly = false;
        if (wicDecodeSuccess && largestPreview != null)
        {
            // Decoded dimensions match largest preview, while file is a RAW format
            if (wicDecodedW == largestPreview.Width && wicDecodedH == largestPreview.Height)
            {
                // If sensor dimensions equal preview, check if preview is full size
                isPreviewOnly = true;
            }
        }

        return new SurveyFileResult(
            filePath,
            Path.GetFileName(filePath),
            format,
            fileSize,
            containerOrient,
            sensorW,
            sensorH,
            embeddedJpegs,
            wicReadSuccess,
            wicInfoW,
            wicInfoH,
            wicInfoOrient,
            wicInfoErr,
            wicDecodeSuccess,
            wicDecodedW,
            wicDecodedH,
            Math.Round(medianTimeMs, 1),
            wicDecodeErr,
            isPreviewOnly,
            sensorSource,
            fileError);
    }

    /// <summary>
    /// Scans a file for all embedded JPEG streams by locating SOI markers (FF D8) followed by valid JPEG segments,
    /// parsing SOF0/1/2 dimensions, APP1 EXIF color-space hint, and locating EOI (FF D9).
    /// </summary>
    public static IReadOnlyList<EmbeddedJpeg> ScanEmbeddedJpegs(string filePath)
    {
        var results = new List<EmbeddedJpeg>();
        using var stream = File.OpenRead(filePath);
        var fileLen = stream.Length;
        if (fileLen < 4) return results;

        // Read in 1MB chunks with overlap to detect markers across boundaries
        const int chunkSize = 1024 * 1024;
        const int overlap = 64 * 1024;
        var buffer = new byte[chunkSize + overlap];

        long streamPos = 0;
        int index = 0;
        long lastFoundEnd = 0;

        while (streamPos < fileLen)
        {
            stream.Position = streamPos;
            int bytesRead = stream.Read(buffer, 0, buffer.Length);
            if (bytesRead < 4) break;

            int searchLimit = bytesRead - 3;
            for (int i = 0; i < searchLimit; i++)
            {
                long currentOffset = streamPos + i;
                if (currentOffset < lastFoundEnd) continue;

                // Check SOI marker: FF D8
                if (buffer[i] == 0xFF && buffer[i + 1] == 0xD8)
                {
                    // Check next marker: FF followed by marker type
                    if (buffer[i + 2] == 0xFF && IsValidLeadingMarker(buffer[i + 3]))
                    {
                        // Found possible JPEG start. Parse from this offset.
                        if (TryParseJpegAt(stream, currentOffset, fileLen, out var jpeg))
                        {
                            results.Add(jpeg with { Index = index++ });
                            lastFoundEnd = jpeg.Offset + jpeg.Length;
                            // Advance search position
                            var skipBytes = (int)(lastFoundEnd - streamPos);
                            if (skipBytes > i)
                            {
                                i = skipBytes - 1;
                            }
                        }
                    }
                }
            }

            streamPos += (chunkSize);
        }

        return results;
    }

    private static bool IsValidLeadingMarker(byte b)
    {
        // Valid markers right after SOI: APP0-APP15 (E0-EF), DQT (DB), DHT (C4), SOF (C0-C3), COM (FE)
        return (b >= 0xE0 && b <= 0xEF) || b == 0xDB || b == 0xC4 || (b >= 0xC0 && b <= 0xC3) || b == 0xFE;
    }

    private static bool TryParseJpegAt(Stream stream, long startOffset, long maxLen, out EmbeddedJpeg jpeg)
    {
        jpeg = default!;
        stream.Position = startOffset;

        int b1 = stream.ReadByte();
        int b2 = stream.ReadByte();
        if (b1 != 0xFF || b2 != 0xD8) return false;

        int width = 0;
        int height = 0;
        string colorSpace = "sRGB";
        long endOffset = -1;

        // Bounded marker walk
        try
        {
            while (stream.Position < maxLen)
            {
                int markerPrefix = stream.ReadByte();
                if (markerPrefix == -1) break;
                if (markerPrefix != 0xFF) continue;

                int marker = stream.ReadByte();
                while (marker == 0xFF) marker = stream.ReadByte(); // skip fill bytes
                if (marker == -1) break;

                if (marker == 0xD8) continue; // nested SOI
                if (marker is 0xD9 or 0x00) // EOI
                {
                    if (marker == 0xD9)
                    {
                        endOffset = stream.Position;
                        break;
                    }
                    continue;
                }

                if (marker is >= 0xD0 and <= 0xD7) continue; // RST markers have no payload

                // Markers with length: read 2-byte big-endian length
                int lenHi = stream.ReadByte();
                int lenLo = stream.ReadByte();
                if (lenHi == -1 || lenLo == -1) break;
                int segLen = (lenHi << 8) | lenLo;
                if (segLen < 2) break;
                int payloadLen = segLen - 2;

                long segStart = stream.Position;

                // SOF0, SOF1, SOF2 (Baseline, Extended, Progressive)
                if (marker is 0xC0 or 0xC1 or 0xC2)
                {
                    if (payloadLen >= 6)
                    {
                        int precision = stream.ReadByte();
                        int hHi = stream.ReadByte();
                        int hLo = stream.ReadByte();
                        int wHi = stream.ReadByte();
                        int wLo = stream.ReadByte();
                        height = (hHi << 8) | hLo;
                        width = (wHi << 8) | wLo;
                    }
                }
                else if (marker == 0xE1 && payloadLen >= 14) // APP1 EXIF
                {
                    var exifHeader = new byte[6];
                    int readExif = stream.Read(exifHeader, 0, 6);
                    if (readExif == 6 && exifHeader[0] == 'E' && exifHeader[1] == 'x' && exifHeader[2] == 'i' && exifHeader[3] == 'f')
                    {
                        // Check for Adobe RGB hint in payload
                        var remBytes = new byte[Math.Min(payloadLen - 6, 2048)];
                        int remRead = stream.Read(remBytes, 0, remBytes.Length);
                        var remSpan = remBytes.AsSpan(0, remRead);
                        if (remSpan.IndexOf(Encoding.ASCII.GetBytes("Adobe RGB")) >= 0 ||
                            remSpan.IndexOf(Encoding.ASCII.GetBytes("R03")) >= 0)
                        {
                            colorSpace = "AdobeRGB";
                        }
                    }
                }
                else if (marker == 0xDA) // SOS (Start of Scan) - entropy data follows
                {
                    // Scan entropy data until EOI (FF D9)
                    stream.Position = segStart + payloadLen;
                    endOffset = ScanForEoi(stream, maxLen);
                    break;
                }

                stream.Position = segStart + payloadLen;
            }
        }
        catch (IOException)
        {
            // stream read error or truncated
        }

        // A candidate without an EOI is truncated or not a JPEG: accepting it would report a "valid" preview running to
        // the end of the file and hide every later preview of that file (T-B-10).
        if (width > 0 && height > 0 && endOffset > startOffset)
        {
            jpeg = new EmbeddedJpeg(0, startOffset, endOffset - startOffset, width, height, colorSpace);
            return true;
        }

        return false;
    }

    private static long ScanForEoi(Stream stream, long maxLen)
    {
        const int bufSize = 32 * 1024;
        var buf = new byte[bufSize];
        int prev = 0;

        while (stream.Position < maxLen)
        {
            long chunkStart = stream.Position;
            int read = stream.Read(buf, 0, buf.Length);
            if (read <= 0) break;

            for (int i = 0; i < read; i++)
            {
                int curr = buf[i];
                if (prev == 0xFF && curr == 0xD9)
                {
                    return chunkStart + i + 1;
                }
                prev = curr;
            }
        }

        return -1; // no EOI before the end of the stream: the caller rejects the candidate
    }

    private static string ProbeWicRawCodecInfo()
    {
        try
        {
            var sb = new StringBuilder();
            // Check registered WIC decoders via registry
            using var catKey = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(@"CLSID\{7ED96837-96F0-4812-B211-F13C24117ED3}\Instance");
            if (catKey != null)
            {
                foreach (var sub in catKey.GetSubKeyNames())
                {
                    using var k = catKey.OpenSubKey(sub);
                    var friendly = k?.GetValue("FriendlyName")?.ToString();
                    var exts = k?.GetValue("FileExtensions")?.ToString();
                    if (!string.IsNullOrEmpty(friendly) && (exts?.Contains(".cr", StringComparison.OrdinalIgnoreCase) == true ||
                                                           exts?.Contains(".nef", StringComparison.OrdinalIgnoreCase) == true ||
                                                           exts?.Contains(".arw", StringComparison.OrdinalIgnoreCase) == true ||
                                                           exts?.Contains(".dng", StringComparison.OrdinalIgnoreCase) == true ||
                                                           friendly.Contains("raw", StringComparison.OrdinalIgnoreCase)))
                    {
                        sb.Append(friendly).Append(" (").Append(exts).Append("); ");
                    }
                }
            }

            return sb.Length > 0 ? sb.ToString().TrimEnd(' ', ';') : "No WIC RAW codec registered in Windows Imaging Component";
        }
        catch (Exception ex)
        {
            return $"Error probing WIC registry: {ex.Message}";
        }
    }

    /// <summary>Reads the RAW container header for RAW extensions; null for non-RAW files (plain JPEG) or when parsing failed (error set).</summary>
    internal static RawContainerInfo? ReadContainer(string filePath, out string? error)
    {
        error = null;
        if (!RawFileTypes.IsRawExtension(filePath)) return null;
        try
        {
            using var source = new SourceRawHeaderSource(filePath, PhysicalSourceReader.Instance);
            var probe = source.Read(0, RawContainerLimits.InitialProbeLength(source.Length));
            var reader = new RawContainerReaderRegistry().FindReader(probe, Path.GetExtension(filePath))
                ?? throw new NotSupportedException($"No RAW container reader accepted {Path.GetExtension(filePath)}");
            return reader.Read(source, CancellationToken.None);
        }
        catch (Exception ex) when (RawDecoderBenchmark.IsMeasurementFailure(ex))
        {
            error = $"container: {ex.GetType().Name}: {ex.Message}";
            return null;
        }
    }

    private static long LongSide(int width, int height) => Math.Max(width, height);

    private static EmbeddedJpeg? LargestPreview(SurveyFileResult f) =>
        f.EmbeddedJpegs.OrderByDescending(j => (long)j.Width * j.Height).FirstOrDefault();

    private static void PrintConsoleSummary(SurveySummary summary)
    {
        Console.WriteLine("================================================================================");
        Console.WriteLine("                     CAMERA RAW SUPPORT SURVEY (RAW-01)                        ");
        Console.WriteLine("================================================================================");
        Console.WriteLine($"OS: {summary.WindowsVersion}");
        Console.WriteLine($"WIC RAW Codecs: {summary.WicRawCodecInfo}");
        Console.WriteLine($"Total Files Surveyed: {summary.Files.Count}");
        Console.WriteLine("--------------------------------------------------------------------------------");

        // Console output is a diagnostic log: format numbers with the invariant culture, never the user locale.
        var inv = CultureInfo.InvariantCulture;
        foreach (var f in summary.Files)
        {
            var largestPreview = LargestPreview(f);
            var previewStr = largestPreview != null ? string.Create(inv, $"{largestPreview.Width}x{largestPreview.Height} ({largestPreview.Length / 1024} KB)") : "None";
            var wicStr = f.WicDecodeSuccess ? string.Create(inv, $"{f.WicDecodedWidth}x{f.WicDecodedHeight} in {f.WicMedianDecodeTimeMs:F1}ms") : (f.WicDecodeError ?? "Failed");
            var sensorStr = f.SensorWidth > 0 ? string.Create(inv, $"{f.SensorWidth}x{f.SensorHeight} ({f.SensorSource})") : "unknown";

            Console.WriteLine(string.Create(inv, $"[{f.Format}] {f.FileName} ({f.FileSizeBytes / (1024 * 1024.0):F1} MB)"));
            Console.WriteLine(string.Create(inv, $"  Previews ({f.EmbeddedJpegs.Count}): {string.Join(", ", f.EmbeddedJpegs.Select(j => $"{j.Width}x{j.Height}@{j.Offset}"))}"));
            Console.WriteLine($"  Largest Preview: {previewStr} | ColorSpace: {largestPreview?.ColorSpaceHint ?? "N/A"} | Sensor: {sensorStr}");
            Console.WriteLine($"  WIC Direct: {wicStr} | PreviewOnly: {f.WicDecodedPreviewOnly}");
            if (f.Error is not null) Console.WriteLine($"  ERROR: {f.Error}");
        }
    }

    private static double? MedianOf(List<double> values)
    {
        if (values.Count == 0) return null;
        var sorted = values.OrderBy(v => v).ToArray();
        return sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2d;
    }

    /// <summary>
    /// Conclusions derived only from what was measured in this corpus. Nothing here is a fixed claim: with an empty or unrepresentative
    /// corpus the text says that no conclusion can be drawn.
    /// </summary>
    internal static IReadOnlyList<string> BuildConclusions(SurveySummary summary)
    {
        var inv = CultureInfo.InvariantCulture;
        var raws = summary.Files.Where(f => f.Format != "JPEG" && f.Error is null).ToList();
        var lines = new List<string>();

        // 1. Disk read reduction: bytes of the largest embedded preview relative to the file (bytes only, no timing claim).
        var ratios = raws.Where(f => f.FileSizeBytes > 0 && LargestPreview(f) is not null)
            .Select(f => LargestPreview(f)!.Length * 100.0 / f.FileSizeBytes).ToList();
        if (ratios.Count == 0)
        {
            lines.Add("**Disk Read Reduction (Goal 1):** no RAW file with an embedded JPEG preview was surveyed, so no reduction can be concluded from this corpus.");
        }
        else
        {
            var median = MedianOf(ratios)!.Value;
            var factor = median > 0 ? 100.0 / median : double.PositiveInfinity;
            lines.Add(string.Create(inv, $"**Disk Read Reduction (Goal 1):** across {ratios.Count} RAW file(s) with previews, the largest embedded preview is {ratios.Min():F1}%-{ratios.Max():F1}% of the file size (median {median:F1}%), i.e. reading only that byte range reads about {factor:F1}x fewer bytes at the median. This is a byte ratio, not a measured time."));
        }

        // 2. WIC coverage versus full decode need.
        if (raws.Count == 0)
        {
            lines.Add("**LibRaw Full Decode (Q-RAW-02):** no RAW file was surveyed, so WIC coverage cannot be judged.");
        }
        else
        {
            var wicOk = raws.Count(f => f.WicDecodeSuccess);
            var previewOnly = raws.Count(f => f.WicDecodeSuccess && f.WicDecodedPreviewOnly);
            var failedFormats = raws.Where(f => !f.WicDecodeSuccess).Select(f => f.Format).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
            var text = string.Create(inv, $"**LibRaw Full Decode (Q-RAW-02):** WIC decoded {wicOk}/{raws.Count} surveyed RAW file(s) on this machine, {previewOnly} of them only at embedded-preview resolution");
            if (failedFormats.Count > 0) text += $"; it failed for format(s) {string.Join(", ", failedFormats)}";
            text += wicOk < raws.Count || previewOnly > 0
                ? ". WIC therefore does not give reliable full-resolution decoding here, which supports a bundled full decoder (LibRaw)."
                : ". WIC produced full-size decodes for every surveyed RAW file here, so this corpus alone does not show a need for LibRaw on this machine.";
            lines.Add(text);
        }

        // 3. Preview versus sensor size, using the container reader sensor dimensions.
        var known = raws.Where(f => f.SensorWidth > 0 && f.SensorHeight > 0 && LargestPreview(f) is not null).ToList();
        if (known.Count == 0)
        {
            lines.Add("**100% Zoom Sensor Dimension (Q-RAW-03):** the RAW container readers reported no sensor dimensions together with a preview for this corpus, so preview versus sensor size cannot be concluded.");
        }
        else
        {
            var smaller = known.Where(f => LongSide(LargestPreview(f)!.Width, LargestPreview(f)!.Height) < LongSide(f.SensorWidth, f.SensorHeight) * 0.95).ToList();
            if (smaller.Count == 0)
            {
                lines.Add(string.Create(inv, $"**100% Zoom Sensor Dimension (Q-RAW-03):** for all {known.Count} file(s) with known sensor size the largest preview is within 5% of the sensor long side (sensor size read from the RAW container). No preview/sensor divergence was observed in this corpus."));
            }
            else
            {
                var formats = string.Join(", ", smaller.Select(f => f.Format).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal));
                lines.Add(string.Create(inv, $"**100% Zoom Sensor Dimension (Q-RAW-03):** the largest preview is smaller than the sensor size (long side, container-reported) for {smaller.Count}/{known.Count} file(s) with known sensor size (formats: {formats}); for those the UI must show the sensor dimensions and indicate preview status when upscaled."));
            }
        }

        return lines;
    }

    public static string GenerateMarkdownReport(SurveySummary summary)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Camera RAW Survey & WIC Probe Report (RAW-01)");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"**Date:** {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC | **OS:** {summary.WindowsVersion}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"**WIC RAW Codec Status:** `{summary.WicRawCodecInfo}`");
        sb.AppendLine();
        sb.AppendLine("## 1. Executive Summary");
        sb.AppendLine();

        var byFormat = summary.Files.GroupBy(f => f.Format).OrderBy(g => g.Key, StringComparer.Ordinal).ToList();
        var fullSizePreviews = 0;
        var smallerPreviews = 0;
        var unknownSize = 0;
        var adobeRgbCount = 0;
        var wicDecodes = 0;

        foreach (var f in summary.Files)
        {
            var largest = LargestPreview(f);
            if (largest != null && largest.ColorSpaceHint == "AdobeRGB") adobeRgbCount++;
            if (largest == null || f.SensorWidth <= 0) unknownSize++;
            else if (LongSide(largest.Width, largest.Height) >= LongSide(f.SensorWidth, f.SensorHeight) * 0.95) fullSizePreviews++;
            else smallerPreviews++;

            if (f.WicDecodeSuccess) wicDecodes++;
        }

        sb.AppendLine(CultureInfo.InvariantCulture, $"- **Total samples surveyed:** {summary.Files.Count} across {byFormat.Count} formats ({string.Join(", ", byFormat.Select(g => g.Key))}).");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **Previews at or near sensor resolution:** {fullSizePreviews}/{summary.Files.Count} file(s) (largest preview long side >= 95% of the container-reported sensor long side).");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **Previews smaller than the sensor:** {smallerPreviews}/{summary.Files.Count} file(s); 100% inspection of these needs a full decode.");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **Unknown preview or sensor size:** {unknownSize}/{summary.Files.Count} file(s) (no embedded preview found, or the container reader reported no sensor dimensions).");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **WIC decode coverage:** {wicDecodes}/{summary.Files.Count} file(s) decoded through Windows Imaging Component on this machine (see the WIC codec status above).");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **Adobe RGB previews:** {adobeRgbCount} sample(s) carry an Adobe RGB hint in the preview EXIF (relevant to Q-RAW-06 colour handling).");
        var failures = summary.Files.Where(f => f.Error is not null).ToList();
        if (failures.Count > 0)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"- **Files that could not be surveyed:** {failures.Count}: {string.Join("; ", failures.Select(f => $"`{f.FileName}` ({f.Error})"))}.");
        }

        sb.AppendLine();

        sb.AppendLine("## 2. Per-Format Survey Tables");
        sb.AppendLine();

        foreach (var g in byFormat)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"### Format: {g.Key}");
            sb.AppendLine();
            sb.AppendLine("| Camera / Sample | Size (MB) | Previews Found | Largest Preview | Sensor (container) | Preview Size Ratio | WIC ReadInfo | WIC Decode (3-run median) | Preview Only? |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|---|");

            foreach (var f in g)
            {
                var largest = LargestPreview(f);
                var largestStr = largest != null ? $"{largest.Width}×{largest.Height}" : "None";
                var sensorStr = f.SensorWidth > 0 ? $"{f.SensorWidth}×{f.SensorHeight}" : "unknown";
                var ratioStr = (largest != null && f.SensorWidth > 0)
                    ? string.Create(CultureInfo.InvariantCulture, $"{LongSide(largest.Width, largest.Height) * 100.0 / LongSide(f.SensorWidth, f.SensorHeight):F0}%")
                    : "n/a";

                var wicInfo = f.WicReadInfoSuccess ? $"{f.WicInfoWidth}×{f.WicInfoHeight} (orient {f.WicInfoOrientation})" : "Unsupported";
                var wicDecode = f.WicDecodeSuccess ? string.Create(CultureInfo.InvariantCulture, $"{f.WicDecodedWidth}×{f.WicDecodedHeight} ({f.WicMedianDecodeTimeMs:F1} ms)") : "Failed/Unsupported";
                var prevOnly = f.WicDecodedPreviewOnly ? "Yes (preview)" : (f.WicDecodeSuccess ? "Full" : "N/A");

                sb.AppendLine(CultureInfo.InvariantCulture, $"| `{f.FileName}` | {f.FileSizeBytes / (1024 * 1024.0):F1} | {f.EmbeddedJpegs.Count} ({string.Join(", ", f.EmbeddedJpegs.Select(j => $"{j.Width}×{j.Height}"))}) | {largestStr} | {sensorStr} | {ratioStr} | {wicInfo} | {wicDecode} | {prevOnly} |");
            }

            sb.AppendLine();
        }

        sb.AppendLine("## 3. Conclusions for RAW Architecture (RAW-10..RAW-70)");
        sb.AppendLine();
        var conclusions = BuildConclusions(summary);
        for (var i = 0; i < conclusions.Count; i++)
            sb.AppendLine(CultureInfo.InvariantCulture, $"{i + 1}. {conclusions[i]}");
        sb.AppendLine();

        return sb.ToString();
    }
}

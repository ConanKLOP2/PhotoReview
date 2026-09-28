using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using PhotoReview.Core;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.FileActions;
using PhotoReview.Core.IO;
using PhotoReview.Core.Model;
using Xunit.Abstractions;

namespace PhotoReview.Core.Tests.FileActions;

/// <summary>
/// CORE-08 measurement (manual): single-JSON-pass parsing and compaction on a synthetic ~5 MB journal.
/// Run: dotnet test tests/PhotoReview.Core.Tests -c Release --filter "Category=Manual&amp;FullyQualifiedName~JournalParseBenchmark" --logger "console;verbosity=detailed"
/// </summary>
[Trait("Category", "Manual")]
public sealed class JournalParseBenchmarkTests(ITestOutputHelper output)
{
    // ---- Legacy oracle (same copy as JournalLineParserTests): the two-pass reader this branch replaced. ----
    private static class LegacyJournalLineParser
    {
        public static JournalEntry? TryParse(string line)
        {
            try
            {
                if (!HasRecognizedEnums(line)) return null;
                var entry = JsonSerializer.Deserialize<JournalEntry>(line);
                return entry is not null && !string.IsNullOrEmpty(entry.Id) && entry.Source is not null ? entry : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static bool HasRecognizedEnums(string line)
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && IsKnown<FileOperationType>(root, nameof(JournalEntry.Type), "Delete")
                && IsKnown<JournalState>(root, nameof(JournalEntry.State), alias: null);
        }

        private static bool IsKnown<T>(JsonElement root, string property, string? alias) where T : struct, Enum
        {
            if (!root.TryGetProperty(property, out var value)) return false;
            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() is { } text
                    && (Enum.TryParse<T>(text.Trim(), ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
                        || (alias is not null && string.Equals(text.Trim(), alias, StringComparison.OrdinalIgnoreCase))),
                JsonValueKind.Number => value.TryGetInt32(out var number) && Enum.IsDefined(typeof(T), number),
                _ => false,
            };
        }
    }

    private static readonly DateTime BaseTime = new(2026, 9, 25, 0, 0, 0, DateTimeKind.Utc);
    private const long TargetBytes = 5L * 1024 * 1024;

    private static byte[] GenerateJournal()
    {
        var types = new[] { FileOperationType.Move, FileOperationType.Copy, FileOperationType.Recycle };
        using var ms = new MemoryStream();
        var i = 0;
        while (ms.Length < TargetBytes)
        {
            var id = $"bench-{i:D7}";
            var type = types[i % types.Length];
            var source = $@"C:\photos\session-{i % 200}\padding-directory-name\{id}.jpg";
            var destination = type == FileOperationType.Recycle ? null : $@"C:\photos\sel\session-{i % 200}\padding-directory-name\{id}.jpg";
            var prepared = new JournalEntry(id, type, JournalState.Prepared, source, destination, 123456, BaseTime.AddSeconds(i), BaseTime.AddSeconds(i));
            var committed = prepared with { State = JournalState.Committed, TimestampUtc = BaseTime.AddSeconds(i + 1) };
            var preparedLine = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(prepared) + "\r\n");
            var committedLine = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(committed) + "\r\n");
            ms.Write(preparedLine);
            ms.Write(committedLine);
            i++;
        }
        return ms.ToArray();
    }

    private static (double MinMs, double MedianMs) Time(Action action, int warmup, int measured)
    {
        for (var i = 0; i < warmup; i++) action();
        var samples = new List<double>();
        var sw = new Stopwatch();
        for (var i = 0; i < measured; i++)
        {
            sw.Restart();
            action();
            sw.Stop();
            samples.Add(sw.Elapsed.TotalMilliseconds);
        }
        samples.Sort();
        var median = samples[samples.Count / 2];
        return (samples[0], median);
    }

    [Fact(DisplayName = "Benchmark: legacy two-pass vs single-pass line parsing, full-journal read, and compaction on a ~5 MB journal")]
    [Trait("Category", "Manual")]
    public void Benchmark_ParseAndCompaction_ReportsTimings()
    {
        var bytes = GenerateJournal();
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"Generated journal: {bytes.Length:N0} bytes, {bytes.Count(b => b == (byte)'\n'):N0} lines"));

        var lines = Encoding.UTF8.GetString(bytes).Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);

        var (legacyMin, legacyMedian) = Time(() =>
        {
            var count = 0;
            foreach (var line in lines)
            {
                if (LegacyJournalLineParser.TryParse(line) is not null) count++;
            }
        }, warmup: 2, measured: 5);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"(a) Legacy two-pass parse of every line: min={legacyMin:F2} ms, median={legacyMedian:F2} ms"));

        var (singlePassMin, singlePassMedian) = Time(() =>
        {
            var count = 0;
            foreach (var line in lines)
            {
                if (JournalLineParser.TryParse(line) is not null) count++;
            }
        }, warmup: 2, measured: 5);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"(b) JournalLineParser.TryParse of every line: min={singlePassMin:F2} ms, median={singlePassMedian:F2} ms"));

        using var root = new TempRoot("journal-benchmark");
        var paths = new AppPaths(root.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.JournalFile)!);
        File.WriteAllBytes(paths.JournalFile, bytes);
        var journal = new OperationJournal(paths, new PhysicalFileSystem(), new SystemClock());

        var (readMin, readMedian) = Time(() => journal.ReadPendingAndFailedOperations(), warmup: 2, measured: 5);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"(c) OperationJournal.ReadPendingAndFailedOperations on the file: min={readMin:F2} ms, median={readMedian:F2} ms"));

        // (d) TryCompact on a fresh copy each time (compaction rewrites the file, so warm-up/measured runs each need their own copy).
        var compactSamples = new List<double>();
        long bytesBefore = 0, bytesAfter = 0;
        for (var i = 0; i < 7; i++)
        {
            File.WriteAllBytes(paths.JournalFile, bytes);
            var compactJournal = new OperationJournal(paths, new PhysicalFileSystem(), new SystemClock(),
                compactionFiles: new PhysicalJournalCompactionFiles());
            var sw = Stopwatch.StartNew();
            var result = compactJournal.TryCompact();
            sw.Stop();
            Assert.Equal(JournalCompactionOutcome.Compacted, result.Outcome);
            if (i >= 2) compactSamples.Add(sw.Elapsed.TotalMilliseconds);
            bytesBefore = result.BytesBefore;
            bytesAfter = result.BytesAfter;
        }
        compactSamples.Sort();
        var compactMin = compactSamples[0];
        var compactMedian = compactSamples[compactSamples.Count / 2];
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"(d) TryCompact: min={compactMin:F2} ms, median={compactMedian:F2} ms, bytes {bytesBefore:N0} -> {bytesAfter:N0}"));
    }
}

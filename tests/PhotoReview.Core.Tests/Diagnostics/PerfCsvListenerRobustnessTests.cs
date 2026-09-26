using System.Globalization;
using System.IO;
using System.Text;
using PhotoReview.Core.Diagnostics;

namespace PhotoReview.Core.Tests.Diagnostics;

/// <summary>Robustness of the perf CSV listener: field escaping round trip, culture, concurrent producers, dispose under load.</summary>
[Collection("GlobalState")] // listens to the process-wide PhotoReviewPerf EventSource
public sealed class PerfCsvListenerRobustnessTests
{
    /// <summary>Minimal RFC 4180 reader for one line.</summary>
    private static List<string> SplitCsvLine(string line)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else sb.Append(c);
            }
            else if (c == '"' && sb.Length == 0) quoted = true;
            else if (c == ',') { fields.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        fields.Add(sb.ToString());
        return fields;
    }

    private static string Flatten(string value) => value.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\r', ' ').Replace('\n', ' ');

    [Theory(DisplayName = "Fuzz: CsvEscape output is always one physical line that parses back to the flattened input")]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void CsvEscape_RoundTrips(int seed)
    {
        var r = new Random(seed);
        string[] pieces = [",", "\"", "\"\"", "\r", "\n", "\r\n", "a", " ", "\t", "é", "日", "😀", ";", "'", "=", "\0", "#"];
        for (var i = 0; i < 5000; i++)
        {
            var value = string.Concat(Enumerable.Range(0, r.Next(0, 8)).Select(_ => pieces[r.Next(pieces.Length)]));

            var escaped = PerfCsvListener.CsvEscape(value);
            var parsed = SplitCsvLine("x," + escaped + ",y");

            Assert.DoesNotContain('\n', escaped);
            Assert.DoesNotContain('\r', escaped);
            Assert.Equal(3, parsed.Count);
            Assert.Equal(Flatten(value), parsed[1]);
        }
    }

    private static (List<List<string>> Rows, long Dropped, string Text) ReadCsv(MemoryStream backing)
    {
        var text = Encoding.UTF8.GetString(backing.ToArray());
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var rows = lines.Where(l => !l.StartsWith('#') && !l.StartsWith("utcTicks,", StringComparison.Ordinal)).Select(SplitCsvLine).ToList();
        var dropped = long.Parse(lines.Last(l => l.StartsWith("# dropped=", StringComparison.Ordinal))["# dropped=".Length..], CultureInfo.InvariantCulture);
        return (rows, dropped, text);
    }

    [Theory(DisplayName = "Numbers are written culture-invariant (dot decimal, no grouping) even on de-DE / tr-TR / ar-SA machines")]
    [InlineData("de-DE")]
    [InlineData("tr-TR")]
    [InlineData("ar-SA")]
    [InlineData("fa-IR")]
    public void Rows_AreCultureInvariant(string culture)
    {
        var ci = CultureInfo.GetCultureInfo(culture);
        var previousCurrent = CultureInfo.CurrentCulture;
        var previousDefault = CultureInfo.DefaultThreadCurrentCulture;
        var backing = new MemoryStream();
        try
        {
            CultureInfo.CurrentCulture = ci;
            CultureInfo.DefaultThreadCurrentCulture = ci;
            var listener = PerfCsvListener.StartForTest(backing, capacity: 1000);

            PhotoReviewPerf.Log.KeyInput(1234567, "Right", 12.5);
            PhotoReviewPerf.Log.DiskCacheRead(2, "id", 0.125, 9876543210);

            listener.Dispose();
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCurrent;
            CultureInfo.DefaultThreadCurrentCulture = previousDefault;
        }

        var (rows, _, text) = ReadCsv(new MemoryStream(backing.ToArray()));
        var key = rows.Single(r => r[3] == "KeyInput");
        var disk = rows.Single(r => r[3] == "DiskCacheRead");
        Assert.Equal("1234567", key[4]);
        Assert.Equal("12.5", key[6]);
        Assert.Equal("0.125", disk[6]);
        Assert.Equal("9876543210", disk[7]);
        Assert.All(rows, r => Assert.Equal(11, r.Count));
        Assert.DoesNotContain("\u066B", text, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Concurrent producers: every event is either a well-formed row or counted as dropped")]
    public void ConcurrentProducers_NothingLost()
    {
        var backing = new MemoryStream();
        var listener = PerfCsvListener.StartForTest(backing, capacity: 100_000);
        const int threads = 8, perThread = 4000;

        Parallel.For(0, threads, t =>
        {
            for (var i = 0; i < perThread; i++) PhotoReviewPerf.Log.PreloadPaused(t, i);
        });
        listener.Dispose();

        var (rows, dropped, _) = ReadCsv(new MemoryStream(backing.ToArray()));
        Assert.Equal(threads * perThread, rows.Count + dropped);
        Assert.All(rows, r => Assert.Equal(11, r.Count));
        Assert.All(rows, r => Assert.Equal("PreloadPaused", r[3]));
    }

    [Fact(DisplayName = "Dispose while producers are still running: no exception, one trailer, no interleaved half rows")]
    public async Task DisposeUnderLoad_ProducesWellFormedFile()
    {
        var backing = new MemoryStream();
        var listener = PerfCsvListener.StartForTest(backing, capacity: 100_000);
        using var stop = new CancellationTokenSource();
        var errors = new List<Exception>();
        var produced = 0;
        var producers = Enumerable.Range(0, 4).Select(t => Task.Run(() =>
        {
            try
            {
                var i = 0;
                while (!stop.IsCancellationRequested) { PhotoReviewPerf.Log.PreloadPaused(t, i++); Interlocked.Increment(ref produced); }
            }
            catch (Exception e)
            {
                lock (errors) errors.Add(e);
            }
        })).ToArray();
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref produced) > 200, TimeSpan.FromSeconds(30)));

        var ex = Record.Exception(listener.Dispose);
        await stop.CancelAsync();
        await Task.WhenAll(producers);

        Assert.Null(ex);
        Assert.Empty(errors);
        var (rows, _, text) = ReadCsv(new MemoryStream(backing.ToArray()));
        Assert.All(rows, r => Assert.Equal(11, r.Count));
        Assert.Equal(1, text.Split('\n').Count(l => l.StartsWith("# dropped=", StringComparison.Ordinal)));
        Assert.StartsWith("# dropped=", text.TrimEnd().Split('\n').Last(), StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Dispose is idempotent and events after Dispose are ignored")]
    public void Dispose_Twice_AndEventsAfterwardsIgnored()
    {
        var backing = new MemoryStream();
        var listener = PerfCsvListener.StartForTest(backing, capacity: 100);
        listener.Dispose();
        var length = backing.ToArray().Length;

        var ex = Record.Exception(() =>
        {
            listener.Dispose();
            PhotoReviewPerf.Log.PreloadPaused(1, 2);
        });

        Assert.Null(ex);
        Assert.Equal(length, backing.ToArray().Length);
    }

    [Fact(DisplayName = "TryStartFromEnvironment returns null and leaves nothing behind when the trace path is a file, blank or invalid")]
    public void TryStartFromEnvironment_BadPaths_ReturnNull()
    {
        var temp = Path.Combine(Path.GetTempPath(), "PhotoReview-PerfEnv-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var asFile = Path.Combine(temp, "not-a-dir");
        File.WriteAllText(asFile, "x");
        var previous = Environment.GetEnvironmentVariable("PHOTOREVIEW_PERF_TRACE");
        try
        {
            foreach (var value in new[] { null, "", "   ", asFile, Path.Combine(asFile, "sub"), "Z:\\definitely\\missing\\drive\\|invalid" })
            {
                Environment.SetEnvironmentVariable("PHOTOREVIEW_PERF_TRACE", value);
                using var listener = PerfCsvListener.TryStartFromEnvironment();
                Assert.Null(listener);
            }
            Assert.Equal(["not-a-dir"], Directory.GetFileSystemEntries(temp).Select(Path.GetFileName));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PHOTOREVIEW_PERF_TRACE", previous);
            Directory.Delete(temp, recursive: true);
        }
    }
}

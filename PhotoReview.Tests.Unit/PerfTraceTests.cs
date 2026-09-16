using System.Diagnostics;
using System.IO;
using System.Linq;
using PhotoReview.App.Diagnostics;

namespace PhotoReview.Tests.Unit;

/// <summary>
/// D03: PhotoReviewPerf EventSource + PerfCsvListener. Mutates the PHOTOREVIEW_PERF_TRACE
/// environment variable, so these run in the "GlobalState" collection (no parallel siblings).
/// </summary>
[Collection("GlobalState")]
public sealed class PerfTraceTests : IDisposable
{
    private readonly string? _previousTrace = Environment.GetEnvironmentVariable("PHOTOREVIEW_PERF_TRACE");
    private readonly TempRoot _root = new("perf-trace");

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PHOTOREVIEW_PERF_TRACE", _previousTrace);
        _root.Dispose();
    }

    private static string[] ReadLines(string path) => File.ReadAllLines(path);

    [Fact(DisplayName = "No PHOTOREVIEW_PERF_TRACE means no listener and no files")]
    public void NoEnvironmentVariableMeansNoListenerAndNoFiles()
    {
        Environment.SetEnvironmentVariable("PHOTOREVIEW_PERF_TRACE", null);

        var listener = PerfCsvListener.TryStartFromEnvironment();

        Assert.Null(listener);
        Assert.Empty(Directory.GetFiles(_root.Path, "*", SearchOption.AllDirectories));
    }

    [Fact(DisplayName = "Blank PHOTOREVIEW_PERF_TRACE means no listener and no files")]
    public void BlankEnvironmentVariableMeansNoListenerAndNoFiles()
    {
        Environment.SetEnvironmentVariable("PHOTOREVIEW_PERF_TRACE", "   ");

        var listener = PerfCsvListener.TryStartFromEnvironment();

        Assert.Null(listener);
    }

    [Fact(DisplayName = "Setting PHOTOREVIEW_PERF_TRACE writes a CSV with header, commit line, and mapped columns")]
    public void SettingEnvironmentVariableWritesCsvWithMappedColumns()
    {
        var dir = _root.Dir("out");
        Environment.SetEnvironmentVariable("PHOTOREVIEW_PERF_TRACE", dir);

        var listener = PerfCsvListener.TryStartFromEnvironment();
        Assert.NotNull(listener);

        try
        {
            Assert.True(PhotoReviewPerf.Log.IsEnabled());

            PhotoReviewPerf.Log.ShowStart(1, 0, "Preview");
            PhotoReviewPerf.Log.Decode(1, "abc12345", 12.5, 800, true, false);
            PhotoReviewPerf.Log.PreloadPaused(85, 512);
        }
        finally
        {
            listener!.Dispose();
        }

        var file = Directory.GetFiles(dir, "perf-*.csv").Single();
        var lines = ReadLines(file);

        Assert.StartsWith("# commit=", lines[0]);
        Assert.Contains("qpcFrequency=" + Stopwatch.Frequency, lines[0]);
        Assert.Equal("utcTicks,qpcTicks,thread,event,nav,pathId,a,b,c,d,text", lines[1]);
        Assert.StartsWith("# dropped=", lines[^1]);

        var dataLines = lines[2..^1];
        Assert.Equal(3, dataLines.Length);

        var showStart = dataLines.Single(l => l.Contains(",ShowStart,"));
        var showStartCols = showStart.Split(',');
        Assert.Equal("1", showStartCols[4]);   // nav
        Assert.Equal("", showStartCols[5]);    // pathId
        Assert.Equal("0", showStartCols[6]);   // a = index
        Assert.Equal("Preview", showStartCols[10]); // text = mode

        var decode = dataLines.Single(l => l.Contains(",Decode,"));
        var decodeCols = decode.Split(',');
        Assert.Equal("1", decodeCols[4]);          // nav
        Assert.Equal("abc12345", decodeCols[5]);   // pathId
        Assert.Equal("12.5", decodeCols[6]);       // a = ms
        Assert.Equal("800", decodeCols[7]);        // b = targetWidth
        Assert.Equal("1", decodeCols[8]);          // c = downscaled
        Assert.Equal("0", decodeCols[9]);          // d = fallback

        var preloadPaused = dataLines.Single(l => l.Contains(",PreloadPaused,"));
        var preloadCols = preloadPaused.Split(',');
        Assert.Equal("", preloadCols[4]);  // nav (absent)
        Assert.Equal("", preloadCols[5]);  // pathId (absent)
        Assert.Equal("85", preloadCols[6]);  // a = loadPercent
        Assert.Equal("512", preloadCols[7]); // b = availableMb
    }

    [Fact(DisplayName = "PathId is stable and case-insensitive")]
    public void PathIdIsStableAndCaseInsensitive()
    {
        var file = _root.File("Sample.JPG", 1, 2, 3);

        var first = PhotoReviewPerf.PathId(file);
        var second = PhotoReviewPerf.PathId(file);
        var upper = PhotoReviewPerf.PathId(file.ToUpperInvariant());
        var lower = PhotoReviewPerf.PathId(file.ToLowerInvariant());

        Assert.Equal(8, first.Length);
        Assert.Equal(first, second);
        Assert.Equal(first, upper);
        Assert.Equal(first, lower);
    }

    [Fact(DisplayName = "Listener does not block under a 200k-event burst and accounts for every event")]
    public void ListenerDoesNotBlockUnderBurstAndAccountsForEveryEvent()
    {
        var dir = _root.Dir("burst");
        Environment.SetEnvironmentVariable("PHOTOREVIEW_PERF_TRACE", dir);

        var listener = PerfCsvListener.TryStartFromEnvironment();
        Assert.NotNull(listener);

        const int total = 200_000;
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < total; i++)
        {
            PhotoReviewPerf.Log.KeyInput(0, "x", 0.0);
        }
        listener!.Dispose();
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"Took {sw.Elapsed}");

        var file = Directory.GetFiles(dir, "perf-*.csv").Single();
        var lines = ReadLines(file);
        var droppedLine = lines[^1];
        Assert.StartsWith("# dropped=", droppedLine);
        var dropped = long.Parse(droppedLine["# dropped=".Length..]);

        var dataLines = lines[2..^1].Length;
        Assert.Equal(total, dataLines + dropped);
    }
}

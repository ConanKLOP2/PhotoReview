using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PhotoReview.PerfAnalysis;

namespace PhotoReview.Integration.Tests;

/// <summary>Fast (default-gate) tests for the `--perf-analyze` report: dropped-row honesty, group ordering,
/// Markdown escaping and N/A formatting in rule evidence. Uses a tiny inline CSV, not the sample fixture.</summary>
public sealed class PerfAnalyzeReportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PhotoReview-PerfReport-Tests-" + Guid.NewGuid().ToString("N"));

    public PerfAnalyzeReportTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private const string Header = "# commit=test diag= qpcFrequency=1000000\nutcTicks,qpcTicks,thread,event,nav,pathId,a,b,c,d,text\n";

    // One complete RamHit-style navigation (ShowStart .. Presented final) so the group has a percentile.
    private static string OneNav(long nav) =>
        $"1,{1000 * nav},1,ShowStart,{nav},,0,,,,Preview\n" +
        $"2,{1000 * nav + 500},1,Presented,{nav},,1,,,,final\n";

    private string WriteRun(string cell, string csvBody, string? sessionJson = null)
    {
        var dir = Path.Combine(_root, cell);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "perf-1.csv"), csvBody);
        if (sessionJson is not null) File.WriteAllText(Path.Combine(dir, "session.json"), sessionJson);
        return dir;
    }

    [Fact]
    public async Task DroppedRowsAreSurfacedInMarkdownAndJson()
    {
        WriteRun("a", Header + OneNav(1) + "# dropped=5\n", "{\"scenario\":\"S2\",\"mode\":\"Preview\",\"condition\":\"warm\"}");

        var result = await PerfAnalyze.RunAsync(_root, rulesPath: null);

        Assert.Equal(5, result.Groups.Single().Summary.DroppedRows);
        var md = File.ReadAllText(result.SummaryMdPath);
        Assert.Contains("dropped 5", md);
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(result.SummaryJsonPath));
        Assert.Equal(5, doc.RootElement.GetProperty("groups")[0].GetProperty("droppedRows").GetInt64());
    }

    [Fact]
    public async Task NoDroppedRowsMeansNoWarning()
    {
        WriteRun("a", Header + OneNav(1), "{\"scenario\":\"S2\",\"mode\":\"Preview\",\"condition\":\"warm\"}");

        var result = await PerfAnalyze.RunAsync(_root, rulesPath: null);

        Assert.DoesNotContain("dropped", File.ReadAllText(result.SummaryMdPath), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GroupsAreOrderedByWorkerCountNumericallyNotAsText()
    {
        foreach (var workers in new[] { 10, 2 })
            WriteRun($"w{workers}", Header + OneNav(1),
                $"{{\"scenario\":\"S2\",\"mode\":\"Preview\",\"condition\":\"warm\",\"preloadWorkers\":{workers}}}");

        var result = await PerfAnalyze.RunAsync(_root, rulesPath: null);

        Assert.Equal([2, 10], result.Groups.Select(g => g.Summary.Key.PreloadWorkers!.Value).ToArray());
    }

    [Fact]
    public async Task PipeInAGroupNameIsEscapedSoTheTableKeepsItsColumns()
    {
        WriteRun("a", Header + OneNav(1), "{\"scenario\":\"S2|x\",\"mode\":\"Preview\",\"condition\":\"warm\"}");

        var result = await PerfAnalyze.RunAsync(_root, rulesPath: null);

        var md = File.ReadAllText(result.SummaryMdPath);
        Assert.Contains(@"S2\|x/Preview/warm", md);
        Assert.DoesNotContain("| S2|x/", md);
    }

    [Fact]
    public void RUiEvidenceShowsNaForAnAbsentRamHitP95()
    {
        var r = PerfRules.EvaluateRUi(RulesConfig.Default(), [], double.NaN, frameTimeP95Ms: 5);

        Assert.DoesNotContain("NaN", r.Evidence, StringComparison.Ordinal);
        Assert.Contains("N/A", r.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void RFolderEvidenceShowsNaForMissingT1AndT3()
    {
        var gens = new[] { new FolderGenSummary { Gen = 3, T2Ms = 2500 } };

        var r = PerfRules.EvaluateRFolder(RulesConfig.Default(), gens);

        Assert.True(r.Triggered);
        Assert.Contains("T1 catalogReady=N/A", r.Evidence, StringComparison.Ordinal);
        Assert.Contains("=N/A;", r.Evidence, StringComparison.Ordinal);
    }
}

/// <summary>KeyInput to ShowStart matching must equal the straightforward definition (nearest KeyInput on the same thread at or
/// before ShowStart, within 500 ms, first in file order among equal timestamps) whatever the row order, threads and ties.</summary>
public sealed class PerfAnalyzeKeyInputMatchTests
{
    private static PerfRow Row(long qpc, int thread, string ev, string nav, string a, string text = "") =>
        new(0, qpc, thread, ev, nav, "", a, "", "", "", text);

    [Fact]
    public void KeyInputMatchingAgreesWithTheBruteForceDefinition()
    {
        var rnd = new Random(12345);
        var rows = new List<PerfRow>();
        var keyInputs = new List<PerfRow>();
        for (var i = 0; i < 300; i++)
        {
            // Frequency 1 MHz: 1000 ticks = 1 ms. Timestamps collide often (multiples of 500) and arrive out of order.
            var k = Row(rnd.Next(0, 400) * 500L, rnd.Next(1, 4), "KeyInput", "", (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
            keyInputs.Add(k);
            rows.Add(k);
        }
        var starts = new List<PerfRow>();
        for (var n = 1; n <= 200; n++)
        {
            var s = Row(rnd.Next(0, 500) * 500L, rnd.Next(1, 4), "ShowStart", n.ToString(System.Globalization.CultureInfo.InvariantCulture), "0", "Preview");
            starts.Add(s);
            rows.Add(s);
            rows.Add(Row(s.QpcTicks + 100, s.Thread, "Presented", s.Nav, "1", "final"));
        }
        var file = new PerfCsvFile
        {
            Path = "synthetic", CommitVersion = "x", DiagFlags = new Dictionary<string, string>(),
            QpcFrequency = 1_000_000, DroppedRows = 0, Rows = rows,
        };

        var analysis = PerfAnalyzeNavBuilder.Build(file);

        var matched = 0;
        foreach (var nav in analysis.Navs)
        {
            var start = starts.Single(s => s.Nav == nav.Nav.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var expected = keyInputs
                .Where(k => k.Thread == start.Thread && k.QpcTicks <= start.QpcTicks)
                .Where(k => (start.QpcTicks - k.QpcTicks) * 1000.0 / 1_000_000 <= 500.0)
                .OrderByDescending(k => k.QpcTicks)
                .FirstOrDefault();
            Assert.Equal(expected?.ANum, nav.TInputMs);
            if (expected is not null) matched++;
        }
        Assert.InRange(matched, 20, 199); // the scenario exercises both matching and non-matching navs
    }
}

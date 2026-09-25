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
